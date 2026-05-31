using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SLS4All.Compact.Helpers;

namespace Inova.ApiPlugin;

public sealed class InovaApiPlugin : IHostedService, IConstructable
{
    private const int Port = 5001;

    private readonly ILogger<InovaApiPlugin> _logger;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public InovaApiPlugin(ILogger<InovaApiPlugin> logger)
    {
        _logger = logger;
        _listener.Prefixes.Add($"http://+:{Port}/");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _logger.LogInformation("Inova API plugin listening on http://+:{Port}/", Port);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        _logger.LogInformation("Inova API plugin stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (string.Equals(path, "/ping", StringComparison.Ordinal))
            {
                var bytes = Encoding.UTF8.GetBytes("pong");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request handler failed");
            try { ctx.Response.StatusCode = 500; } catch { /* response already started */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* response already closed */ }
        }
    }
}
