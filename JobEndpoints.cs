using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SLS4All.Compact.Storage;
using SLS4All.Compact.Storage.PrintJobs;
using SLS4All.Compact.Storage.PrintProfiles;

namespace Inova.ApiPlugin;

/// <summary>
/// REST API for jobs stored on the printer. Backed by the firmware's own
/// <see cref="IJobStorage"/> singleton (forwarded into the plugin's child DI
/// container), so operations here are identical to what the firmware UI does.
///
/// Jobs are stored as <c>.s4a</c> ZIP archives (metadata JSON + STL meshes).
/// The storage handles all ZIP I/O internally; these endpoints mutate the
/// in-memory <see cref="IPrintJob"/> and call <c>UpsertJob</c> — the same
/// pattern the firmware's Razor pages use.
///
/// CREATE is intentionally omitted: creating a job requires uploading STL
/// mesh files, which belongs to a file-upload flow, not a JSON endpoint.
/// Use <c>CloneJob</c> on an existing job as a template instead (future work).
/// </summary>
internal static class JobEndpoints
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static void MapJobEndpoints(this WebApplication app)
    {
        // List — lightweight descriptions ordered by the storage's own sort
        // (typically alphabetical or by creation date). Cheap to poll; no ZIP
        // reading, just the cached metadata index.
        app.MapGet("/jobs", async (IJobStorage jobs, CancellationToken ct) =>
        {
            var descriptions = await jobs.GetOrderedJobDescriptions(ct).ConfigureAwait(false);
            return Results.Ok(descriptions.Select(d => new
            {
                id = d.Id,
                type = d.Type.ToString(),
                name = d.Name,
                createdAt = d.CreatedAtUtc,
            }).ToArray());
        });

        // Get one — full metadata projection. Opens the .s4a ZIP to read the
        // job's print-profile reference, object file list, and nesting snapshot.
        // 404 if the id is unknown.
        app.MapGet("/jobs/{id:guid}", async (Guid id, IJobStorage jobs, CancellationToken ct) =>
        {
            var desc = await jobs.TryGetJobDescription(id, ct).ConfigureAwait(false);
            if (desc is null) return Results.NotFound(new { error = $"no job with id {id}" });

            var job = await jobs.TryGetJob(id, ct).ConfigureAwait(false);
            if (job is null) return Results.NotFound(new { error = $"no job with id {id}" });

            return Results.Json(ProjectJob(desc, job), _json);
        });

        // Partial update. Accepts any combination of:
        //   { "name": "New Name" }
        //   { "printProfileId": "guid" }   — "" or all-zeros clears the profile
        // Fetches the live job, mutates it, and calls UpsertJob so the storage
        // handles re-packing the .s4a archive. Returns the updated projection.
        // 404 if unknown; 400 for validation failures.
        app.MapPatch("/jobs/{id:guid}", async (
            Guid id, PatchJobRequest? body, IJobStorage jobs, CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "JSON object body required" });
            if (body.Name is null && body.PrintProfileId is null)
                return Results.BadRequest(new { error = "at least one of 'name' or 'printProfileId' is required" });
            if (body.Name is not null && string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "'name' must be non-empty" });

            var desc = await jobs.TryGetJobDescription(id, ct).ConfigureAwait(false);
            if (desc is null) return Results.NotFound(new { error = $"no job with id {id}" });

            var job = await jobs.TryGetJob(id, ct).ConfigureAwait(false);
            if (job is null) return Results.NotFound(new { error = $"no job with id {id}" });

            if (body.Name is not null)
                job.Name = body.Name.Trim();

            if (body.PrintProfileId is not null)
            {
                var profileId = Guid.TryParse(body.PrintProfileId, out var g) ? g : Guid.Empty;
                job.PrintProfile = new PrintProfileReference { Id = profileId };
            }

            await jobs.UpsertJob(job, ct).ConfigureAwait(false);

            // Re-read the description to get any storage-side normalisation
            // (e.g. CreatedAt stamp, canonical name). Fall back to the mutated
            // desc if the re-read fails.
            var updatedDesc = await jobs.TryGetJobDescription(id, ct).ConfigureAwait(false) ?? desc;
            return Results.Json(ProjectJob(updatedDesc, job), _json);
        });

        // Delete. 404 if unknown, otherwise 204. The storage handles removing
        // the .s4a file and any cached state.
        app.MapDelete("/jobs/{id:guid}", async (Guid id, IJobStorage jobs, CancellationToken ct) =>
        {
            var desc = await jobs.TryGetJobDescription(id, ct).ConfigureAwait(false);
            if (desc is null) return Results.NotFound(new { error = $"no job with id {id}" });

            await jobs.DeleteJob(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    // Projects an IPrintJob + description into a stable JSON-friendly shape.
    // IPrintJob is an interface; projecting into an anonymous type ensures the
    // serialized shape is explicit and doesn't change if the concrete firmware
    // type adds/removes fields.
    private static object ProjectJob(PrintJobDescription desc, IPrintJob job)
    {
        var ns = job.NestingState;
        return new
        {
            id = desc.Id,
            type = desc.Type.ToString(),
            name = job.Name,
            createdAt = job.CreatedAt,
            printProfileId = job.PrintProfile.IsEmpty ? (Guid?)null : job.PrintProfile.Id,
            objectFiles = job.ObjectFiles?.Select(f => new
            {
                id = f.Id,
                name = f.Name,
                hash = f.Hash,
            }).ToArray() ?? [],
            nestingState = ns is null ? null : new
            {
                chamberDepth = ns.ChamberDepth,
                chamberVolume = ns.ChamberVolume,
                sinteredVolume = ns.SinteredVolume,
                availableDepth = ns.AvailableDepth,
            },
            dryPrintEnabled = job.DryPrintEnabled,
            needsLaser = job.NeedsLaser,
            previewEnabled = job.PreviewEnabled,
        };
    }

    private sealed record PatchJobRequest(string? Name, string? PrintProfileId);
}
