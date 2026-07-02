using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SLS4All.Compact.Movement;
using SLS4All.Compact.Printer;
using SLS4All.Compact.Temperature;

namespace Inova.ApiPlugin;

// Subclass of the firmware's LayerClient that expands one BeginLayer call
// into N full-height recoats. Installed via [Application.PluginReplacements]
// — see install.sh.
//
// Why this exists: the firmware's own RecoaterPasses(Override) is NOT "N full
// recoats" — with PowderBedDivision = Thickness it stages the powder delivery
// (pass i feeds 1/N of the dose, stops partway with a shake, and only the
// last pass planes the bed at layer height). This class implements the other
// intuitive semantics:
//
//   sub-recoat 0:    the normal recoat — bed drops one full layer thickness,
//                    full powder dose, complete sweep.
//   sub-recoats 1..: repeat sweeps at the SAME bed height, via
//                    DisableLayerAdditiveMovement = true ("Z will not
//                    additively move down or up ... effectively the same as
//                    setting LayerThickness to zero" per the firmware docs).
//                    With RepeatPowderDose, each repeat first feeds a full
//                    powder-chamber dose manually (the flag suppresses the
//                    built-in feed), for short-feed compensation; without it
//                    the repeats run dry — useful for pushing debris off the
//                    print bed. Extra powder consumption is budgeted by the
//                    print profile's cap powder (~50-100 recoats' worth).
//
// The net bed drop per layer stays exactly one layer thickness either way,
// which the slicer's Z bookkeeping requires.
//
// BeginLayer is public virtual and the firmware registers LayerClient via
// AddAsImplementationAndInterfaces, so the replacement resolves for both the
// concrete type and ILayerClient (the IPrintingService path). Unlike the
// disabled LoggingMovementClient replacement (DI cycle through
// McuPrinterClient), LayerClient's dependencies are all lower-level clients
// that don't resolve ILayerClient back, so no constructable cycle.
public class FullRecoatLayerClient : LayerClient
{
    // Runtime override state, read at the start of every BeginLayer. Written
    // by the /printing/recoater-passes-full endpoints. Static because the
    // plugin's child container and the firmware's parent container hold
    // different code paths to the same plugin assembly. Null or 1 = passthrough.
    private static volatile int _fullPassesOverride; // 0 = unset

    public static int? FullPassesOverride
    {
        get
        {
            var v = _fullPassesOverride;
            return v == 0 ? null : v;
        }
        set => _fullPassesOverride = value ?? 0;
    }

    // Whether repeat sweeps feed a fresh full powder dose (true, short-feed
    // compensation) or run dry (false, debris clearing). Sticky across value
    // changes.
    private static volatile bool _repeatPowderDose = true;

    public static bool RepeatPowderDose
    {
        get => _repeatPowderDose;
        set => _repeatPowderDose = value;
    }

    private readonly IOptionsMonitor<LayerClientOptions> _layerOptions;
    private readonly IMovementClient _movementClient;
    private readonly ILogger<FullRecoatLayerClient> _log;

    public FullRecoatLayerClient(
        ILogger<LayerClient> logger,
        IOptionsMonitor<LayerClientOptions> options,
        IMovementClient movement,
        IPrinterClient printer,
        ITemperatureClient temperature,
        ISurfaceHeater surface,
        ILogger<FullRecoatLayerClient> log)
        : base(logger, options, movement, printer, temperature, surface)
    {
        _layerOptions = options;
        _movementClient = movement;
        _log = log;
        _log.LogInformation("FullRecoatLayerClient installed as ILayerClient (full-recoat expansion available)");
    }

    public override async Task BeginLayer(BeginLayerSetup setup, CancellationToken cancel)
    {
        if (FullPassesOverride is not int passes || passes <= 1 || !setup.Enabled)
        {
            await base.BeginLayer(setup, cancel).ConfigureAwait(false);
            return;
        }

        var powder = RepeatPowderDose;
        _log.LogInformation(
            "Expanding layer into {Passes} full-height recoats ({Repeats} {Kind} repeat sweeps)",
            passes, passes - 1, powder ? "powdered" : "dry");

        // The firmware's staged multi-pass would re-trigger inside each
        // sub-recoat if RecoaterPassesOverride is set (it wins over the
        // setup's RecoaterPasses). Force it to 1 for the duration and restore
        // the plugin's saved override state afterwards — re-read at restore
        // time so a POST landing mid-layer isn't clobbered. A GET on
        // /printing/recoater-passes during the expansion will transiently
        // report 1; cosmetic.
        var opts = _layerOptions.CurrentValue;
        opts.RecoaterPassesOverride = 1;
        try
        {
            for (var i = 0; i < passes; i++)
            {
                if (i > 0 && powder)
                    await FeedRepeatDose(setup, opts, cancel).ConfigureAwait(false);
                await base.BeginLayer(CreateSubSetup(setup, first: i == 0), cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            opts.RecoaterPassesOverride = InovaApiPlugin.RecoaterPassesOverrideState;
        }
    }

    // Manually feed one layer's powder dose for a repeat sweep, since
    // DisableLayerAdditiveMovement suppresses the built-in Z1 feed.
    //
    // Dose [um] = LayerThickness x (PrintChamberArea / PowderChamberArea)
    //             x VolumeFactor — the volume of one full-bed layer expressed
    // as powder-piston travel. Matches recorded telemetry: net Z1 delta per
    // layer on build 40 was ~-105.6 um for the running profile. Sintered-area
    // compensation (PrevLayerFillRatio) is deliberately omitted — the repeat
    // dose is a flat top-up, not a shrinkage-compensated primary feed.
    //
    // Z1 sign: the axis coordinate DECREASES as the piston rises to expose
    // powder (verified from build-40 position telemetry — z1 fell 42.5 mm to
    // 33.8 mm as powder was consumed), so a feed is a NEGATIVE relative move.
    // Speed left null to use the movement client's default for the axis.
    private async Task FeedRepeatDose(BeginLayerSetup setup, LayerClientOptions options, CancellationToken cancel)
    {
        var volumeFactor = options.VolumeFactorOverride ?? setup.VolumeFactor;
        var dose = setup.LayerThickness * (options.PrintChamberArea / options.PowderChamberArea) * volumeFactor;
        _log.LogInformation("Feeding repeat powder dose: Z1 relative {Delta:0.##} um", -dose);
        await _movementClient
            .MoveAux(MovementAxis.Z1, new MoveAuxItem(-dose, Relative: true), cancel: cancel)
            .ConfigureAwait(false);
    }

    private static BeginLayerSetup CreateSubSetup(BeginLayerSetup src, bool first)
        => new()
        {
            // LayerSetupBase / PrintLayerSetupBase
            ConfigurationName = src.ConfigurationName,
            Enabled = src.Enabled,
            LayerThickness = src.LayerThickness,
            RecoaterPowderSpeedFactor = src.RecoaterPowderSpeedFactor,
            RecoaterPrintSpeedFactor = src.RecoaterPrintSpeedFactor,
            ZMove = src.ZMove,
            RecoaterShakeFactor = src.RecoaterShakeFactor,
            VolumeFactor = src.VolumeFactor,
            // Repeat sweeps run the full recoat motion but must not advance Z
            // (no additional bed drop, no built-in powder feed).
            DisableLayerAdditiveMovement = first ? src.DisableLayerAdditiveMovement : true,
            DisableZMovement = src.DisableZMovement,
            RecoaterPasses = 1,
            RecoaterMaxDistance = src.RecoaterMaxDistance,
            MidRecoatThicknessFactor = src.MidRecoatThicknessFactor,
            // BeginLayerSetup
            PrevLayerFillRatio = src.PrevLayerFillRatio,
            SinteredVolumeFactor = src.SinteredVolumeFactor,
            CustomSinteredVolumeFactor = src.CustomSinteredVolumeFactor,
            UseSlowRecoaterSpeed = src.UseSlowRecoaterSpeed,
            // Temperature settle + layer-extend dwell are per-layer costs,
            // not per-sweep: pay them once on the first sub-recoat, and hold
            // the already-reached target for the rest.
            TemperatureTarget = src.TemperatureTarget,
            TemperatureDelay = first ? src.TemperatureDelay : TimeSpan.Zero,
            KeepTemperatureTarget = first ? src.KeepTemperatureTarget : true,
            LayerExtendDelay = first ? src.LayerExtendDelay : TimeSpan.Zero,
        };
}
