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
using SLS4All.Compact.Graphics;
using SLS4All.Compact.Helpers;
using SLS4All.Compact.Movement;
using SLS4All.Compact.Nesting;
using SLS4All.Compact.Power;
using SLS4All.Compact.Printing;
using SLS4All.Compact.Slicing;
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
        Forward<ICodePlotter>(builder.Services);
        // The plugin's PluginReplacements registration substitutes ICodePlotter
        // with LoggingCodePlotter. The parent container holds the LoggingCodePlotter
        // singleton under both keys; forwarding it under its concrete type lets
        // the new /plotter endpoints access decorator-specific APIs.
        Forward<LoggingCodePlotter>(builder.Services);
        // INestingService holds the live in-memory nesting state for the current
        // job (instance list + transforms + bounds). Source for /job/current/parts.
        Forward<INestingService>(builder.Services);
        // IPrintingService is the *during-print* source: GetPrintingObjectStates()
        // returns one PrintingObjectState per object actively being printed
        // (id, name, hash, transform, isExcluded), and ExcludeObject(id, bool)
        // is the mid-print include/exclude knob. The id matches the plotter's
        // CodePlotterMarkedObject.Id, so dashboard overlays correlate directly.
        Forward<IPrintingService>(builder.Services);

        _app = builder.Build();
        _app.UseDeveloperExceptionPage(); // surface child-Kestrel exceptions in 500 response body
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

        // ICodePlotter — accumulates the laser exposure mask for the current
        // layer as raster commands stream through the firmware. This is the
        // authoritative "where has the laser been" source; CurrentPosition.X/Y
        // do NOT track raster moves. Version increments as the plotter receives
        // new commands. currentLayer / currentCmdIdx / buildEpoch come from the
        // LoggingCodePlotter decorator; clients use them to seed the command
        // backfill query (/plotter/layer/{n}/commands?since=…) and detect
        // plugin restarts (buildEpoch changes when Clear(beginPrint=true) fires).
        app.MapGet("/plotter/info", (ICodePlotter plotter, LoggingCodePlotter logger, IMovementClient movement) =>
        {
            var s = logger.GetState();
            return Timed(new
            {
                width = plotter.Width,
                height = plotter.Height,
                version = plotter.Version,
                layerCount = plotter.LayerCount,
                isEmpty = plotter.IsEmpty,
                currentLayer = s.CurrentLayer,
                currentCmdIdx = s.CurrentCmdIdx,
                buildEpoch = s.BuildEpoch,
                maxXY = movement.MaxXY,
            });
        });

        // Full mask as float[]. Length = width * height. Polling cadence is up
        // to the client — version field lets the client skip refetches when
        // nothing has changed. Caller-supplied buffer is created fresh per
        // request; firmware reuses internal buffers across calls.
        app.MapGet("/plotter/mask", (ICodePlotter plotter) =>
        {
            float[] buffer = null!;
            var (w, h) = plotter.GetMask(ref buffer);
            return Timed(new
            {
                width = w,
                height = h,
                version = plotter.Version,
                values = buffer,
            });
        });

        // Per-layer command backfill. Returns a slice of the LoggingCodePlotter's
        // in-memory ring buffer from sinceIdx onward. Used by clients joining
        // mid-print to seed their canvas before subscribing to the WS stream.
        // Layers are retained LRU (default 8); requests for evicted layers
        // return an empty list — clients should fall back to the Postgres
        // backfill route on the recorder (/api/builds/{id}/plotter/commands).
        app.MapGet("/plotter/layer/{idx:int}/commands", (int idx, int? since, LoggingCodePlotter logger) =>
            Timed(new
            {
                layer = idx,
                sinceCmdIdx = since ?? 0,
                commands = logger.SnapshotLayer(idx, since ?? 0),
            }));

        // WebSocket stream of LoggingCodePlotter command frames. Subscribes
        // to the decorator's fan-out; each Process(CodeCommand) call emits one
        // frame here. Bounded channel cap 1024 with DropOldest — slow clients
        // lose middle-history but the GET-layer backfill above lets them
        // recover. Frame: {respondedAt, data: {buildEpoch, layer, idx, ts,
        // op, x, y, laser, speed, raw}}.
        app.MapGet("/plotter/commands/stream", StreamPlotterCommandsAsync);

        // Per-object markers for the current layer as the plotter sees them.
        // Each entry is {id, outline}; outline is a polygon in plotter raster
        // coordinates (origin top-left, units = plotter pixels — divide by
        // (width, height) and multiply by chamber size to get mm). The id is
        // an opaque integer the plotter assigns per print; pair it with
        // /plotter/objects/{id}/mask.png to fetch a per-object raster, and
        // with /job/current/parts to recover human-readable names (the link
        // is by emission order — first marked object = first nested instance).
        app.MapGet("/plotter/objects", (ICodePlotter plotter) =>
        {
            var objs = plotter.GetMarkedObjects();
            return Timed(new
            {
                version = plotter.Version,
                width = plotter.Width,
                height = plotter.Height,
                objects = objs.Select(o => new
                {
                    id = o.Id,
                    outline = o.RelativeOutline.Select(v => new[] { v.X, v.Y }).ToArray(),
                }).ToArray(),
            });
        });

        // PNG mask of a single marked object on the current layer. White-on-
        // transparent by default; pass ?on=RRGGBBAA and ?off=RRGGBBAA (8-hex-
        // digit RGBA) to override. Returns 404 if the id isn't present in the
        // current layer (e.g. object not yet emitted, or evicted by a Clear).
        app.MapGet("/plotter/objects/{id:int}/mask.png", (
            int id, string? on, string? off, ICodePlotter plotter) =>
        {
            var onColor = ParseRgba(on) ?? new RgbaB(255, 255, 255, 255);
            var offColor = ParseRgba(off) ?? new RgbaB(0, 0, 0, 0);
            var mask = plotter.CreateMarkedObjectMask(id, onColor, offColor);
            if (mask.IsEmpty) return Results.NotFound();
            return Results.Bytes(mask.Data.ToArray(), mask.ContentType ?? "image/png");
        });

        // Projection of INestingService.GetInstances() — the live in-memory
        // nesting state for the current job. Each instance carries the part
        // name (STL filename), mesh content hash, axis-aligned bounds, place-
        // ment transform (position + rotation/quaternion + scale), and the
        // firmware-assigned RGBA color. Use the chamber.size{X,Y,Z} to scale
        // a 2D top-down view. This is the right source for a parts-list card
        // because it includes names (the plotter's marked objects don't).
        app.MapGet("/job/current/parts", (INestingService nesting) =>
        {
            var instances = nesting.GetInstances();
            var dim = nesting.NestingDim;
            return Timed(new
            {
                chamber = dim is null ? null : new
                {
                    sizeX = dim.SizeX,
                    sizeY = dim.SizeY,
                    sizeZ = dim.SizeZ,
                },
                chamberStep = nesting.ChamberStep,
                instances = instances.Select(i =>
                {
                    var t = i.TransformState;
                    var b = i.Mesh;
                    return new
                    {
                        index = i.Index,
                        name = i.Name,
                        meshHash = b?.Hash,
                        bounds = b is null ? null : new
                        {
                            center = new[] { b.Bounds.Center.X, b.Bounds.Center.Y, b.Bounds.Center.Z },
                            size = new[] { b.Bounds.Size.X, b.Bounds.Size.Y, b.Bounds.Size.Z },
                        },
                        transform = new
                        {
                            position = new[] { t.Position.X, t.Position.Y, t.Position.Z },
                            rotation = new[] { t.Rotation.X, t.Rotation.Y, t.Rotation.Z },
                            quaternion = new[] { t.Quaternion.X, t.Quaternion.Y, t.Quaternion.Z, t.Quaternion.W },
                            scale = new[] { t.Scale.X, t.Scale.Y, t.Scale.Z },
                        },
                        color = new { r = i.Color.R, g = i.Color.G, b = i.Color.B, a = i.Color.A },
                        isOverlapping = i.IsOverlapping,
                        inset = i.Inset,
                        margin = i.Margin,
                        nestingPriority = i.NestingPriority,
                    };
                }).ToArray(),
            });
        });

        // Live per-object state during an active print. Each entry corresponds
        // to one printing object the slicer/plotter is aware of: id (int, the
        // same number used by /plotter/objects), name (STL filename), mesh
        // hash, world-space 4x4 transform (row-major), and the current
        // isExcluded flag. Returns an empty list when no print is active.
        app.MapGet("/printing/objects", (IPrintingService printing) =>
        {
            var states = printing.GetPrintingObjectStates() ?? Array.Empty<PrintingObjectState>();
            return Timed(new
            {
                objects = states.Select(s =>
                {
                    var o = s.Object;
                    var m = o.Transform;
                    // Mesh-local axis-aligned bounds (NOT transformed). The
                    // dashboard 3D view applies `transform` separately to place
                    // each instance, so local bounds + transform = world AABB.
                    // Null when the live PrintingObject doesn't carry a Mesh
                    // (shouldn't happen during an active print, but defensive).
                    var bounds = o.Mesh?.GetBounds();
                    return new
                    {
                        id = o.Id,
                        name = o.Name,
                        hash = o.Hash,
                        transform = new[]
                        {
                            new[] { m.M11, m.M12, m.M13, m.M14 },
                            new[] { m.M21, m.M22, m.M23, m.M24 },
                            new[] { m.M31, m.M32, m.M33, m.M34 },
                            new[] { m.M41, m.M42, m.M43, m.M44 },
                        },
                        bounds = bounds is null ? null : new
                        {
                            center = new[] { bounds.Value.Center.X, bounds.Value.Center.Y, bounds.Value.Center.Z },
                            size = new[] { bounds.Value.Size.X, bounds.Value.Size.Y, bounds.Value.Size.Z },
                        },
                        isExcluded = s.IsExcluded,
                    };
                }).ToArray(),
            });
        });

        // Mid-print include/exclude toggle. POST body: {"excluded": true|false}.
        // Returns the post-mutation state. ExcludeObject is a no-op during
        // phases where exclusion isn't meaningful (e.g. before "Layers" phase);
        // we surface that via the returned isExcluded — the firmware is the
        // source of truth, not the request body.
        app.MapPost("/printing/objects/{id:int}/exclude", (
            int id, ExcludeRequest body, IPrintingService printing) =>
        {
            printing.ExcludeObject(id, body.Excluded);
            return Timed(new { id, isExcluded = printing.IsExcludedObject(id) });
        });

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
        app.MapGet("/movement/position/stream", StreamPositionAsync);

        // WebSocket stream of raw IR thermal matrix frames, subscribed to the
        // firmware's StateChangedHighFrequency event (~6 Hz native, camera-bound).
        // Use ?hz=N to decimate (clamped 1..60). Each frame is
        // {respondedAt, data: { timestamp, width, height, values }}.
        // Events without a BedMatrix (null on the TemperatureState) are skipped.
        app.MapGet("/temperature/bedmatrix/stream", StreamBedMatrixAsync);
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
        // Use the Task overload of AddHandler — the ValueTask overload may not exist
        // on older firmware builds that the plugin gets loaded into.
        Func<PositionHighFrequency, CancellationToken, Task> handler = (arg, _) =>
        {
            channel.Writer.TryWrite(arg);
            return Task.CompletedTask;
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

    private static async Task StreamPlotterCommandsAsync(
        HttpContext ctx,
        LoggingCodePlotter plotterLogger,
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

        // Bounded channel, DropOldest. Commands can burst much higher than the
        // ~1 kHz position stream (a single layer can emit tens of thousands of
        // MOVE_XY calls); cap 1024 + drop policy means slow WS clients lose
        // middle history but the GET /plotter/layer/{n}/commands?since=… backfill
        // lets the dashboard recover without firmware-side state.
        var channel = Channel.CreateBounded<CommandFrame>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // Optional decimation, mirrors StreamPositionAsync. Default 0 = pass
        // every command. Clamp 1..1000 — past that, downsample via the GET
        // backfill query instead.
        var minIntervalTicks = hz.HasValue
            ? Stopwatch.Frequency / Math.Clamp(hz.Value, 1, 1000)
            : 0L;
        long lastSentTicks = 0;

        plotterLogger.AddSubscriber(channel.Writer);

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var cmd))
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
                        data = cmd,
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
            plotterLogger.RemoveSubscriber(channel.Writer);
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

        // See StreamPositionAsync for why we use the Task overload here.
        Func<TemperatureState, CancellationToken, Task> handler = (state, _) =>
        {
            if (state.BedMatrix is not null)
                channel.Writer.TryWrite(state.BedMatrix);
            return Task.CompletedTask;
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

    // POST body for /printing/objects/{id}/exclude. ASP.NET Core minimal APIs
    // bind JSON request bodies to record types automatically; "excluded" maps
    // to the camelCase JSON key via the web-defaults serializer options.
    private sealed record ExcludeRequest(bool Excluded);

    // Parse a query-string RGBA hex (RRGGBBAA, 8 hex digits, leading '#' optional).
    // Returns null for null/empty/malformed input so callers can fall back to a
    // default — this is query-tuning sugar, not a validation surface.
    private static RgbaB? ParseRgba(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var h = s.StartsWith('#') ? s[1..] : s;
        if (h.Length != 8) return null;
        if (!byte.TryParse(h.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)) return null;
        if (!byte.TryParse(h.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)) return null;
        if (!byte.TryParse(h.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return null;
        if (!byte.TryParse(h.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var a)) return null;
        return new RgbaB(r, g, b, a);
    }
}
