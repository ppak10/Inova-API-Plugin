using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SLS4All.Compact.Helpers;

namespace Inova.ApiPlugin;

public sealed class InovaApiPlugin : IHostedService, IConstructable
{
    private const int Port = 5001;

    private readonly ILogger<InovaApiPlugin> _logger;
    private WebApplication? _app;

    public InovaApiPlugin(ILogger<InovaApiPlugin> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // The firmware's SimpleFileLogger already covers our class's logs; suppress
        // the child host's default providers to avoid double-logging to console.
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.ListenAnyIP(Port));

        _app = builder.Build();
        MapEndpoints(_app);

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

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/ping", () => "pong");
    }
}
