using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Provenance;

namespace PXReprise.Qc;

/// <summary>
/// The QC stage for one dataset: every .raw under the spectra directory, one verdict each, written as
/// <c>qc_report.json</c> in the aging pipeline's shape (keyed by file name) with an <c>aging-provenance/3</c> record.
/// </summary>
public static class QcStage
{
    public static (JsonObject Report, bool AllPass) Run(string spectraDir, string outDir, Profile profile, Machine machine,
        string paramsFile, string? runDate)
    {
        Directory.CreateDirectory(outDir);
        var section = JsonSerializer.SerializeToNode(new Dictionary<string, object>
        {
            ["min_fraction_orbitrap_hcd"] = profile.Qc.MinFractionOrbitrapHcd,
            ["min_ms2"] = profile.Qc.MinMs2,
        })!;
        var prov = new AgingProvenance("qc_spectra", machine.WorkRoot, paramsFile, section, runDate);
        string fetchProv = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spectraDir))!, "provenance.json");
        if (File.Exists(fetchProv)) prov.Upstream(fetchProv);

        // A profile that does not take isobaric labels refuses files whose spectra carry reporter ions (G4).
        bool refuseIsobaric = profile.Accepts.Labellings.Count > 0 && !profile.Accepts.Labellings.Contains(Discovery.Labelling.Isobaric);
        var report = new JsonObject();
        foreach (string f in Directory.EnumerateFiles(spectraDir, "*.raw").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            prov.Command(new[] { "mzLib.MsDataFileReader.GetDataFile", Path.GetFileName(f), "LoadAllStaticData" });
            var r = SpectraQc.Check(f, profile.Qc, refuseIsobaric);
            report[Path.GetFileName(f)] = r.FailReasons.Contains(SpectraQc.Unreadable)
                ? new JsonObject
                {
                    ["pass"] = false, ["fail_reasons"] = new JsonArray(SpectraQc.Unreadable), ["error"] = r.Error,
                    ["scans"] = null, ["ms2"] = 0, ["fraction_orbitrap_hcd"] = null, ["run_minutes"] = null,
                }
                : new JsonObject
                {
                    ["pass"] = r.Pass,
                    ["fail_reasons"] = new JsonArray(r.FailReasons.Select(x => (JsonNode?)x).ToArray()),
                    ["scans"] = r.Scans, ["ms2"] = r.Ms2, ["fraction_orbitrap_hcd"] = r.FractionOrbitrapHcd,
                    ["ms2_analyzer_dissociation"] = Obj(r.Ms2AnalyzerDissociation!),
                    ["run_minutes"] = r.RunMinutes,
                    ["charge_states"] = Obj(r.ChargeStates!),
                };
            // Only when found: such a deposit is never ingested, so dataRepo's qc_report.json shape is unchanged.
            if (r.Reporters?.Tag is { } tag)
                report[Path.GetFileName(f)]!["isobaric_reporters"] = new JsonObject
                    { ["tag"] = tag, ["fraction"] = r.Reporters.Fraction, ["spectra_checked"] = r.Reporters.SpectraChecked };
            // DATAREPO-72: a key only for a value the reader gave; a missing key means "not read".
            if (r.Run is { } run)
            {
                var entry = report[Path.GetFileName(f)]!.AsObject();
                if (run.StartTime is { } st) entry["start_time"] = st;
                if (run.InstrumentModel is { } im) entry["instrument_model"] = im;
                if (run.InstrumentModelAccession is { } ia) entry["instrument_model_accession"] = ia;
                if (run.InstrumentSerial is { } sn) entry["instrument_serial"] = sn;
            }
            if (r.FailReasons.Contains(SpectraQc.Unreadable))
                prov.Note($"{Path.GetFileName(f)}: unreadable; recorded as a failure and skipped");
            else prov.Input(f);
        }
        if (report.Count == 0) throw new UsageException($"no .raw files in {spectraDir}");
        if (report.Any(kv => kv.Value?["start_time"] is not null))
            prov.Note("start_time is the reader's value, unconverted: mzLib ThermoRawFileReader, from the .raw file header "
                      + "(FileHeader.CreationDate), which is the instrument's local clock with no time zone. Never compare it across deposits as UTC.");
        string reportFile = Path.Combine(outDir, "qc_report.json");
        File.WriteAllText(reportFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        bool all = report.All(kv => kv.Value!["pass"]!.GetValue<bool>());
        prov.Set("all_pass", (JsonNode)all);
        prov.Output(reportFile);
        prov.Write(outDir);
        return (report, all);
    }

    private static JsonObject Obj(IReadOnlyDictionary<string, int> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v;
        return o;
    }
}
