using System;
using System.Collections.Generic;
using System.Linq;
using SLS4All.Compact.Movement;

namespace Inova.ApiPlugin;

// Registry of LayerClientOptions' runtime-overridable knobs (the nullable
// "set to override, null to defer to config/profile" properties), backing
// the /printing/layer-overrides endpoints.
//
// Saved values live in a static dictionary so the IOptionsMonitor.OnChange
// handler (InovaApiPlugin.StartAsync) can re-apply ALL of them when the
// firmware re-binds LayerClientOptions on a config hot-reload — without
// that, runtime overrides silently revert. Note two of these are pinned in
// appsettings.toml on the printer (recoaterShakeBackoffOverride = 70,
// midRecoatThicknessFactorOverride = 2.0): a runtime write shadows the TOML
// value and the re-apply keeps it shadowed until cleared, after which the
// next config rebind restores the TOML value.
//
// TimeSpan-typed options are exposed as fractional SECONDS over the API.
internal sealed class LayerOverrideField
{
    public required string Name { get; init; }   // camelCase API name
    public required string Kind { get; init; }   // "int" | "double" | "seconds"
    public required double Min { get; init; }
    public required double Max { get; init; }
    public required Func<LayerClientOptions, object?> Get { get; init; }
    public required Action<LayerClientOptions, object?> Set { get; init; }
}

internal static class LayerOverrides
{
    private static readonly object _sync = new();
    // Only fields explicitly set via the API appear here; value null means
    // "explicitly cleared" and is pruned to absent.
    private static readonly Dictionary<string, object?> _saved = new();

    public static readonly LayerOverrideField[] Fields =
    [
        new()
        {
            Name = "recoaterPassesOverride", Kind = "int", Min = 1, Max = 5,
            Get = o => o.RecoaterPassesOverride,
            Set = (o, v) => o.RecoaterPassesOverride = (int?)v,
        },
        new()
        {
            Name = "recoaterPowderSpeedFactorOverride", Kind = "double", Min = 0.05, Max = 10,
            Get = o => o.RecoaterPowderSpeedFactorOverride,
            Set = (o, v) => o.RecoaterPowderSpeedFactorOverride = (double?)v,
        },
        new()
        {
            Name = "recoaterPrintSpeedFactorOverride", Kind = "double", Min = 0.05, Max = 10,
            Get = o => o.RecoaterPrintSpeedFactorOverride,
            Set = (o, v) => o.RecoaterPrintSpeedFactorOverride = (double?)v,
        },
        new()
        {
            Name = "recoaterShakeFactorOverride", Kind = "double", Min = 0, Max = 10,
            Get = o => o.RecoaterShakeFactorOverride,
            Set = (o, v) => o.RecoaterShakeFactorOverride = (double?)v,
        },
        new()
        {
            // R-axis position units (config pins 70 with margin 5).
            Name = "recoaterShakeBackoffOverride", Kind = "double", Min = 0, Max = 500,
            Get = o => o.RecoaterShakeBackoffOverride,
            Set = (o, v) => o.RecoaterShakeBackoffOverride = (double?)v,
        },
        new()
        {
            // [um] — overrides ZMoveDefault (700), the Z clearance dance.
            Name = "zMoveForce", Kind = "double", Min = 0, Max = 5000,
            Get = o => o.ZMoveForce,
            Set = (o, v) => o.ZMoveForce = (double?)v,
        },
        new()
        {
            Name = "volumeFactorOverride", Kind = "double", Min = 0.1, Max = 5,
            Get = o => o.VolumeFactorOverride,
            Set = (o, v) => o.VolumeFactorOverride = (double?)v,
        },
        new()
        {
            Name = "customSinteredVolumeFactorOverride", Kind = "double", Min = 0.1, Max = 5,
            Get = o => o.CustomSinteredVolumeFactorOverride,
            Set = (o, v) => o.CustomSinteredVolumeFactorOverride = (double?)v,
        },
        new()
        {
            Name = "midRecoatThicknessFactorOverride", Kind = "double", Min = 0.1, Max = 10,
            Get = o => o.MidRecoatThicknessFactorOverride,
            Set = (o, v) => o.MidRecoatThicknessFactorOverride = (double?)v,
        },
        new()
        {
            Name = "settleTemperatureDelayOverride", Kind = "seconds", Min = 0, Max = 600,
            Get = o => o.SettleTemperatureDelayOverride?.TotalSeconds,
            Set = (o, v) => o.SettleTemperatureDelayOverride =
                v is double s ? TimeSpan.FromSeconds(s) : null,
        },
        new()
        {
            Name = "layerExtendDelayOverride", Kind = "seconds", Min = 0, Max = 600,
            Get = o => o.LayerExtendDelayOverride?.TotalSeconds,
            Set = (o, v) => o.LayerExtendDelayOverride =
                v is double s ? TimeSpan.FromSeconds(s) : null,
        },
    ];

    public static LayerOverrideField? Find(string name)
        => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public static object? GetSaved(string name)
    {
        lock (_sync)
            return _saved.GetValueOrDefault(name);
    }

    public static void SetSaved(string name, object? value)
    {
        lock (_sync)
        {
            if (value is null)
                _saved.Remove(name);
            else
                _saved[name] = value;
        }
    }

    // Re-apply every saved override onto a (freshly re-bound) options
    // instance. Called from the IOptionsMonitor.OnChange subscription.
    public static void ApplySaved(LayerClientOptions options)
    {
        KeyValuePair<string, object?>[] saved;
        lock (_sync)
            saved = _saved.ToArray();
        foreach (var (name, value) in saved)
            Find(name)?.Set(options, value);
    }
}
