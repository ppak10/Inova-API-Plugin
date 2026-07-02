using System;
using System.Collections.Generic;
using System.Linq;
using SLS4All.Compact.Storage.PrintSessions;

namespace Inova.ApiPlugin;

// Registry of the scalar knobs on the firmware's PrintSetupOverrides — the
// native mid-print tuning channel exposed read/write as
// IPrintingService.SetupOverrides. Unlike LayerOverrides (our plugin-held
// state re-applied onto LayerClientOptions), these are owned by the firmware:
// PrintingService resets SetupOverrides to a fresh empty instance at every
// print start (RunOverride), so anything POSTed while idle is discarded when
// the next print begins. Null = defer to the running profile's value.
//
// Min/MaxTemperatureTarget are computed getters on the firmware type (derived
// from the three phase temps), so they are intentionally not listed here.
// LaserOutlineEnergyDensities (a list) is handled separately by the endpoint.
//
// All values are decimal in the firmware; ranges are sanity bounds around the
// known profile values (temps ~174 C, fill energy density ~15,
// TotalEnergyDensityPercent nominal 100).
internal sealed class PrintSetupOverrideField
{
    public required string Name { get; init; }
    public required decimal Min { get; init; }
    public required decimal Max { get; init; }
    public required Func<PrintSetupOverrides, decimal?> Get { get; init; }
    public required Action<PrintSetupOverrides, decimal?> Set { get; init; }
}

internal static class PrintSetupOverrideFields
{
    public static readonly PrintSetupOverrideField[] Fields =
    [
        new()
        {
            Name = "bedPreparationTemperatureTarget", Min = 0, Max = 300,
            Get = o => o.BedPreparationTemperatureTarget,
            Set = (o, v) => o.BedPreparationTemperatureTarget = v,
        },
        new()
        {
            Name = "beginLayerTemperatureTarget", Min = 0, Max = 300,
            Get = o => o.BeginLayerTemperatureTarget,
            Set = (o, v) => o.BeginLayerTemperatureTarget = v,
        },
        new()
        {
            Name = "printCapTemperatureTarget", Min = 0, Max = 300,
            Get = o => o.PrintCapTemperatureTarget,
            Set = (o, v) => o.PrintCapTemperatureTarget = v,
        },
        new()
        {
            Name = "totalEnergyDensityPercent", Min = 1, Max = 500,
            Get = o => o.TotalEnergyDensityPercent,
            Set = (o, v) => o.TotalEnergyDensityPercent = v,
        },
        new()
        {
            Name = "laserFillEnergyDensity", Min = 0, Max = 100,
            Get = o => o.LaserFillEnergyDensity,
            Set = (o, v) => o.LaserFillEnergyDensity = v,
        },
    ];

    public static PrintSetupOverrideField? Find(string name)
        => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public static Dictionary<string, decimal?> Snapshot(PrintSetupOverrides? overrides)
        => Fields.ToDictionary(f => f.Name, f => overrides is null ? null : f.Get(overrides));
}
