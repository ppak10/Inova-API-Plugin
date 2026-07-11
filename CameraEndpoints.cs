using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SLS4All.Compact.Camera;
using SLS4All.Compact.IO;

namespace Inova.ApiPlugin;

/// <summary>
/// WebSocket stream of raw JPEG camera frames from <see cref="ICameraClient"/>.
///
/// The firmware spawns <c>rpicam-vid --codec mjpeg</c> as a child process and
/// reads individual JPEG frames from its stdout into the <c>ICameraClient</c>
/// singleton. Our plugin subscribes to <c>ICameraClient.Captured</c> — the
/// same <c>AsyncEvent&lt;MimeData&gt;</c> pattern used by temperature — and
/// pushes each frame as a binary WebSocket message.
///
/// Clients receive raw JPEG bytes (not base64, not JSON), which the browser
/// renders via <c>URL.createObjectURL(new Blob([bytes], {type:'image/jpeg'}))</c>.
/// This is significantly more efficient than the previous HTTP-poll approach:
/// the frame is pushed once to all connected clients from a single upstream
/// subscription, instead of each client independently re-fetching from the
/// firmware on its own timer.
///
/// GET /camera/stream
/// GET /camera/stream?hz=N   — client-side decimation (1..12, default full rate)
/// </summary>
internal static class CameraEndpoints
{
    public static void MapCameraEndpoints(this WebApplication app)
    {
        app.MapGet("/camera/stream", StreamCameraAsync);
    }

    private static async Task StreamCameraAsync(
        HttpContext ctx,
        ICameraClient camera,
        int? hz)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("WebSocket upgrade required").ConfigureAwait(false);
            return;
        }

        // Same SubProtocol = null workaround as the other stream endpoints — see
        // comment in StreamStateAsync for the full explanation.
        var acceptContext = new WebSocketAcceptContext { SubProtocol = null };
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync(acceptContext)
            .ConfigureAwait(false);
        var cancel = ctx.RequestAborted;

        // Camera native rate is 12 fps (set in rpicam-vid --framerate 12).
        // Clamp to 1..12; 0 or absent = full rate.
        var minIntervalTicks = hz.HasValue
            ? System.Diagnostics.Stopwatch.Frequency / Math.Clamp(hz.Value, 1, 12)
            : 0L;
        long lastSentTicks = 0;

        // Bounded channel, DropOldest. MimeData bytes are copied into the
        // channel immediately so Return() can be called before any await.
        // Capacity 4: at 12 fps the WebSocket send is well under 83 ms, but
        // a slow client should drop old frames rather than back-pressure the
        // camera capture loop.
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // Subscribe to ICameraClient.Captured. Adding a handler causes
        // LazyDataGenerator.CheckStartGenerator() to activate the camera
        // capture thread if it wasn't already running. Removing it on
        // disconnect allows the camera to idle when no clients are watching.
        Func<MimeData, CancellationToken, Task> handler = (frame, _) =>
        {
            // Copy bytes out of the MimeData before returning it to the pool.
            // frame.Data is Memory<byte> backed by a rented buffer; we must
            // not hold a reference after Return() is called.
            var bytes = frame.Data.ToArray();
            if (frame.IsReturnable) frame.Return();
            channel.Writer.TryWrite(bytes);
            return Task.CompletedTask;
        };
        camera.Captured.AddHandler(handler);

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var bytes))
                {
                    if (socket.State != WebSocketState.Open) return;

                    if (minIntervalTicks > 0)
                    {
                        var now = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (now - lastSentTicks < minIntervalTicks) continue;
                        lastSentTicks = now;
                    }

                    // Send raw JPEG as a binary frame. Browser creates an
                    // ObjectURL from the received Blob — no base64 overhead.
                    await socket.SendAsync(
                        bytes,
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancel).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnect — normal */ }
        finally
        {
            camera.Captured.RemoveHandler(handler);
        }
    }
}
