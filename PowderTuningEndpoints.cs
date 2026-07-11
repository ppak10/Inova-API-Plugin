using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SLS4All.Compact.Movement;       // BeginLayerSetup, BedLevelingSetup, EndPrintSetup
using SLS4All.Compact.Printing;       // IPowderTuning, PowderTuning* commands, PrintingMode
using SLS4All.Compact.PrintSessions;  // PrintSetup

namespace Inova.ApiPlugin;

/// <summary>
/// REST API for the firmware's interactive powder-tuning session. The session
/// prints a 5×5 grid (25 patches), each sintered with different laser parameters,
/// to characterise a new powder material.
///
/// Commands are dispatched via one of two paths depending on the deployed firmware
/// ABI version:
///   - Older ABI: <c>IPowderTuning.ExecuteCommand</c> (separate DI service)
///   - Newer ABI: <c>IPrintingService.ExecutePowderTuningCommand</c> (on the main
///     printing service — resolved via reflection since the refs/ DLLs predate it)
///
/// The session MUST already be running (started from the firmware wizard at
/// /wizard/powder-tuning). There is no start endpoint here because
/// PrinterPowerSettings — required by IPrintingService.CreateSetup — is not yet
/// exposed via the plugin.
///
/// Typical agentic workflow:
///   1. Human starts the session via the firmware wizard.
///   2. GET  /powder-tuning/status     — confirm active, read gridDim.
///   3. POST /powder-tuning/bed-level  — level the powder bed.
///   4. POST /powder-tuning/layer      — spread a fresh layer.
///   5. POST /powder-tuning/surface    — reach surface temperature target.
///   6. For each patch 0..gridDim²-1:
///        POST /powder-tuning/params   { gridIndex }
///        POST /powder-tuning/print    { laser params }
///   7. POST /powder-tuning/stop       — end the session.
/// </summary>
internal static class PowderTuningEndpoints
{
    public static void MapPowderTuningEndpoints(this WebApplication app)
    {
        // ── status ────────────────────────────────────────────────────────────
        // Check whether a powder tuning session is currently active. Cheap to
        // poll. Returns key PrintSetup fields when active so clients can see
        // the configured grid dimension and laser baseline.
        app.MapGet("/powder-tuning/status", (IPrintingService printing) =>
        {
            var mode = printing.PrintingMode;
            var isActive = mode == PrintingMode.PowderTuning;
            SLS4All.Compact.PrintSessions.PrintSetup? setup = null;
            if (isActive)
            {
                try { setup = printing.RunningSetup; }
                catch { /* not printing — treat as idle */ }
            }
            return Timed(new
            {
                isActive,
                mode = mode.ToString(),
                phase = printing.PrintingPhase.ToString(),
                gridDim = setup?.PowderTuningGridDim,
                gridMargin = setup?.PowderTuningGridMargin,
                powderDepth = setup?.PowderTuningDepth,
                laserOnPercent = setup?.LaserOnPercent,
                totalEnergyDensityPercent = setup?.TotalEnergyDensityPercent,
                recoaterPasses = setup?.RecoaterPasses,
            });
        });

        // ── layer ─────────────────────────────────────────────────────────────
        // Add a fresh layer of powder (triggers the recoater).
        // Body: { temperatureTarget?, temperatureDelaySeconds?, sinteredVolumeFactor? }
        app.MapPost("/powder-tuning/layer", async (
            LayerRequest? body, IPrintingService printing, IPowderTuning? tuning,
            CancellationToken ct) =>
        {
            if (printing.PrintingMode != PrintingMode.PowderTuning)
                return Results.BadRequest(new { error = "no powder tuning session is active" });

            var cmd = new BeginLayerSetup
            {
                TemperatureTarget = body?.TemperatureTarget,
                TemperatureDelay = body?.TemperatureDelaySeconds is double d
                    ? TimeSpan.FromSeconds(d) : default,
                SinteredVolumeFactor = body?.SinteredVolumeFactor ?? 0.0,
            };
            await Run(printing, tuning, cmd, ct).ConfigureAwait(false);
            return Results.Ok(Timed(new { ok = true }));
        });

        // ── bed-level ─────────────────────────────────────────────────────────
        // Level the powder bed (step-and-check sequence).
        // Body: { stepThickness?, stepCount?, dryPrint? }
        app.MapPost("/powder-tuning/bed-level", async (
            BedLevelRequest? body, IPrintingService printing, IPowderTuning? tuning,
            CancellationToken ct) =>
        {
            if (printing.PrintingMode != PrintingMode.PowderTuning)
                return Results.BadRequest(new { error = "no powder tuning session is active" });

            var cmd = new BedLevelingSetup
            {
                StepThickness = body?.StepThickness,
                StepCount = body?.StepCount,
                DryPrintEnabled = body?.DryPrint ?? false,
            };
            await Run(printing, tuning, cmd, ct).ConfigureAwait(false);
            return Results.Ok(Timed(new { ok = true }));
        });

        // ── surface ───────────────────────────────────────────────────────────
        // Set the surface IR temperature target. Blocks until reached.
        // Body: { temperature }
        app.MapPost("/powder-tuning/surface", async (
            SurfaceRequest body, IPrintingService printing, IPowderTuning? tuning,
            CancellationToken ct) =>
        {
            if (printing.PrintingMode != PrintingMode.PowderTuning)
                return Results.BadRequest(new { error = "no powder tuning session is active" });

            await Run(printing, tuning,
                new PowderTuningSetSurfaceCommand { SurfaceTemperature = body.Temperature },
                ct).ConfigureAwait(false);
            return Results.Ok(Timed(new { ok = true, temperature = body.Temperature }));
        });

        // ── params ────────────────────────────────────────────────────────────
        // Select which patch to sinter (gridIndex 0..gridDim²-1, row-major)
        // and optional per-patch metadata. Omit a field to leave it unchanged.
        // Send an empty body {} to read back the current values.
        // Body: { gridIndex?, fillPhase?, printLabelIndex? }
        app.MapPost("/powder-tuning/params", async (
            SetParamsRequest? body, IPrintingService printing, IPowderTuning? tuning,
            CancellationToken ct) =>
        {
            if (printing.PrintingMode != PrintingMode.PowderTuning)
                return Results.BadRequest(new { error = "no powder tuning session is active" });

            var cmd = new PowderTuningSetParametersCommand
            {
                GridIndex = body?.GridIndex,
                FillPhase = body?.FillPhase,
                PrintLabelIndex = body?.PrintLabelIndex,
            };
            await Run(printing, tuning, cmd, ct).ConfigureAwait(false);
            return Results.Ok(Timed(new
            {
                gridIndex = cmd.GridIndex,
                fillPhase = cmd.FillPhase,
                printLabelIndex = cmd.PrintLabelIndex,
            }));
        });

        // ── print/setup (GET) ─────────────────────────────────────────────────
        // Read the current laser setup values WITHOUT sintering. Executes an
        // empty PowderTuningPrintCommand (no SetupFunc), which the service uses
        // to populate PrintSetupSource without firing the laser. Returns the
        // effective parameters the next print patch would start from.
        app.MapGet("/powder-tuning/print/setup", async (
            IPrintingService printing, IPowderTuning? tuning, CancellationToken ct) =>
        {
            if (printing.PrintingMode != PrintingMode.PowderTuning)
                return Results.BadRequest(new { error = "no powder tuning session is active" });

            var cmd = new PowderTuningPrintCommand(); // no SetupFunc = probe only, no laser fire
            await Run(printing, tuning, cmd, ct).ConfigureAwait(false);
            return Results.Ok(Timed(ProjectSetup(cmd.PrintSetupSource)));
        });

        // ── print ─────────────────────────────────────────────────────────────
        // Sinter the currently selected patch with the given laser parameters.
        // Omitted fields fall back to the session's running profile values.
        // Returns the PrintSetup that was actually used (PrintSetupResult), so
        // callers can log exactly what was applied per patch.
        //
        // Body: { laserOnPercent?, totalEnergyDensityPercent?,
        //         laserFillEnergyDensity?, laserFirstOutlineEnergyDensity?,
        //         laserOtherOutlineEnergyDensity?, laserFillSpeedXY?,
        //         laserOutlineSpeedXY?, outlineCount?, fillPhase?,
        //         printNumberEnabled? }
        app.MapPost("/powder-tuning/print", async (
            PrintRequest? body, IPrintingService printing, IPowderTuning? tuning,
            CancellationToken ct) =>
        {
            if (printing.PrintingMode != PrintingMode.PowderTuning)
                return Results.BadRequest(new { error = "no powder tuning session is active" });

            // Capture into locals — the lambda must not hold a reference to the
            // request record beyond the request lifetime.
            var laserOn      = body?.LaserOnPercent;
            var totalEnergy  = body?.TotalEnergyDensityPercent;
            var fillEnergy   = body?.LaserFillEnergyDensity;
            var firstOutline = body?.LaserFirstOutlineEnergyDensity;
            var otherOutline = body?.LaserOtherOutlineEnergyDensity;
            var fillSpeed    = body?.LaserFillSpeedXY;
            var outlineSpeed = body?.LaserOutlineSpeedXY;
            var outlineCount = body?.OutlineCount;
            var fillPhase    = body?.FillPhase;
            var printNumber  = body?.PrintNumberEnabled ?? true;

            var cmd = new PowderTuningPrintCommand
            {
                PrintNumberEnabled = printNumber,
                // SetupFunc receives a copy of the current PrintSetup from the
                // service. Apply the requested overrides; omitted fields keep
                // the profile's current value. Returning the (modified) setup
                // triggers the actual laser sinter.
#pragma warning disable CS0618 // LaserFirstOutlineEnergyDensity/LaserOtherOutlineEnergyDensity are obsolete in newer firmware source; still functional in the deployed ABI
                SetupFunc = (setup, _) =>
                {
                    if (laserOn.HasValue)      setup.LaserOnPercent = laserOn.Value;
                    if (totalEnergy.HasValue)  setup.TotalEnergyDensityPercent = totalEnergy.Value;
                    if (fillEnergy.HasValue)   setup.LaserFillEnergyDensity = fillEnergy.Value;
                    if (firstOutline.HasValue) setup.LaserFirstOutlineEnergyDensity = firstOutline.Value;
                    if (otherOutline.HasValue) setup.LaserOtherOutlineEnergyDensity = otherOutline.Value;
                    if (fillSpeed.HasValue)    setup.LaserFillSpeedXY = fillSpeed.Value;
                    if (outlineSpeed.HasValue) setup.LaserOutlineSpeedXY = outlineSpeed.Value;
                    if (outlineCount.HasValue) setup.OutlineCount = outlineCount.Value;
                    if (fillPhase.HasValue)    setup.FillPhase = fillPhase.Value;
#pragma warning restore CS0618
#pragma warning disable CS8619 // Nullability mismatch: SetupFunc delegate may declare PrintSetup? in newer firmware
                    return ValueTask.FromResult(setup);
#pragma warning restore CS8619
                },
            };
            await Run(printing, tuning, cmd, ct).ConfigureAwait(false);
            return Results.Ok(Timed(ProjectSetup(cmd.PrintSetupResult)));
        });

        // ── stop ──────────────────────────────────────────────────────────────
        // End the powder tuning session cleanly. The firmware handles cap-and-cool.
        app.MapPost("/powder-tuning/stop", async (
            IPrintingService printing, IPowderTuning? tuning, CancellationToken ct) =>
        {
            if (printing.PrintingMode != PrintingMode.PowderTuning)
                return Results.BadRequest(new { error = "no powder tuning session is active" });

            await Run(printing, tuning, new EndPrintSetup(), ct).ConfigureAwait(false);
            return Results.Ok(Timed(new { ok = true }));
        });
    }

    // Dispatches a powder tuning command using whichever API the deployed firmware
    // exposes. Two code paths handle the DLL version split:
    //
    //   Older deployed ABI: PowderTuningService is a separate singleton registered
    //   as IPowderTuning. Its ExecuteCommand(object, StatusUpdater, CancellationToken)
    //   is the dispatch point. The plugin forwards it into the child DI container so
    //   endpoint handlers can declare IPowderTuning? as a parameter (null when absent).
    //
    //   Newer source ABI: ExecutePowderTuningCommand was merged onto IPrintingService
    //   itself (per the source code in the submodule). The deployed refs/ DLLs predate
    //   this change, so we cannot call it at compile time — we resolve it via reflection
    //   at runtime on the live IPrintingService instance.
    private static async Task Run(
        IPrintingService printing,
        IPowderTuning? tuning,
        object command,
        CancellationToken ct)
    {
        if (tuning is not null)
        {
            // Older ABI path: StatusUpdater is a progress-reporting delegate;
            // null is safe — the service skips progress callbacks when it's absent.
            await tuning.ExecuteCommand(command, null!, ct).ConfigureAwait(false);
            return;
        }

        // Newer ABI path: ExecutePowderTuningCommand on IPrintingService.
        // Called via reflection because the compile-time refs/ DLLs predate this
        // method being added to the interface.
        var method = printing.GetType().GetMethod("ExecutePowderTuningCommand")
            ?? throw new InvalidOperationException(
                "Powder tuning is not available: IPowderTuning is not registered in DI and " +
                "IPrintingService.ExecutePowderTuningCommand was not found on the runtime type. " +
                "Update refs/ DLLs from the deployed printer firmware.");

        var result = method.Invoke(printing, [command, null, ct]);
        if (result is Task task)
            await task.ConfigureAwait(false);
    }

#pragma warning disable CS0618 // obsolete outline fields still present in deployed ABI
    private static object ProjectSetup(SLS4All.Compact.PrintSessions.PrintSetup? s) =>
        s is null ? (object)new { } : new
        {
            laserOnPercent = s.LaserOnPercent,
            totalEnergyDensityPercent = s.TotalEnergyDensityPercent,
            laserFillEnergyDensity = s.LaserFillEnergyDensity,
            laserFirstOutlineEnergyDensity = s.LaserFirstOutlineEnergyDensity,
            laserOtherOutlineEnergyDensity = s.LaserOtherOutlineEnergyDensity,
            laserOutlineEnergyDensities = s.LaserOutlineEnergyDensities?.ToArray(),
            laserFillSpeedXY = s.LaserFillSpeedXY,
            laserOutlineSpeedXY = s.LaserOutlineSpeedXY,
            outlineCount = s.OutlineCount,
            fillPhase = s.FillPhase,
            powderTuningGridDim = s.PowderTuningGridDim,
            powderTuningGridMargin = s.PowderTuningGridMargin,
            powderTuningDepth = s.PowderTuningDepth,
        };
#pragma warning restore CS0618

    private static object Timed<T>(T payload) =>
        new { respondedAt = DateTimeOffset.UtcNow, data = payload };

    private sealed record LayerRequest(
        double? TemperatureTarget,
        double? TemperatureDelaySeconds,
        double? SinteredVolumeFactor);

    private sealed record BedLevelRequest(
        double? StepThickness,
        int? StepCount,
        bool? DryPrint);

    private sealed record SurfaceRequest(double Temperature);

    private sealed record SetParamsRequest(
        int? GridIndex,
        int? FillPhase,
        int? PrintLabelIndex);

    private sealed record PrintRequest(
        decimal? LaserOnPercent,
        decimal? TotalEnergyDensityPercent,
        decimal? LaserFillEnergyDensity,
        decimal? LaserFirstOutlineEnergyDensity,
        decimal? LaserOtherOutlineEnergyDensity,
        decimal? LaserFillSpeedXY,
        decimal? LaserOutlineSpeedXY,
        int? OutlineCount,
        int? FillPhase,
        bool? PrintNumberEnabled);
}
