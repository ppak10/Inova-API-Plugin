using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SLS4All.Compact.Configuration;
using SLS4All.Compact.Diagnostics;
using SLS4All.Compact.Movement;
using SLS4All.Compact.Power;
using SLS4All.Compact.Printer;

namespace Inova.ApiPlugin;

// Subclass of the firmware's McuMovementClient that intercepts MoveXY and
// SetLaser. Installed via [Application.PluginReplacements] — see install.sh.
//
// Why this exists: the slicer in SLS4All.Compact.Printing.dll bypasses the
// DI singleton ICodePlotter for per-command writes (it uses its own internal
// ImageCodePlotterBase instance), so LoggingCodePlotter.Process never fires
// during raster. The MCU motion path however MUST go through IMovementClient
// because that is the only path to the MCU queue — so by overriding here we
// see every commanded galvo move with absolute (x, y) coords in firmware
// units (0..MaxXY).
//
// CompactServiceCollectionExtensions.AddAsImplementationAndInterfaces detects
// that this is a subclass of McuMovementClient and registers both — anything
// resolving the concrete McuMovementClient type still works.
public class LoggingMovementClient : McuMovementClient
{
    private readonly LoggingCodePlotter _hub;
    private readonly ILogger<LoggingMovementClient> _interceptLogger;

    public LoggingMovementClient(
        ILogger<McuMovementClient> logger,
        IOptionsMonitor<McuMovementClientOptions> options,
        IOptionsWriter<MovementClientBaseSavedOptions> savedOptions,
        McuPrinterClient printerClient,
        McuPowerClient powerClient,
        IThreadStackTraceDumper dumper,
        IPrinterMemoryManager memoryManager,
        LoggingCodePlotter hub,
        ILogger<LoggingMovementClient> interceptLogger)
        : base(logger, options, savedOptions, printerClient, powerClient, dumper, memoryManager)
    {
        _hub = hub;
        _interceptLogger = interceptLogger;
        _interceptLogger.LogInformation(
            "LoggingMovementClient intercepting MoveXY/SetLaser, fanning out via LoggingCodePlotter");
    }

    public override ValueTask MoveXY(
        double x,
        double y,
        bool relative,
        double? speed = null,
        bool clamp = false,
        bool hidden = false,
        IPrinterClientCommandContext? context = null,
        CancellationToken cancel = default)
    {
        // Forward first. base.MoveXY queues the move synchronously inside a
        // master queue lock and updates _posX/_posY before returning the
        // ValueTask, so CurrentPosition after the call reflects the new
        // absolute position regardless of `relative`.
        var t = base.MoveXY(x, y, relative, speed, clamp, hidden, context, cancel);
        var pos = CurrentPosition;
        _hub.Emit(
            op: "MOVE_XY",
            x: (float)pos.X,
            y: (float)pos.Y,
            laser: null,
            speed: speed is null ? null : (float)speed.Value,
            raw: null);
        return t;
    }

    public override ValueTask SetLaser(
        double value,
        bool noCompensation = false,
        IPrinterClientCommandContext? context = null,
        CancellationToken cancel = default)
    {
        var t = base.SetLaser(value, noCompensation, context, cancel);
        _hub.Emit(
            op: "SET_LASER",
            x: null,
            y: null,
            laser: (float)value,
            speed: null,
            raw: null);
        return t;
    }
}
