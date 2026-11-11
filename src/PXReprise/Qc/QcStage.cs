using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Provenance;

namespace PXReprise.Qc;

/// <summary>
/// The QC stage for one dataset: every .raw under the spectra directory (or only the files <c>only</c> names), one verdict
/// each, written as <c>qc_report.json</c> in the aging pipeline's shape (keyed by file name) with an
/// <c>aging-provenance/3</c> record.
/// </summary>
public static class QcStage
{
    /// <param name="only">File names to check, the rest of the directory ignored: the probe's own files (v0.4.12). A
    /// deposit downloaded ahead under STOP has every file on disk, and PXD026335's probe (2026-10-13) checked and
    /// searched all 20 of them until its 60-minute limit.</param>
    /// <param name="probe">This QC is the probe's (<c>02b_qc_probe</c>), whose report the probe search reads its per-file
    /// fragmentation from; <see cref="IonTrapHcdAsLowCid"/>.</param>
    /// <param name="kept">v0.4.45: raw files not on disk whose calibrated stand-in is searched (Search.CalibratedStandIns): each
    /// keeps its row of the earlier <c>qc_report.json</c> in <paramref name="outDir"/>, which read the raw before it was deleted.</param>
    public static (JsonObject Report, bool AllPass) Run(string spectraDir, string outDir, Profile profile, Machine machine,
        string paramsFile, string? runDate, IReadOnlySet<string>? only = null, bool probe = false, IReadOnlyCollection<string>? kept = null)
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
        // v0.4.34 (PLAN_tmt-dda-2 stage 4): a profile that takes them reads each file's kit, and accepts SPS-MS3.
        bool isobaric = profile.Accepts.Labellings.Contains(Discovery.Labelling.Isobaric);
        var report = new JsonObject();
        var reporterShares = new Dictionary<string, double>(StringComparer.Ordinal);
        bool lowCid = IonTrapHcdAsLowCid(profile, probe);
        if (lowCid && !profile.IonTrapHcdLowCid)
            prov.Note("HCD read in the ion trap is decided as LowCID for the probe search only (ion_trap_hcd_probe_lowcid); the full search's QC decides it as HCD at the low-res tolerance");
        foreach (string f in Directory.EnumerateFiles(spectraDir, "*.raw").Where(f => only is null || only.Contains(Path.GetFileName(f)))
                     .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            prov.Command(new[] { "mzLib.MsDataFileReader.GetDataFile", Path.GetFileName(f), "LoadAllStaticData" });
            var r = SpectraQc.Check(f, profile.Qc, refuseIsobaric, isobaric);
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
            // D32 (G24): the file's fragmentation from its own MS2 headers, written into its <raw>.toml at search time.
            if (profile.Fragmentation == "per_file" && r.Ms2AnalyzerDissociation is { } pairs)
            {
                var call = Fragmentation.Decide(pairs, profile.IonTrapHcd, lowCid);
                var entry = report[Path.GetFileName(f)]!.AsObject();
                entry["fragmentation"] = new JsonObject
                {
                    ["dissociation_type"] = call.DissociationType, ["low_res"] = call.LowRes,
                    ["unsupported"] = call.Unsupported, ["evidence"] = call.Evidence,
                };
                if (call.Unsupported is not null)
                {
                    entry["pass"] = false;
                    entry["fail_reasons"]!.AsArray().Add(SpectraQc.UnsupportedFragmentation);
                }
            }
            // Only when found: such a deposit is never ingested, so dataRepo's qc_report.json shape is unchanged.
            // Not under an isobaric profile: there the key would route the deposit away from the very profile that takes it
            // (BatchRunner.RoutedByReporters), and isobaric_kit says what it carries.
            if (!isobaric && r.Reporters?.Tag is { } tag)
                report[Path.GetFileName(f)]!["isobaric_reporters"] = new JsonObject
                    { ["tag"] = tag, ["fraction"] = r.Reporters.Fraction, ["spectra_checked"] = r.Reporters.SpectraChecked };
            if (r.Reporters is { } rep) reporterShares[Path.GetFileName(f)] = rep.Fraction;
            if (r.SpsMs3 is { } sps)
            {
                var entry = report[Path.GetFileName(f)]!.AsObject();
                entry["isobaric_kit"] = r.IsobaricKit;
                entry["sps_ms3"] = sps;
                if (r.IsobaricChannels is { } channels) entry["isobaric_channels"] = new JsonArray(channels.Select(c => (JsonNode?)c).ToArray());
            }
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
        string reportFile = Path.Combine(outDir, "qc_report.json");
        if (kept is { Count: > 0 } && File.Exists(reportFile) && JsonNode.Parse(File.ReadAllText(reportFile)) is JsonObject earlier)
        {
            var carried = kept.Where(n => !report.ContainsKey(n) && earlier[n] is JsonObject).Order(StringComparer.Ordinal).ToList();
            foreach (string n in carried) report[n] = earlier[n]!.DeepClone();
            if (carried.Count > 0)
            {
                report = new JsonObject(report.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())).ToList());
                prov.Set("verdicts_kept", new JsonArray(carried.Select(n => (JsonNode?)n).ToArray()));
                prov.Note($"{carried.Count} file(s) not read again: their raws were deleted once calibrated, and an interrupted search's calibration stands in for each (v0.4.45); each keeps its verdict from the earlier qc_report.json, which read the raw");
            }
        }
        if (isobaric && PassThinFractions(report) is { Count: > 0 } thin)
        {
            prov.Set("thin_fractions_passed", new JsonArray(thin.Select(n => (JsonNode?)n).ToArray()));
            prov.Note($"{thin.Count} file(s) passed with their deposit although fewer than {IsobaricReporters.MinFraction:0%} of their spectra carry reporters "
                      + $"({string.Join(", ", thin.Select(n => reporterShares.TryGetValue(n, out double s) ? $"{n} {s:0.0%}" : n))}): each shows the deposit's kit "
                      + "and every channel its passing files share, as the thin late fractions of a fractionated plex do (v0.4.45; PXD027464, the user, 2026-11-11)");
        }
        if (report.Count == 0)
            throw new UsageException(only is null ? $"no .raw files in {spectraDir}" : $"none of {string.Join(", ", only)} in {spectraDir}");
        if (report.Any(kv => kv.Value?["start_time"] is not null))
            prov.Note("start_time is the reader's value, unconverted: mzLib ThermoRawFileReader, from the .raw file header "
                      + "(FileHeader.CreationDate), which is the instrument's local clock with no time zone. Never compare it across deposits as UTC.");
        File.WriteAllText(reportFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        bool all = report.All(kv => kv.Value!["pass"]!.GetValue<bool>());
        prov.Set("all_pass", (JsonNode)all);
        prov.Output(reportFile);
        prov.Write(outDir);
        return (report, all);
    }

    /// <summary>
    /// v0.4.45 (PXD027464; the user, 2026-11-11, "(b)"): passes the thin fractions of an isobaric deposit. A file that failed only
    /// on <c>no_isobaric_reporters</c> but whose kit QC still read (<see cref="IsobaricReporters.MinFractionBesideItsDeposit"/>) passes
    /// when its kit is the deposit's (<see cref="IsobaricReporters.DepositKit"/> of the passing files, unchanged by it) and it shows
    /// every channel the passing files share (at least <see cref="IsobaricReporters.MinChannels"/>). PXD027464's F36 and F37 and
    /// PXD030407's IMAC_21, 22 and 24 are the late fractions of a plex whose other fractions pass, with the plex's kit and channels.
    /// A deposit with no passing labelled file passes nothing. Returns the files passed, in report order.
    /// </summary>
    internal static IReadOnlyList<string> PassThinFractions(JsonObject report)
    {
        static HashSet<string> Strings(JsonNode? a) => (a as JsonArray)?.Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        var passing = report.Select(kv => kv.Value).OfType<JsonObject>().Where(o => o["pass"]?.GetValue<bool>() == true && o["isobaric_kit"] is not null).ToList();
        if (passing.Count == 0 || passing.Any(o => o["isobaric_channels"] is null)) return Array.Empty<string>();
        var kits = passing.Select(o => o["isobaric_kit"]!.GetValue<string>()).ToList();
        string? deposit = IsobaricReporters.DepositKit(kits);
        var shared = passing.Select(o => Strings(o["isobaric_channels"])).Aggregate((a, b) => { a.IntersectWith(b); return a; });
        if (deposit is null || shared.Count < IsobaricReporters.MinChannels) return Array.Empty<string>();
        var passed = new List<string>();
        foreach (var (name, node) in report)
        {
            if (node is not JsonObject o || o["pass"]?.GetValue<bool>() != false) continue;
            if (!Strings(o["fail_reasons"]).SetEquals(new[] { SpectraQc.NoReporters })) continue;
            if (o["isobaric_kit"]?.GetValue<string>() is not { } kit || IsobaricReporters.DepositKit(kits.Append(kit)) != deposit) continue;
            if (!shared.IsSubsetOf(Strings(o["isobaric_channels"]))) continue;
            o["pass"] = true;
            o["fail_reasons"] = new JsonArray();
            passed.Add(name);
        }
        return passed;
    }

    /// <summary>Whether a QC decides HCD read in an ion trap as LowCID: in the probe and the search under
    /// <c>ion_trap_hcd_lowcid</c> (lowres@4), in the probe only under <c>ion_trap_hcd_probe_lowcid</c> (lowres@5). Under the
    /// probe's settings HCD finds far less than LowCID; in the full search LowCID lost 1-16% of PSMs (2026-10-21).</summary>
    public static bool IonTrapHcdAsLowCid(Profile profile, bool probe) => profile.IonTrapHcdLowCid || probe && profile.IonTrapHcdProbeLowCid;

    private static JsonObject Obj(IReadOnlyDictionary<string, int> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v;
        return o;
    }
}
