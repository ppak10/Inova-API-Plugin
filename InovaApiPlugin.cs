using System.Net.WebSockets;
using System.Text.Json;
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
        // the rendered thermal heatmap (existing firmware endpoint on port 80).
        app.MapGet("/temperature/current", (ITemperatureClient temperature)
            => Timed(new { entries = temperature.CurrentState.Entries }));

        // Combined snapshot of all Tier 1 telemetry. Same shape as the per-frame
        // payload of the /state/stream WebSocket, but as a one-shot HTTP GET.
        app.MapGet("/state/snapshot", (
                IMovementClient movement,
                ILightsClient lights,
                IPowerClient power,
                ITemperatureClient temperature)
            => Timed(CaptureSnapshot(movement, lights, power, temperature)));

        // WebSocket stream of combined telemetry. Default 10 Hz, ?hz= overrides
        // (clamped 1..100). Each frame is a JSON object with the same shape as
        // GET /state/snapshot. Loop exits when the client disconnects.
        app.Map("/state/stream", StreamStateAsync);
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

        var rate = Math.Clamp(hz ?? 10, 1, 100);
        var interval = TimeSpan.FromSeconds(1.0 / rate);

        // Hypothesis: the no-args AcceptWebSocketAsync() was synthesising a
        // SubProtocol = "default", causing the server to emit
        // `Sec-WebSocket-Protocol: default` in the 101 response — which RFC 6455
        // forbids when the client offered no subprotocol. Explicitly pass null
        // here to test that hypothesis; if the response still carries "default",
        // the source is deeper in the runtime/middleware, not our call.
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
