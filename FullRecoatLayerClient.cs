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
// into N complete recoats at 1/N layer thickness each. Installed via
// [Application.PluginReplacements] — see install.sh.
//
// Why this exists: the firmware's own RecoaterPasses(Override) is NOT "N full
// recoats" — with PowderBedDivision = Thickness it stages the powder delivery
// (pass i feeds 1/N of the dose, stops partway with a shake, and only the
// last pass planes the bed at layer height). This class implements the other
// intuitive semantics: each sub-recoat drops the bed 1/N thickness, feeds a
// 1/N powder dose, and sweeps the full bed — N finished thin sub-layers.
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

    private readonly IOptionsMonitor<LayerClientOptions> _layerOptions;
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

        _log.LogInformation(
            "Expanding layer into {Passes} full recoats of {SubThickness:0.##} um (of {Thickness:0.##} um)",
            passes, setup.LayerThickness / passes, setup.LayerThickness);

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
                await base.BeginLayer(CreateSubSetup(setup, passes, first: i == 0), cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            opts.RecoaterPassesOverride = InovaApiPlugin.RecoaterPassesOverrideState;
        }
    }

    private static BeginLayerSetup CreateSubSetup(BeginLayerSetup src, int passes, bool first)
        => new()
        {
            // LayerSetupBase / PrintLayerSetupBase
            ConfigurationName = src.ConfigurationName,
            Enabled = src.Enabled,
            LayerThickness = src.LayerThickness / passes,
            RecoaterPowderSpeedFactor = src.RecoaterPowderSpeedFactor,
            RecoaterPrintSpeedFactor = src.RecoaterPrintSpeedFactor,
            ZMove = src.ZMove,
            RecoaterShakeFactor = src.RecoaterShakeFactor,
            VolumeFactor = src.VolumeFactor,
            DisableLayerAdditiveMovement = src.DisableLayerAdditiveMovement,
            DisableZMovement = src.DisableZMovement,
            RecoaterPasses = 1,
            RecoaterMaxDistance = src.RecoaterMaxDistance,
            MidRecoatThicknessFactor = src.MidRecoatThicknessFactor,
            // BeginLayerSetup. Powder-dose inputs (PrevLayerFillRatio, the
            // volume factors) stay on every sub-recoat: the dose scales with
            // LayerThickness, so N doses of t/N sum to the layer total.
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
