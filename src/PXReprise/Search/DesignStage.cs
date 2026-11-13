using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Omics.Modifications;
using Readers;

namespace PXReprise.Search;

/// <summary>
/// The label-free experimental design a search runs with (G15): <c>ExperimentalDesign.tsv</c> beside the spectra, from an
/// SDRF. The question's curated SDRF comes first (<c>[designs] dir</c>, one <c>&lt;PXD&gt;.sdrf.tsv</c> per deposit), then
/// the deposit's own. mzLib's <see cref="SdrfLabelFreeDesign"/> checks each the way MetaMorpheus reads it and refuses
/// rather than repairs, because MetaMorpheus skips quantification on a bad design with only a warning. Deposited SDRFs
/// rarely say more than the tissue: on the aging corpus 22 of 23 were refused (2026-10-04), so the curated one counts.
/// With neither usable, PXReprise writes its own default SDRF and the design comes from that (D31): no search is
/// design-less.
/// </summary>
public static class DesignStage
{
    public const string FileName = "ExperimentalDesign.tsv";

    /// <summary>MetaMorpheus's TMT design file, read from the same place as <see cref="FileName"/>.</summary>
    public const string TmtFileName = "TmtDesign.txt";

    /// <summary>
    /// v0.4.34 (PLAN_tmt-dda-2 stage 6): written beside a <see cref="TmtFileName"/> PXReprise wrote, holding its sha256, so
    /// <see cref="RemoveStale"/> can tell ours (removed) from one put there by hand (refused).
    /// </summary>
    public const string TmtMarkerName = TmtFileName + ".pxreprise";

    /// <summary>
    /// v0.4.17: removes an <see cref="FileName"/> left beside the spectra, before the probe search (which brings no design)
    /// and before <see cref="Prepare"/> writes one. A profile without a design still runs with one put there by hand, as
    /// before (D48, QuantProject U15 check it was used). MetaMorpheus 1.1.12's CMD reads a design beside the first spectra
    /// file on every run, quantifying or not, and stops with exit 5 when it does not fit the files given: on 2026-10-14 an
    /// interrupted full search's 61-file design failed PXD044227's 3-file re-probe (<c>Condition "all" biorep 2 is
    /// missing!</c>). A <see cref="TmtFileName"/> PXReprise wrote (<see cref="TmtMarkerName"/> holds its sha256) is removed
    /// too; any other is refused, never removed: it was put there by hand, and MetaMorpheus would read it.
    /// </summary>
    /// <returns>True when a stale design was removed.</returns>
    /// <exception cref="SearchSetupException">A TMT design beside the spectra that PXReprise did not write.</exception>
    public static bool RemoveStale(string spectraDir)
    {
        string tmt = Path.Combine(spectraDir, TmtFileName), marker = Path.Combine(spectraDir, TmtMarkerName);
        bool removed = false;
        if (File.Exists(tmt))
        {
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != Sha(tmt))
                throw new SearchSetupException($"{tmt} exists and was not written by PXReprise: MetaMorpheus would read it as this search's design");
            File.Delete(tmt);
            removed = true;
        }
        if (File.Exists(marker)) File.Delete(marker);
        string target = Path.Combine(spectraDir, FileName);
        if (!File.Exists(target)) return removed;
        File.Delete(target);
        return true;
    }

    /// <summary>
    /// The flag of an isobaric search run on PXReprise's default channel design, which v0.4.34 to v0.4.45 wrote when no SDRF
    /// said which sample is in which channel: each file its own plex, each channel its own sample. v0.4.46 has no default
    /// (<see cref="IsobaricDesignMissingException"/>); the flag is read in the provenance of the searches made before, which
    /// the catalog restricts for <c>quantitation</c> (<see cref="Batch.Restrictions"/>).
    /// </summary>
    public const string NoChannelDesign = "no_channel_design";

    /// <summary>
    /// v0.4.34 (PLAN_tmt-dda-2 stage 6): the channel design of an isobaric search, from the question's curated SDRF, else
    /// the deposit's own (<c>metadata/*sdrf*.tsv</c>), read by mzLib's <see cref="SdrfIsobaricDesign"/> against
    /// <paramref name="kit"/> (the kit QC read from the spectra; never guessed from the labels). The plex is what the SDRF
    /// states in a column mzLib's audit recognises (<see cref="SdrfQuantAuditor"/>), else one plex for the whole deposit,
    /// which mzLib refuses unless every file carries the same channel-to-sample map. Nothing else infers a plex: mzLib
    /// measured partitioning files by their samples right in 3 of 9 SDRFs (SdrfQuantAudit.ReadPlex). Null, with every
    /// SDRF's refusals in <paramref name="record"/>, when none gives a design (<see cref="PrepareIsobaric"/> then refuses the
    /// search, and the batch parks the deposit before its whole download).
    /// </summary>
    /// <param name="searchedFiles">The files the search reads (paths or names); rows naming other files are dropped.</param>
    public static SdrfIsobaricDesign? FindIsobaric(string metadataDir, IReadOnlyList<string> searchedFiles, string? curated,
        IReadOnlyList<string>? conditionColumns, string kit, out JsonObject record)
    {
        if (!IsobaricMassTag.TryGetIsobaricMassTag(kit, out var tag) || tag is null)
            throw new SearchSetupException($"the kit {kit} is not one mzLib defines");
        var refused = new JsonArray();
        var candidates = new List<(string Source, string Sdrf, IReadOnlyList<string>? Columns)>();
        if (curated is not null && File.Exists(curated)) candidates.Add(("question", curated, conditionColumns));
        if (Directory.Exists(metadataDir))
            candidates.AddRange(Directory.EnumerateFiles(metadataDir, "*.tsv").Where(f => Path.GetFileName(f).Contains("sdrf", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).Select(f => ("deposit", f, (IReadOnlyList<string>?)null)));
        foreach (var (source, sdrf, columns) in candidates)
        {
            var d = ReadIsobaric(sdrf, searchedFiles, columns, tag, out string plexRule, out var scope, out string? error);
            if (d is { IsValid: true })
            {
                record = IsobaricRecord(source, sdrf, d, plexRule, kit);
                record["scope"] = scope;
                if (refused.Count > 0) record["sdrfs_refused"] = refused;
                return d;
            }
            refused.Add(new JsonObject
            {
                ["source"] = source, ["sdrf"] = Path.GetFileName(sdrf), ["plex_rule"] = plexRule,
                ["refusals"] = new JsonArray((error is null ? d!.Refusals.Take(5) : new[] { error }).Select(x => (JsonNode?)x).ToArray()),
                ["scope"] = scope,
            });
        }
        record = new JsonObject
        {
            ["source"] = null, ["written"] = false, ["kit"] = kit,
            ["reason"] = candidates.Count == 0 ? "no curated design and no deposited SDRF" : "every SDRF was refused",
            ["sdrfs_refused"] = refused,
        };
        return null;
    }

    /// <summary>
    /// <see cref="FindIsobaric"/>, then writes <see cref="TmtFileName"/> beside the spectra (and its
    /// <see cref="TmtMarkerName"/>), after removing any stale design. Returns its provenance record.
    /// </summary>
    /// <remarks>
    /// v0.4.46 (G69; the user, 2026-11-11): there is no default design. Until v0.4.45 a deposit no SDRF described was searched
    /// with each file its own plex, so a fraction or a repeat injection of one plex became a new plex, and its channels new
    /// samples (PXD034564, one TMT10 plex in 32 files, came out as 32 plexes and 320 samples). The user: "These are definitely
    /// not plexes ... These fractions also must correctly be collapsed ... I think this must be in SDRF." So the files' plexes,
    /// fractions and technical replicates come from an SDRF that states them, or the search does not run.
    /// </remarks>
    /// <exception cref="IsobaricDesignMissingException">No SDRF gives a design: the deposit waits for one.</exception>
    /// <exception cref="SearchSetupException">A curated design MetaMorpheus would not accept (fixed, not searched
    /// around); a TMT design put there by hand.</exception>
    public static JsonObject PrepareIsobaric(string spectraDir, IReadOnlyList<string> searchedFiles, string? curated,
        IReadOnlyList<string>? conditionColumns, string kit)
    {
        RemoveStale(spectraDir);
        string metadata = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spectraDir))!, "metadata");
        var d = FindIsobaric(metadata, searchedFiles, curated, conditionColumns, kit, out var record);
        if (curated is not null && File.Exists(curated) && record["source"]?.GetValue<string>() != "question")
            throw new SearchSetupException($"the question's design {curated} is refused: "
                + string.Join(" | ", record["sdrfs_refused"]!.AsArray().First(x => x!["source"]!.GetValue<string>() == "question")!["refusals"]!.AsArray().Select(r => r!.GetValue<string>())));
        if (d is null)
            throw new IsobaricDesignMissingException(MissingDesign(record), record);
        string target = Path.Combine(spectraDir, TmtFileName);
        d.WriteTmtDesign(target);
        if (MarkUnnamedChannelsEmpty(target, kit) is { Count: > 0 } empty)
            record["channels_without_sample"] = new JsonObject(empty.Select(kv =>
                KeyValuePair.Create(kv.Key, (JsonNode?)new JsonArray(kv.Value.Select(c => (JsonNode?)c).ToArray()))));
        File.WriteAllText(Path.Combine(spectraDir, TmtMarkerName), Sha(target));
        record["written"] = true;
        record["design"] = target;
        record["design_sha256"] = Sha(target);
        return record;
    }

    /// <summary>
    /// v0.4.47 (2026-11-13): gives every channel of the kit that a file's rows leave out a row of its own in that file, with no
    /// sample name, no condition and <c>Sample Type</c> <c>empty</c>, and every other row <c>study sample</c>, in the kit's channel
    /// order. An SDRF names only the channels that hold the study's samples, but MetaMorpheus writes a quantity column for every
    /// channel of the kit, and datarepo refuses a column no design row labels: PXD060120 (12 of TMTpro's 18 named) from
    /// 2026-11-12, PXD035689 (one TMT10 plex without 131) on 2026-11-13. G84 (v0.4.41) did this for the default design only,
    /// which v0.4.46 retired. mzLib's SDRF reader has no way to say empty (its MAP-08), hence here and not in the SDRF.
    /// MetaMorpheus requires a biological replicate of at least 1 on any row, so these take the numbers after the file's samples.
    /// A design that names every channel is left as it is (no column added). Returns the channels marked, by file name.
    /// </summary>
    private static SortedDictionary<string, List<string>> MarkUnnamedChannelsEmpty(string tmtDesign, string kit)
    {
        if (!IsobaricMassTag.TryGetIsobaricMassTag(kit, out var tag) || tag is null)
            throw new SearchSetupException($"the kit {kit} is not one mzLib defines");
        var lines = File.ReadAllLines(tmtDesign);
        var header = lines[0].Split('\t');
        int Column(string name) => Array.IndexOf(header, name);
        int file = Column("File"), sample = Column("Sample Name"), channel = Column("TMT Channel"), condition = Column("Condition"), biorep = Column("Biological Replicate");
        if (new[] { file, sample, channel, condition, biorep }.Any(i => i < 0) || Column(SampleTypeColumn) >= 0)
            throw new SearchSetupException($"{tmtDesign} has not the columns mzLib writes: {lines[0]}");
        var order = tag.ChannelLabels.Select((label, i) => (label, i)).ToDictionary(x => x.label, x => x.i, StringComparer.Ordinal);
        var byFile = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t')).GroupBy(r => r[file], StringComparer.Ordinal).ToList();
        var marked = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var rows in byFile)
        {
            var named = rows.Select(r => r[channel]).ToHashSet(StringComparer.Ordinal);
            var unknown = named.Where(c => !order.ContainsKey(c)).ToList();
            if (unknown.Count > 0)
                throw new SearchSetupException($"{tmtDesign}: {rows.Key} names channel(s) {string.Join(", ", unknown)}, which the kit {kit} has not");
            var missing = tag.ChannelLabels.Where(c => !named.Contains(c)).ToList();
            if (missing.Count > 0) marked[Path.GetFileName(rows.Key)] = missing;
        }
        if (marked.Count == 0) return marked;
        var output = new List<string> { string.Join('\t', header.Append(SampleTypeColumn)) };
        foreach (var rows in byFile)
        {
            var samples = rows.Select(r => r.Append("study sample").ToArray()).ToList();
            int next = samples.Max(r => int.Parse(r[biorep], CultureInfo.InvariantCulture));
            var empties = (marked.TryGetValue(Path.GetFileName(rows.Key), out var missing) ? missing : new List<string>()).Select(c =>
            {
                var row = samples[0].ToArray();
                row[sample] = "";
                row[channel] = c;
                row[condition] = "";
                row[biorep] = (++next).ToString(CultureInfo.InvariantCulture);
                row[^1] = "empty";
                return row;
            });
            output.AddRange(samples.Concat(empties).OrderBy(r => order[r[channel]]).Select(r => string.Join('\t', r)));
        }
        File.WriteAllLines(tmtDesign, output);
        return marked;
    }

    private const string SampleTypeColumn = "Sample Type";

    /// <summary>Why an isobaric deposit has no design, from <see cref="FindIsobaric"/>'s record: said in the park's detail.</summary>
    public static string MissingDesign(JsonObject record)
    {
        int refused = record["sdrfs_refused"]?.AsArray().Count ?? 0;
        return $"no SDRF states the {record["kit"]} files' plexes, fractions and technical replicates ({record["reason"]}"
            + (refused > 0 ? $"; {refused} SDRF(s) refused, the first: {record["sdrfs_refused"]![0]!["refusals"]?.AsArray().FirstOrDefault()?.GetValue<string>()}" : "")
            + "); PXReprise has no default isobaric design (G69), so the deposit waits for an SDRF that states them";
    }

    private static JsonObject IsobaricRecord(string source, string sdrf, SdrfIsobaricDesign d, string plexRule, string kit) => new()
    {
        ["source"] = source, ["written"] = false, ["sdrf"] = sdrf, ["sdrf_sha256"] = Sha(sdrf), ["kit"] = kit,
        ["plex_rule"] = plexRule, ["plexes"] = new JsonArray(d.PlexIds.Keys.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray()),
        ["file_key_column"] = d.FileKeyColumn,
        ["condition_columns"] = new JsonArray(d.ConditionColumns.Select(c => (JsonNode?)c).ToArray()),
        ["conditions"] = new JsonArray(d.Files.SelectMany(f => f.Channels).Select(c => c.Condition).Distinct().Order(StringComparer.Ordinal).Select(c => (JsonNode?)c).ToArray()),
        ["files"] = d.Files.Count,
        ["notes"] = new JsonArray(d.Notes.Select(n => (JsonNode?)n).ToArray()),
        ["report"] = d.Report(),
    };

    /// <remarks>
    /// The SDRF is first restricted to this search's files by mzLib's <see cref="SdrfSearchScope"/> (QuantProject 085): an
    /// SDRF an earlier search wrote names that search's files (<c>X-calib.mzML</c>) in <c>comment[searched data file]</c>,
    /// the column the reader keys on, and only the scope replaces them with this search's names. What the scope could not
    /// join goes in <paramref name="scope"/>.
    /// </remarks>
    private static SdrfIsobaricDesign? ReadIsobaric(string sdrf, IReadOnlyList<string> searchedFiles, IReadOnlyList<string>? conditionColumns,
        IsobaricMassTag tag, out string plexRule, out JsonObject? scope, out string? error)
    {
        error = null;
        scope = null;
        plexRule = "none";
        try
        {
            var audit = SdrfQuantAuditor.Audit(sdrf);
            string? column = audit.PlexSource == SdrfPlexSource.Column ? audit.PlexEvidence : null;
            plexRule = column is null ? "single: no plex column, so one plex, which mzLib accepts only if every file has the same channels" : $"column: {column}";
            var scoped = SdrfSearchScope.Restrict(new SdrfDocument(sdrf), searchedFiles);
            scope = new JsonObject
            {
                ["searched_without_row"] = Names(scoped.SearchedWithoutRow), ["dropped_data_files"] = Names(scoped.DroppedDataFiles),
                ["ambiguous"] = Names(scoped.Ambiguous), ["shared_stem"] = Names(scoped.SharedStem),
            };
            return SdrfIsobaricDesign.Read(scoped.Document, new SdrfIsobaricDesignOptions
            {
                Tag = tag, PlexColumn = column, SinglePlex = column is null ? "1" : null,
                ConditionColumns = conditionColumns, SearchedFiles = searchedFiles.ToList(),
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode?)n).ToArray());

    /// <summary>
    /// Writes the design, or removes any stale one, and returns its provenance record (<c>written</c> says which).
    /// </summary>
    /// <param name="searchedFiles">The spectra files the search reads; rows for any other file are dropped.</param>
    /// <param name="curated">The question's SDRF for this deposit; ignored when the file does not exist.</param>
    /// <param name="conditionColumns">The factor columns a curated design's condition is built from; null for its only one.</param>
    /// <param name="organism">The profile's organism key, written into a default SDRF.</param>
    /// <param name="accession">The deposit's accession, naming a default SDRF.</param>
    /// <exception cref="SearchSetupException">A curated design MetaMorpheus would not accept. It is fixed, not searched around.</exception>
    public static JsonObject Prepare(string spectraDir, IReadOnlyList<string> searchedFiles, string? curated,
        IReadOnlyList<string>? conditionColumns, string? organism = null, string? accession = null)
    {
        string target = Path.Combine(spectraDir, FileName);
        RemoveStale(spectraDir);   // never one left by an earlier attempt or by hand
        if (curated is not null && File.Exists(curated))
        {
            var d = Read(curated, searchedFiles, conditionColumns, out string? error);
            if (d is null || !d.IsValid)
                throw new SearchSetupException($"the question's design {curated} is refused: {error ?? string.Join(" | ", d!.Refusals.Take(5))}");
            d.WriteExperimentalDesignTsv(target);
            return Record("question", curated, d);
        }
        var refused = new JsonArray();
        string metadata = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spectraDir))!, "metadata");
        var deposited = Directory.Exists(metadata)
            ? Directory.EnumerateFiles(metadata, "*.tsv").Where(f => Path.GetFileName(f).Contains("sdrf", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToList()
            : new List<string>();
        foreach (string s in deposited)
        {
            var d = Read(s, searchedFiles, null, out string? error);
            if (d is { IsValid: true })
            {
                d.WriteExperimentalDesignTsv(target);
                var rec = Record("deposit", s, d);
                if (refused.Count > 0) rec["deposit_sdrfs_refused"] = refused;
                return rec;
            }
            refused.Add(new JsonObject
            {
                ["sdrf"] = Path.GetFileName(s),
                ["refusals"] = new JsonArray((error is null ? d!.Refusals.Take(5) : new[] { error }).Select(x => (JsonNode?)x).ToArray()),
            });
        }
        // D31 (the user, 2026-10-07): every searched set of files gets an SDRF and a design. With neither SDRF usable, ours
        // says only what design-less quantification assumes, on the record: one condition, each file its own sample.
        string why = deposited.Count == 0 ? "no curated design and no deposited SDRF" : "no curated design; every deposited SDRF was refused";
        string fallback = WriteDefaultSdrf(spectraDir, searchedFiles, organism, accession);
        var made = Read(fallback, searchedFiles, null, out string? fallbackError);
        if (made is null || !made.IsValid)
            throw new SearchSetupException($"PXReprise's default SDRF {fallback} is refused: {fallbackError ?? string.Join(" | ", made!.Refusals.Take(5))}");
        made.WriteExperimentalDesignTsv(target);
        var record = Record(DefaultSource, fallback, made);
        record["reason"] = why;
        if (refused.Count > 0) record["deposit_sdrfs_refused"] = refused;
        return record;
    }

    /// <summary>The provenance <c>source</c> of a design from <see cref="WriteDefaultSdrf"/>.</summary>
    public const string DefaultSource = "pxreprise_default";

    /// <summary>The condition every file of a default design is in.</summary>
    public const string DefaultCondition = "all";

    /// <summary>
    /// Writes PXReprise's own SDRF for the searched files (D31): one row per file, all in <see cref="DefaultCondition"/>,
    /// each file its own biological replicate, fraction 1, technical replicate 1. It sits in <c>pxreprise_sdrf</c> beside
    /// the fetch's <c>metadata</c>, never in it, so a later attempt does not read it back as the deposit's. A fractionated
    /// deposit is wrong under it (each fraction a sample) until a curated SDRF or sdrf's drafter replaces it.
    /// </summary>
    private static string WriteDefaultSdrf(string spectraDir, IReadOnlyList<string> searchedFiles, string? organism, string? accession)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spectraDir))!, "pxreprise_sdrf");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{(string.IsNullOrEmpty(accession) ? "deposit" : accession)}.{DefaultSource}.sdrf.tsv");
        var lines = new List<string>
        {
            "source name\tcharacteristics[organism]\tcharacteristics[biological replicate]\tassay name\tcomment[label]\t"
            + "comment[data file]\tcomment[technical replicate]\tcomment[fraction identifier]\tfactor value[group]",
        };
        int i = 0;
        foreach (string f in searchedFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            i++;
            lines.Add($"sample {i}\t{OrganismName(organism)}\t{i}\trun {i}\tlabel free sample\t{f}\t1\t1\t{DefaultCondition}");
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>The SDRF spelling of a profile's organism key; anything else is written as given, or "not available".</summary>
    private static string OrganismName(string? organism) => organism?.ToLowerInvariant() switch
    {
        null or "" => "not available",
        "human" => "homo sapiens",
        "mouse" => "mus musculus",
        "rat" => "rattus norvegicus",
        var o => o,
    };

    private static SdrfLabelFreeDesign? Read(string sdrf, IReadOnlyList<string> searchedFiles, IReadOnlyList<string>? conditionColumns, out string? error)
    {
        error = null;
        try
        {
            return SdrfLabelFreeDesign.Read(sdrf, new SdrfLabelFreeDesignOptions { SearchedFiles = searchedFiles.ToList(), ConditionColumns = conditionColumns });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A file that is not an SDRF at all: as much a refusal as a bad one, and recorded the same way.
            error = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    private static JsonObject Record(string source, string sdrf, SdrfLabelFreeDesign d) => new()
    {
        ["source"] = source, ["written"] = true, ["sdrf"] = sdrf, ["sdrf_sha256"] = Sha(sdrf),
        ["file_key_column"] = d.FileKeyColumn,
        ["condition_columns"] = new JsonArray(d.ConditionColumns.Select(c => (JsonNode?)c).ToArray()),
        ["conditions"] = new JsonArray(d.Files.Select(f => f.Condition).Distinct().Order(StringComparer.Ordinal).Select(c => (JsonNode?)c).ToArray()),
        ["files"] = d.Files.Count,
        ["notes"] = new JsonArray(d.Notes.Select(n => (JsonNode?)n).ToArray()),
        // QuantProject 012 (PXR-U1): mzLib's whole account, kept per deposit. Since mzLib 1.0.596 (#1426, v0.4.33) the
        // SDRF's own replicate and fraction numbers are kept, gaps included; MetaMorpheus 1.1.13 quantifies a gap (#2928),
        // 1.1.12 refuses it, so this engine must not search a 1.1.12 profile with a gapped SDRF.
        ["report"] = d.Report(),
    };

    /// <summary>
    /// The numbering gaps in an ExperimentalDesign.tsv, in MetaMorpheus 1.1.13's own wording (#2928,
    /// ExperimentalDesign.AddGapWarning): a condition's biological replicates, a replicate's fractions, a fraction's
    /// technical replicates. Empty when every numbering runs 1..n. MetaMorpheus 1.1.12 refuses a design with any gap.
    /// </summary>
    public static List<string> Gaps(string designFile)
    {
        var rows = File.ReadAllLines(designFile).Skip(1).Select(l => l.TrimEnd('\r').Split('\t')).Where(c => c.Length >= 5)
            .Select(c => (Condition: c[1], Bio: int.Parse(c[2], CultureInfo.InvariantCulture), Fraction: int.Parse(c[3], CultureInfo.InvariantCulture), Tech: int.Parse(c[4], CultureInfo.InvariantCulture)))
            .ToList();
        var gaps = new List<string>();
        void Add(string owner, string level, IEnumerable<int> numbers)
        {
            var present = numbers.Distinct().Order().ToList();
            var missing = Enumerable.Range(1, Math.Max(0, present.LastOrDefault())).Except(present).ToList();
            if (missing.Count > 0) gaps.Add($"{owner}: {level}s {string.Join(", ", present)}; missing {string.Join(", ", missing)}");
        }
        foreach (var c in rows.GroupBy(r => r.Condition).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Add($"Condition \"{c.Key}\"", "biological replicate", c.Select(r => r.Bio));
            foreach (var b in c.GroupBy(r => r.Bio).OrderBy(g => g.Key))
            {
                Add($"Condition \"{c.Key}\" biorep {b.Key}", "fraction", b.Select(r => r.Fraction));
                foreach (var f in b.GroupBy(r => r.Fraction).OrderBy(g => g.Key))
                    Add($"Condition \"{c.Key}\" biorep {b.Key} fraction {f.Key}", "technical replicate", f.Select(r => r.Tech));
            }
        }
        return gaps;
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}

/// <summary>
/// v0.4.46 (G69): an isobaric search whose files no SDRF assigns to plexes, fractions and technical replicates. Not a
/// failure of the deposit: it waits for an SDRF that states them (the batch parks it <c>waiting_isobaric_design</c>).
/// <see cref="Record"/> is <see cref="DesignStage.FindIsobaric"/>'s, with every SDRF's refusals.
/// </summary>
public sealed class IsobaricDesignMissingException(string message, JsonObject record) : SearchSetupException(message)
{
    public JsonObject Record { get; } = record;
}
