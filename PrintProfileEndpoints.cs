using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SLS4All.Compact.Storage.PrintProfiles;

namespace Inova.ApiPlugin;

/// <summary>
/// Basic REST CRUD over the firmware's print profiles. Backed by the firmware's
/// own <see cref="IPrintProfileStorage"/> singleton (forwarded into the plugin's
/// child DI container), so profiles created/edited here are the exact same
/// on-disk JSON the firmware UI reads and the print pipeline resolves at print
/// time — no separate store, no format drift.
///
/// Storage model note: a *user* profile is stored as a sparse delta (mostly
/// null fields); the effective values a print uses come from merging it over the
/// system Default profile. So:
///   - CREATE starts from an empty PrintProfile (all deltas null) — the new
///     profile inherits everything not explicitly set, and keeps tracking the
///     Default for untouched fields.
///   - GET /{id} returns the stored delta; GET /{id}?merged=true returns the
///     effective (merged-over-default) values the print would actually use.
///
/// This surface intentionally does NOT tune a profile mid-print — that is what
/// the /printing/setup-overrides and /printing/layer-overrides routes are for.
/// </summary>
internal static class PrintProfileEndpoints
{
    // Match the rest of the plugin's HTTP surface: camelCase, case-insensitive.
    // Enums serialize as their integer value (as the firmware's on-disk profile
    // JSON does, e.g. "shrinkageCorrectionType": 2).
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // Profiles are stored as "<name>.<guid>.json" (the default profile as
    // "DefaultProfile.json") under ~/SLS4All/PrintProfiles. The storage
    // interface exposes no timestamps, and most profiles were never stamped
    // with CreatedAt — the file mtime is the only real date, so the list
    // endpoint stats the files directly.
    private static readonly string _profilesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "SLS4All", "PrintProfiles");

    // Enforced field bounds — the firmware's PrintProfile model carries NO
    // validation of its own (0/97 properties have range attributes, no
    // Validate method), so this table is the source of truth keeping
    // out-of-range values out of storage, for every caller (GUI form, MCP
    // tools, curl). Temperature ceilings are deliberately conservative
    // (200 °C — PA12-class work runs well under it); raise here on purpose
    // if a hotter material ever needs it. Keep in sync with the MCP
    // profile_set schema and the GUI form ranges.
    private static readonly Dictionary<string, (double Min, double Max)> _bounds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Thickness fields are MICROMETERS (live data: layer 100,
            // bedPreparation 13000, printCap 5000).
            ["layerThickness"] = (50, 300),
            ["recoaterPasses"] = (1, 10),
            ["recoaterPowderSpeedPercent"] = (1, 200),
            ["recoaterPrintSpeedPercent"] = (1, 200),
            ["heatingTargetPowder"] = (0, 200),
            ["heatingTargetPrint"] = (0, 200),
            ["heatingTargetPrintBed"] = (0, 200),
            ["heatingRate"] = (0, 100),
            ["heatingThreshold"] = (0, 200),
            ["surfaceTarget"] = (0, 200),
            ["beginLayerTemperatureTarget"] = (0, 200),
            ["bedPreparationTemperatureTarget"] = (0, 200),
            ["bedPreparationThickness"] = (0, 50000),
            ["printCapTemperatureTarget"] = (0, 200),
            ["printCapThickness"] = (0, 50000),
            ["laserOnPercent"] = (0, 100),
            ["totalEnergyDensityPercent"] = (1, 500),
            ["laserFirstOutlineEnergyDensity"] = (0, 100),
            ["laserOtherOutlineEnergyDensity"] = (0, 100),
            ["laserFillEnergyDensity"] = (0, 100),
            ["outlineCount"] = (0, 20),
            ["coolingTarget"] = (0, 200),
            ["coolingThreshold1"] = (0, 200),
            ["coolingThreshold2"] = (0, 200),
            ["coolingRate1"] = (0, 100),
            ["coolingRate2"] = (0, 100),
        };

    // Collect bound violations for the numeric fields present in a patch
    // body. Nulls pass (null = revert to inheriting the Default); unknown
    // keys pass (ApplyPatch decides what they mean).
    private static List<string> BoundViolations(JsonObject body)
    {
        var errors = new List<string>();
        foreach (var (key, node) in body)
        {
            if (node is null || !_bounds.TryGetValue(key, out var b)) continue;
            if (node is JsonValue v && v.TryGetValue<double>(out var d) && (d < b.Min || d > b.Max))
                errors.Add($"{key}={d} out of range [{b.Min}..{b.Max}]");
        }
        return errors;
    }

    private static DateTimeOffset? FileModifiedAt(Guid id, bool isDefault)
    {
        try
        {
            var file = isDefault
                ? Path.Combine(_profilesDir, "DefaultProfile.json")
                : Directory.EnumerateFiles(_profilesDir, $"*.{id}.json").FirstOrDefault();
            if (file is null || !File.Exists(file)) return null;
            return File.GetLastWriteTimeUtc(file);
        }
        catch
        {
            return null;
        }
    }

    public static void MapPrintProfileEndpoints(this WebApplication app)
    {
        // List — {id, name, isDefault, createdAt, modifiedAt} in the firmware's
        // own ordering. createdAt lives only on the full profile (descriptions
        // don't carry it, and most existing profiles were never stamped);
        // modifiedAt is the profile file's mtime — the reliable "real" date.
        app.MapGet("/profiles", async (IPrintProfileStorage storage, CancellationToken ct) =>
        {
            var result = new List<object>();
            foreach (var d in await storage.GetOrderedProfileDescriptions(ct).ConfigureAwait(false))
            {
                DateTimeOffset? createdAt = null;
                try
                {
                    var p = d.IsDefault
                        ? await storage.GetDefaultProfile(ct).ConfigureAwait(false)
                        : await storage.TryGetProfile(d.Id, ct).ConfigureAwait(false);
                    createdAt = p?.CreatedAt;
                }
                catch { /* unreadable profile — list it without a date */ }
                result.Add(new
                {
                    id = d.Id,
                    name = d.Name,
                    isDefault = d.IsDefault,
                    createdAt,
                    modifiedAt = FileModifiedAt(d.Id, d.IsDefault),
                });
            }
            return Results.Ok(result.ToArray());
        });

        // Get one. Returns the stored user delta by default; ?merged=true returns
        // the effective values (delta merged over the system Default) the print
        // pipeline would actually apply. 404 if the id isn't a known profile.
        app.MapGet("/profiles/{id:guid}", async (
            Guid id, bool? merged, IPrintProfileStorage storage, CancellationToken ct) =>
        {
            var profile = merged == true
                ? await storage.TryGetMergedProfile(id, ct).ConfigureAwait(false)
                : await storage.TryGetProfile(id, ct).ConfigureAwait(false);
            return profile is null ? Results.NotFound(new { error = $"no profile with id {id}" })
                                   : Results.Json(profile, _json);
        });

        // Create. Body is a partial profile (camelCase); at minimum "name" is
        // required. Starts from an empty delta so the new profile inherits from
        // the Default for everything not supplied. Server assigns a fresh id
        // (any "id" in the body is ignored). Returns 201 with the stored profile.
        app.MapPost("/profiles", async (
            JsonObject? body, IPrintProfileStorage storage, CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "JSON object body required" });
            var name = TryGetString(body, "name");
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "\"name\" is required and must be non-empty" });

            var violations = BoundViolations(body);
            if (violations.Count > 0)
                return Results.BadRequest(new { error = "validation failed: " + string.Join("; ", violations) });

            PrintProfile profile;
            try { profile = ApplyPatch(new PrintProfile(), body); }
            catch (JsonException ex) { return Results.BadRequest(new { error = "invalid profile field: " + ex.Message }); }

            profile.Id = Guid.NewGuid();
            // Stamp creation time (the firmware doesn't always) — feeds the
            // GUI's sort-by-date column.
            profile.CreatedAt ??= DateTimeOffset.UtcNow;
            // Give the nested shrinkage-correction record its own identity so two
            // profiles never share a child id (the Default's child id would
            // otherwise be inherited through the serialized template).
            if (profile.ShrinkageCorrectionStandard is { } scs)
                scs.Id = Guid.NewGuid();

            await storage.UpsertUserProfile(profile, ct).ConfigureAwait(false);
            return Results.Created($"/profiles/{profile.Id}", profile);
        });

        // Edit (partial update). Only the fields present in the body change;
        // everything else is left as stored. Send a field as null to clear it
        // (reverts that field to inheriting the Default). Editing the Default
        // profile itself is routed through SetDefaultProfile. 404 if unknown.
        app.MapPut("/profiles/{id:guid}", async (
            Guid id, JsonObject? body, IPrintProfileStorage storage, CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "JSON object body required" });

            var putViolations = BoundViolations(body);
            if (putViolations.Count > 0)
                return Results.BadRequest(new { error = "validation failed: " + string.Join("; ", putViolations) });

            var defaultId = (await storage.GetDefaultProfile(ct).ConfigureAwait(false)).Id;
            var isDefault = id == defaultId;

            var existing = isDefault
                ? await storage.GetDefaultProfile(ct).ConfigureAwait(false)
                : await storage.TryGetProfile(id, ct).ConfigureAwait(false);
            if (existing is null)
                return Results.NotFound(new { error = $"no profile with id {id}" });

            PrintProfile updated;
            try { updated = ApplyPatch(existing, body); }
            catch (JsonException ex) { return Results.BadRequest(new { error = "invalid profile field: " + ex.Message }); }
            updated.Id = id; // never let the body move a profile's identity

            if (isDefault)
                await storage.SetDefaultProfile(updated, ct).ConfigureAwait(false);
            else
                await storage.UpsertUserProfile(updated, ct).ConfigureAwait(false);
            return Results.Json(updated, _json);
        });

        // Delete a user profile. The system Default cannot be deleted (400).
        // 404 if the id isn't a known profile.
        app.MapDelete("/profiles/{id:guid}", async (
            Guid id, IPrintProfileStorage storage, CancellationToken ct) =>
        {
            var descriptions = await storage.GetOrderedProfileDescriptions(ct).ConfigureAwait(false);
            var match = descriptions.FirstOrDefault(d => d.Id == id);
            if (match is null)
                return Results.NotFound(new { error = $"no profile with id {id}" });
            if (match.IsDefault)
                return Results.BadRequest(new { error = "the default profile cannot be deleted" });

            await storage.RemoveProfile(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    // Overlay the JSON patch onto a base profile using a serialize→merge→
    // deserialize round-trip. This reuses System.Text.Json's native conversions
    // for every field type (decimal/int/bool/TimeSpan?/enum/StorageList<decimal>/
    // nested records) instead of hand-mapping ~90 properties, and gives partial-
    // update semantics: keys absent from the patch keep the base value; keys
    // present (including explicit null) overwrite it. Read-only computed
    // properties on PrintProfile (the *Collapsed getters) serialize out but are
    // silently ignored on the way back in.
    private static PrintProfile ApplyPatch(PrintProfile baseProfile, JsonObject patch)
    {
        var node = JsonSerializer.SerializeToNode(baseProfile, _json)!.AsObject();
        foreach (var (rawKey, value) in patch)
        {
            // Resolve to the base node's existing casing so we replace rather
            // than add a duplicate-cased key; fall back to the incoming key
            // (final deserialize is case-insensitive anyway).
            var key = node.FirstOrDefault(
                n => string.Equals(n.Key, rawKey, StringComparison.OrdinalIgnoreCase)).Key ?? rawKey;
            node[key] = value?.DeepClone();
        }
        return node.Deserialize<PrintProfile>(_json)!;
    }

    private static string? TryGetString(JsonObject obj, string key)
    {
        var match = obj.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));
        return match.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
