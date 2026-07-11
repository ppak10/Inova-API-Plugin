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

    public static void MapPrintProfileEndpoints(this WebApplication app)
    {
        // List — lightweight {id, name, isDefault} descriptions, default first
        // in the firmware's own ordering. Cheap enough to poll for a picker UI.
        app.MapGet("/profiles", async (IPrintProfileStorage storage, CancellationToken ct) =>
        {
            var descriptions = await storage.GetOrderedProfileDescriptions(ct).ConfigureAwait(false);
            return Results.Ok(descriptions.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                isDefault = d.IsDefault,
            }).ToArray());
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

            PrintProfile profile;
            try { profile = ApplyPatch(new PrintProfile(), body); }
            catch (JsonException ex) { return Results.BadRequest(new { error = "invalid profile field: " + ex.Message }); }

            profile.Id = Guid.NewGuid();
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
