using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

        app.MapGet("/movement/position", (IMovementClient movement) => movement.CurrentPosition);

        app.MapGet("/lights/state", (ILightsClient lights) => new
        {
            isEnabled = lights.CurrentState.IsEnabled,
            lightCount = lights.LightCount,
        });

        app.MapGet("/power/current", (IPowerClient power) => power.CurrentState);

        // TemperatureState includes BedMatrix (IR thermal pixels, ~50 KB) we don't
        // want to ship over JSON on every poll. Use /api/bedmatrix/image/... for
        // the rendered thermal heatmap (existing firmware endpoint on port 80).
        app.MapGet("/temperature/current", (ITemperatureClient temperature)
            => new { entries = temperature.CurrentState.Entries });
    }
}
