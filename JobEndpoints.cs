using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SLS4All.Compact.Nesting;
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
/// CREATE-from-scratch is intentionally omitted: creating a job requires
/// uploading STL mesh files, which belongs to a file-upload flow, not a
/// JSON endpoint. POST /jobs/{id}/clone covers the create-from-template
/// workflow instead (added 2026-07-18).
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

        // Clone — the firmware's own IJobStorage.CloneJob (the same operation
        // the firmware UI's duplicate uses): copies the .s4a under a new id,
        // then optionally re-points the print profile via the UpsertJob
        // pattern. We pass suggestedId ourselves because IPrintJob doesn't
        // expose an Id — the clone's identity must be known up front.
        // Mechanically generic on purpose: the "[TEMPLATE]" policy (agents may
        // only clone template jobs) is enforced in the MCP layer, not here.
        // 404 unknown source; 400 for a missing/blank name.
        app.MapPost("/jobs/{id:guid}/clone", async (
            Guid id, CloneJobRequest? body, IJobStorage jobs, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "'name' is required" });

            var srcDesc = await jobs.TryGetJobDescription(id, ct).ConfigureAwait(false);
            if (srcDesc is null) return Results.NotFound(new { error = $"no job with id {id}" });

            var newId = Guid.NewGuid();
            var clone = await jobs.CloneJob(id, body.Name.Trim(), newId, ct).ConfigureAwait(false);

            if (body.PrintProfileId is not null)
            {
                // Re-read before mutating: CloneJob's returned object keeps the
                // requested name verbatim, while the storage sanitizes
                // filesystem-hostile characters ("/" → "_") into the persisted
                // name — UpsertJob on the raw-named object computes a
                // mismatched path and throws FileNotFound. A fresh read is
                // consistent (same pattern as the PATCH route above).
                var fresh = await jobs.TryGetJob(newId, ct).ConfigureAwait(false) ?? clone;
                var profileId = Guid.TryParse(body.PrintProfileId, out var g) ? g : Guid.Empty;
                fresh.PrintProfile = new PrintProfileReference { Id = profileId };
                await jobs.UpsertJob(fresh, ct).ConfigureAwait(false);
                clone = fresh;
            }

            var cloneDesc = await jobs.TryGetJobDescription(newId, ct).ConfigureAwait(false);
            if (cloneDesc is null)
                return Results.Problem($"clone created but not found under suggested id {newId}");
            return Results.Json(ProjectJob(cloneDesc, clone), _json, statusCode: StatusCodes.Status201Created);
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

        // Placed instances of a STORED job — the nesting solution persisted in
        // the .s4a's NestingSnapshot, available whether or not anything is
        // printing (unlike /job/current/parts, which reads the live
        // INestingService and is empty when idle). Shape mirrors
        // /printing/objects: `transform` is the snapshot's MeshPrintTransform
        // as a row-major 4x4 (V·M convention, same as the live route);
        // `transformState` carries the raw nesting-editor placement
        // (position/rotation/quaternion/scale) as a fallback for jobs whose
        // print transform was never computed. Bounds are NOT included — the
        // client derives mesh-local bounds from the fetched geometry.
        // Chamber dims come from the nesting service and are valid while idle.
        // Non-Automatic jobs (or jobs never nested) return an empty list.
        app.MapGet("/jobs/{id:guid}/instances", async (
            Guid id, IJobStorage jobs, INestingService nesting, CancellationToken ct) =>
        {
            var job = await jobs.TryGetJob(id, ct).ConfigureAwait(false);
            if (job is null) return Results.NotFound(new { error = $"no job with id {id}" });

            var dim = nesting.NestingDim;
            var instances = (job.NestingState as AutomaticJob.NestingSnapshot)?.Instances
                ?? Array.Empty<AutomaticJobInstance>();

            return Results.Json(new
            {
                chamber = dim is null ? null : new
                {
                    sizeX = dim.SizeX,
                    sizeY = dim.SizeY,
                    sizeZ = dim.SizeZ,
                },
                instances = instances.Select(i =>
                {
                    var t = i.TransformState;
                    var m = i.MeshPrintTransform;
                    return new
                    {
                        id = i.Id,
                        name = i.ObjectCopy?.Name,
                        hash = i.Hash,
                        isOverlapping = i.IsOverlapping,
                        transform = new[]
                        {
                            new[] { m.M11, m.M12, m.M13, m.M14 },
                            new[] { m.M21, m.M22, m.M23, m.M24 },
                            new[] { m.M31, m.M32, m.M33, m.M34 },
                            new[] { m.M41, m.M42, m.M43, m.M44 },
                        },
                        transformState = new
                        {
                            position = new[] { t.PX, t.PY, t.PZ },
                            rotation = new[] { t.RX, t.RY, t.RZ },
                            quaternion = new[] { t.QX, t.QY, t.QZ, t.QW },
                            scale = new[] { t.SX, t.SY, t.SZ },
                        },
                    };
                }).ToArray(),
            }, _json);
        });

        // Mesh geometry for a STORED job's object file, keyed by content hash —
        // the off-print counterpart of /printing/meshes/{hash}. Reads the raw
        // STL out of the .s4a archive via IJobStorage.GetObject and converts it
        // to the same binary MESH blob the live route serves (see StlMesh).
        // 404 for unknown job or hash; 422 if the STL fails to parse.
        app.MapGet("/jobs/{id:guid}/meshes/{hash}", async (
            Guid id, string hash, IJobStorage jobs, CancellationToken ct) =>
        {
            var job = await jobs.TryGetJob(id, ct).ConfigureAwait(false);
            if (job is null) return Results.NotFound(new { error = $"no job with id {id}" });

            var file = job.ObjectFiles?.FirstOrDefault(
                f => string.Equals(f.Hash, hash, StringComparison.OrdinalIgnoreCase));
            if (file is null)
                return Results.NotFound(new { error = "no object file with that hash in this job" });

            await using var stream = await jobs
                .GetObject(new JobObjectPath(id, file.Name), ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);

            var blob = StlMesh.ToMeshBlob(ms.ToArray());
            if (blob is null)
                return Results.UnprocessableEntity(new { error = $"could not parse '{file.Name}' as STL" });
            return Results.Bytes(blob, "application/octet-stream");
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

    private sealed record CloneJobRequest(string? Name, string? PrintProfileId);
}
