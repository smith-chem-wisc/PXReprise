using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Provenance;

namespace PXReprise.Qc;

/// <summary>What the qc-payload stage did: the payload, and qc's verdict on it when its tool was available.</summary>
public sealed record QcPayloadOutcome(
    string PayloadFile, int Files, int? ProteinGroupsQuantified, string BinSource,
    string Validate, string? Render, string? FindingsFile, IReadOnlyList<string> Flags, string ProvenanceFile)
{
    /// <summary>True when qc's validator accepted the payload (false when it rejected it, or could not run).</summary>
    public bool Valid => Validate.StartsWith("valid", StringComparison.Ordinal);
}

/// <summary>
/// Stage 5 (aging's numbering): build qc's <c>qc-payload/1</c> into <c>&lt;run&gt;/05_qc/</c>, then, when the machine names
/// a Python with qctemplates installed, run qc's <c>validate</c> and <c>render</c> into the same folder. A rejected or
/// unrenderable payload is recorded as a flag, never raised: QC reporting must not stop a deposit from being delivered.
/// </summary>
public static class QcPayloadStage
{
    public const string StageName = "qc_payload";

    public static QcPayloadOutcome Run(string searchDir, string qcDir, string outDir, string accession, string? runDate,
        string workRoot, string paramsFile, string? qcPython, string? metamorpheusVersion = null)
    {
        Directory.CreateDirectory(outDir);
        var bins = QcBins.FromQcTemplates(qcPython);
        string searchProv = Path.Combine(searchDir, "provenance.json");
        string? mmVersion = metamorpheusVersion;
        if (mmVersion is null && File.Exists(searchProv))
        {
            var sp = JsonNode.Parse(File.ReadAllText(searchProv, Encoding.UTF8));
            mmVersion = sp?["tools"]?["MetaMorpheus"]?["release"]?.GetValue<string>() ?? sp?["params"]?["metamorpheus_version"]?.GetValue<string>();
        }
        var prov = new AgingProvenance(StageName, workRoot, paramsFile,
            new JsonObject { ["qc_python"] = qcPython, ["bin_edges_source"] = bins.Source }, runDate);
        prov.Upstream(searchProv);

        string runLabel = $"{runDate ?? ""}/{accession}".Trim('/');
        var result = QcPayloadBuilder.Build(searchDir, qcDir, accession, runLabel, mmVersion, bins);
        string task = Directory.EnumerateDirectories(Path.Combine(searchDir, "mm"), "Task*SearchTask").OrderBy(x => x, StringComparer.Ordinal).Last();
        prov.Input(Path.Combine(qcDir, "qc_report.json"));
        prov.Input(Path.Combine(task, "results.txt"));
        prov.Input(Path.Combine(task, "AllPSMs.psmtsv"));

        string payloadFile = Path.Combine(outDir, "qc_payload.json");
        File.WriteAllText(payloadFile, result.Payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }), new UTF8Encoding(false));
        prov.Output(payloadFile);

        var flags = new List<string>();
        string validate;
        string? render = null, findings = null;
        if (string.IsNullOrEmpty(qcPython))
        {
            validate = "not run: no qc_python in the machine file";
        }
        else
        {
            prov.Command(new[] { qcPython, "-m", "qctemplates", "validate", payloadFile });
            var (vrc, vout) = SafeRun(qcPython, new[] { "-m", "qctemplates", "validate", payloadFile });
            validate = vrc == 0 ? vout.Trim().Split('\n')[^1].Trim() : $"INVALID (exit {vrc}): {vout.Trim()}";
            if (vrc != 0) flags.Add("qc_payload_invalid");
            prov.Command(new[] { qcPython, "-m", "qctemplates", "render", payloadFile, outDir });
            var (rrc, rout) = SafeRun(qcPython, new[] { "-m", "qctemplates", "render", payloadFile, outDir });
            string t2 = Path.Combine(outDir, "tables", "T2_findings.tsv");
            if (rrc == 0 && File.Exists(t2))
            {
                render = rout.Trim().Split('\n')[^1].Trim();
                findings = t2;
                prov.Output(t2);
                string html = Path.Combine(outDir, "report.html");
                if (File.Exists(html)) prov.Output(html);
            }
            else
            {
                render = $"FAILED (exit {rrc}): {rout.Trim()}";
                flags.Add("qc_render_failed");
            }
        }
        prov.Set("files_described", (JsonNode)result.Files);
        prov.Set("protein_groups_quantified", result.ProteinGroupsQuantified is { } q ? (JsonNode)q : null);
        prov.Set("bin_edges_source", (JsonNode)bins.Source);
        prov.Set("qctemplates", new JsonObject
        {
            ["validate"] = validate, ["render"] = render,
            ["payload"] = Rel(payloadFile, workRoot), ["findings"] = findings is null ? null : Rel(findings, workRoot),
        });
        prov.Set("flags", new JsonArray(flags.Select(f => (JsonNode?)f).ToArray()));
        prov.Set("success", (JsonNode)true);
        string provFile = prov.Write(outDir);
        return new QcPayloadOutcome(payloadFile, result.Files, result.ProteinGroupsQuantified, bins.Source, validate, render, findings, flags, provFile);
    }

    private static (int, string) SafeRun(string python, string[] args)
    {
        try { return QcTemplates.Run(python, args, TimeSpan.FromMinutes(20)); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, e.Message); }
    }

    private static string Rel(string path, string workRoot)
    {
        string rel = Path.GetRelativePath(Path.GetFullPath(workRoot), Path.GetFullPath(path));
        return (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? Path.GetFullPath(path) : rel).Replace('\\', '/');
    }
}
