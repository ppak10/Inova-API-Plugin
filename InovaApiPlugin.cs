using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SLS4All.Compact.Helpers;
using SLS4All.Compact.Movement;
using SLS4All.Compact.Power;
using SLS4All.Compact.Temperature;

namespace Inova.ApiPlugin;

public sealed class InovaApiPlugin : IHostedService, IConstructable
{
    private const int Port = 5001;
    private const string Version = "0.1.0";

    // Web-defaults (camelCase, case-insensitive) so WS frames match GET responses.
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _parent;
    private readonly ILogger<InovaApiPlugin> _logger;
    private DateTimeOffset _startedAt;
    private WebApplication? _app;

    public InovaApiPlugin(IServiceProvider parent, ILogger<InovaApiPlugin> logger)
    {
        _parent = parent;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _startedAt = DateTimeOffset.UtcNow;

        var builder = WebApplication.CreateSlimBuilder();
        // Firmware's SimpleFileLogger covers our class's logs; silence the child
        // host's default providers to avoid double-logging to console.
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.ListenAnyIP(Port));

        // Forward firmware services into the child container so endpoint handlers
        // can declare them as parameters. Same instance the firmware uses.
        Forward<IMovementClient>(builder.Services);
        Forward<ILightsClient>(builder.Services);
        Forward<IPowerClient>(builder.Services);
        Forward<ITemperatureClient>(builder.Services);

        _app = builder.Build();
        _app.UseWebSockets();
        MapEndpoints(_app, _startedAt);

        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Inova API plugin listening on http://+:{Port}/", Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
        _logger.LogInformation("Inova API plugin stopped");
    }

    private void Forward<T>(IServiceCollection services) where T : class
        => services.AddSingleton(_parent.GetRequiredService<T>());

    private static void MapEndpoints(WebApplication app, DateTimeOffset startedAt)
    {
        app.MapGet("/ping", () => "pong");

        app.MapGet("/info", () => new
        {
            plugin = "Inova.ApiPlugin",
            version = Version,
            listenPort = Port,
            startedAtUtc = startedAt,
            uptimeSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
        });

        app.MapGet("/movement/position", (IMovementClient movement)
            => Timed(movement.CurrentPosition));

        app.MapGet("/lights/state", (ILightsClient lights) => Timed(new
        {
            isEnabled = lights.CurrentState.IsEnabled,
            lightCount = lights.LightCount,
        }));

        app.MapGet("/power/current", (IPowerClient power) => Timed(power.CurrentState));

        // TemperatureState includes BedMatrix (IR thermal pixels, ~50 KB) we don't
        // want to ship over JSON on every poll. Use /api/bedmatrix/image/... for
        // the rendered thermal heatmap (existing firmware endpoint on port 80),
        // or /temperature/bedmatrix below for the raw float matrix.
        app.MapGet("/temperature/current", (ITemperatureClient temperature)
            => Timed(new { entries = temperature.CurrentState.Entries }));

        // Raw IR thermal pixel matrix as float32 values. Width/Height come from
        // the underlying ITemperatureCamera. Returns data:null when no matrix is
        // currently available (no thermal camera or not yet sampled).
        app.MapGet("/temperature/bedmatrix", (ITemperatureClient temperature)
            => Timed(temperature.CurrentState.BedMatrix));

        // Combined snapshot of all Tier 1 telemetry. Same shape as the per-frame
        // payload of the /state/stream WebSocket, but as a one-shot HTTP GET.
        app.MapGet("/state/snapshot", (
                IMovementClient movement,
                ILightsClient lights,
                IPowerClient power,
                ITemperatureClient temperature)
            => Timed(CaptureSnapshot(movement, lights, power, temperature)));

        // WebSocket stream of combined telemetry. Default 100 Hz, ?hz= overrides
        // (clamped 1..100). Each frame is a JSON object with the same shape as
        // GET /state/snapshot. Loop exits when the client disconnects.
        app.Map("/state/stream", StreamStateAsync);

        // WebSocket stream of high-frequency raw position events from the firmware's
        // PositionChangedHighFrequency AsyncEvent (~1 kHz native). Use ?hz=N to
        // decimate to at most N sends/sec (clamped 1..1000). Each frame is
        // {respondedAt, data: { x, y, z1, z2, r, hasHomed }}. Unlike /state/stream,
        // this is event-driven, not timer-driven — frames are emitted as the
        // firmware's internal motion loop publishes them.
        app.Map("/movement/position/stream", StreamPositionAsync);

        // WebSocket stream of raw IR thermal matrix frames, subscribed to the
        // firmware's StateChangedHighFrequency event (~6 Hz native, camera-bound).
        // Use ?hz=N to decimate (clamped 1..60). Each frame is
        // {respondedAt, data: { timestamp, width, height, values }}.
        // Events without a BedMatrix (null on the TemperatureState) are skipped.
        app.Map("/temperature/bedmatrix/stream", StreamBedMatrixAsync);
    }

    private static async Task StreamStateAsync(
        HttpContext ctx,
        IMovementClient movement,
        ILightsClient lights,
        IPowerClient power,
        ITemperatureClient temperature,
        int? hz)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("WebSocket upgrade required").ConfigureAwait(false);
            return;
        }

        var rate = Math.Clamp(hz ?? 100, 1, 100);
        var interval = TimeSpan.FromSeconds(1.0 / rate);

        // The parameterless AcceptWebSocketAsync() overload in ASP.NET Core 10
        // synthesises SubProtocol = "default" somewhere along the call chain,
        // causing the 101 response to carry `Sec-WebSocket-Protocol: default`.
        // RFC 6455 §4.2.2 forbids that header when the client offered no
        // subprotocol — strict clients (e.g. Python websockets) refuse the
        // connection. Passing an explicit context with SubProtocol = null
        // short-circuits the default.
        var acceptContext = new WebSocketAcceptContext { SubProtocol = null };
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync(acceptContext).ConfigureAwait(false);
        var cancel = ctx.RequestAborted;
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancel).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open) break;

                var frame = new
                {
                    respondedAt = DateTimeOffset.UtcNow,
                    data = CaptureSnapshot(movement, lights, power, temperature),
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, _jsonOptions);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancel)
                            .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* client disconnect — normal shutdown path */ }
    }

    private static async Task StreamPositionAsync(
        HttpContext ctx,
        IMovementClient movement,
        int? hz)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("WebSocket upgrade required").ConfigureAwait(false);
            return;
        }

        // Same SubProtocol = null workaround as /state/stream — see comment there.
        var acceptContext = new WebSocketAcceptContext { SubProtocol = null };
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync(acceptContext).ConfigureAwait(false);
        var cancel = ctx.RequestAborted;

        // Bounded channel with DropOldest: keep memory bounded and never block the
        // firmware's emit loop. If the WS client falls behind, older events are
        // dropped in favour of newer ones — continuous motion still resamples cleanly.
        var channel = Channel.CreateBounded<PositionHighFrequency>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // Optional decimation. minIntervalTicks=0 means "send every event the
        // firmware emits". When ?hz=N is set, skip events that arrive sooner than
        // 1/N seconds after the last send.
        var minIntervalTicks = hz.HasValue
            ? Stopwatch.Frequency / Math.Clamp(hz.Value, 1, 1000)
            : 0L;
        long lastSentTicks = 0;

        // Capture as a stable Func so RemoveHandler sees the same delegate identity.
        Func<PositionHighFrequency, CancellationToken, ValueTask> handler = (arg, _) =>
        {
            channel.Writer.TryWrite(arg);
            return ValueTask.CompletedTask;
        };
        movement.PositionChangedHighFrequency.AddHandler(handler);

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var ev))
                {
                    if (socket.State != WebSocketState.Open) return;

                    if (minIntervalTicks > 0)
                    {
                        var now = Stopwatch.GetTimestamp();
                        if (now - lastSentTicks < minIntervalTicks) continue;
                        lastSentTicks = now;
                    }

                    var frame = new
                    {
                        respondedAt = DateTimeOffset.UtcNow,
                        data = new
                        {
                            x = ev.Position.X,
                            y = ev.Position.Y,
                            z1 = ev.Position.Z1,
                            z2 = ev.Position.Z2,
                            r = ev.Position.R,
                            hasHomed = ev.HasHomed,
                        },
                    };
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, _jsonOptions);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancel)
                                .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnect — normal */ }
        finally
        {
            movement.PositionChangedHighFrequency.RemoveHandler(handler);
        }
    }

    private static async Task StreamBedMatrixAsync(
        HttpContext ctx,
        ITemperatureClient temperature,
        int? hz)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("WebSocket upgrade required").ConfigureAwait(false);
            return;
        }

        var acceptContext = new WebSocketAcceptContext { SubProtocol = null };
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync(acceptContext).ConfigureAwait(false);
        var cancel = ctx.RequestAborted;

        // Capacity 8 — these frames are large (~50 KB JSON); we never want to
        // buffer multiple. DropOldest keeps the freshest frame available.
        var channel = Channel.CreateBounded<TemperatureMatrix>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // The thermal camera reports at ~6 Hz natively, so the upper hz clamp is
        // 60 — well above what the underlying hardware can produce.
        var minIntervalTicks = hz.HasValue
            ? Stopwatch.Frequency / Math.Clamp(hz.Value, 1, 60)
            : 0L;
        long lastSentTicks = 0;

        Func<TemperatureState, CancellationToken, ValueTask> handler = (state, _) =>
        {
            if (state.BedMatrix is not null)
                channel.Writer.TryWrite(state.BedMatrix);
            return ValueTask.CompletedTask;
        };
        temperature.StateChangedHighFrequency.AddHandler(handler);

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var matrix))
                {
                    if (socket.State != WebSocketState.Open) return;

                    if (minIntervalTicks > 0)
                    {
                        var now = Stopwatch.GetTimestamp();
                        if (now - lastSentTicks < minIntervalTicks) continue;
                        lastSentTicks = now;
                    }

                    var frame = new
                    {
                        respondedAt = DateTimeOffset.UtcNow,
                        data = matrix,
                    };
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, _jsonOptions);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancel)
                                .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnect — normal */ }
        finally
        {
            temperature.StateChangedHighFrequency.RemoveHandler(handler);
        }
    }

    private static object CaptureSnapshot(
        IMovementClient movement,
        ILightsClient lights,
        IPowerClient power,
        ITemperatureClient temperature)
        => new
        {
            position = movement.CurrentPosition,
            lights = new { isEnabled = lights.CurrentState.IsEnabled, lightCount = lights.LightCount },
            power = power.CurrentState,
            temperature = new { entries = temperature.CurrentState.Entries },
        };

    // Wraps a data payload with the server's UTC wall-clock at JSON-serialize time.
    // Combined with per-entry `elapsedFromNow` fields where present, this lets a
    // client reconstruct sub-second wall-clock time for each underlying sample
    // (sample_wall_clock = respondedAt - elapsedFromNow). Endpoints whose data
    // carries no time semantics (e.g. /ping, /info) don't use this wrapper.
    private static object Timed<T>(T payload) => new
    {
        respondedAt = DateTimeOffset.UtcNow,
        data = payload,
    };
}
