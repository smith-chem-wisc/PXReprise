using System.Text.Json;
using System.Text.Json.Nodes;

namespace PXReprise.Batch;

/// <summary>
/// One deposit's result from <c>datarepo ingest --json</c> (datarepo 1.0.0, dataRepo 010): the envelope's
/// <c>datasets[]</c> entry for the accession. <see cref="Rc"/> is what <c>ingest_rc</c> records, and only 0 is a
/// delivery.
/// </summary>
public sealed record IngestOutcome(int Rc, string? Status, string? BundleId, IReadOnlyList<string> Reasons, int? Mismatches);

public static class IngestEnvelope
{
    /// <summary>
    /// Delivered means exit code 0 AND the deposit's status is <c>ingested</c> or <c>unchanged</c> (the bundle was already
    /// written). A deposit <c>excluded</c> by the manifest, <c>refused</c>, or missing from the envelope is not delivered,
    /// even under exit code 0. An unreadable envelope falls back on the exit code, dataRepo's contract either way, and says
    /// so in <see cref="IngestOutcome.Reasons"/>.
    /// </summary>
    public static IngestOutcome Read(string stdout, string accession, int exitCode)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(stdout); }
        catch (JsonException) { root = null; }
        if (root is not JsonObject env)
            return new IngestOutcome(exitCode, null, null, new[] { $"no JSON envelope on stdout (exit code {exitCode}); the exit code decides" }, null);

        var reasons = Strings(env["reasons"]).ToList();
        if (env["error"] is { } err) reasons.Add($"error: {err.ToJsonString()}");
        var ds = env["datasets"]?.AsArray().OfType<JsonObject>().FirstOrDefault(d => Str(d["accession"]) == accession);
        if (ds is null)
        {
            reasons.Insert(0, $"{accession} is not in the envelope");
            return new IngestOutcome(exitCode != 0 ? exitCode : 1, null, null, reasons, null);
        }
        string? status = Str(ds["status"]);
        reasons.AddRange(Strings(ds["reasons"]));
        int? mismatches = ds["mismatches"] is JsonValue mv && mv.TryGetValue(out int n) ? n : null;
        bool delivered = exitCode == 0 && status is "ingested" or "unchanged";
        if (exitCode == 0 && !delivered) reasons.Insert(0, $"exit code 0 but status '{status ?? "none"}': not delivered");
        return new IngestOutcome(delivered ? 0 : exitCode != 0 ? exitCode : 1, status, Str(ds["bundle_id"]), reasons, mismatches);
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static IEnumerable<string> Strings(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue(out string? s) ? s : x?.ToJsonString()).OfType<string>() : Enumerable.Empty<string>();
}
