using System.Text.RegularExpressions;
using DataRepo.Bundle;

namespace DataRepo.Ingest.Sources;

/// <summary>MetaMorpheus <c>.psmtsv</c> to Psm, Peptidoform, Protein and PtmSite rows.</summary>
/// <remarks>
/// <para>Every PSM the search wrote is stored, decoys and above-threshold matches included. Filtering is a
/// question a caller asks (<c>q_value &lt;= 0.01 and target_decoy = 'target'</c>), not a decision the
/// repository takes for them, and keeping the whole table is what lets a bundle reconcile its own counts
/// against the producer's summary.</para>
/// <para>Ambiguity is carried rather than resolved. MetaMorpheus separates alternatives with <c>|</c> when it
/// cannot place a modification or choose between sequences; the first alternative becomes the stored
/// peptidoform and <c>ambiguity_level</c> says what it is. Ported from <c>sources/identifications.py</c>.</para>
/// </remarks>
public static class Identifications
{
    private static readonly Regex Range = new(@"\[(\d+)\s+to\s+(\d+)\]", RegexOptions.CultureInvariant);

    /// <summary>Producer ambiguity levels, best first. A site is credited with the best level that placed it.</summary>
    public static readonly IReadOnlyList<string> AmbiguityOrder = ["1", "2A", "2B", "2C", "2D", "3", "4", "5"];

    /// <summary>MetaMorpheus prefixes the accessions of its reversed decoy entries.</summary>
    public const string DecoyPrefix = "DECOY_";

    /// <summary>MetaMorpheus accepts a match on BOTH its q-value and its notch q-value, which is why a count
    /// filtered on <c>q_value</c> alone does not reproduce its own results.txt (DATAREPO-14).</summary>
    public const double ProducerThreshold = 0.01;

    /// <summary>A search that cannot settle on one notch reports its candidates separated by this.</summary>
    public const string NotchSeparator = "|";

    private static string PyStr(object? value) => value is null ? "None" : PyFormat.Str(value);

    /// <summary>One producer cell exactly as written, or null when it is absent.</summary>
    private static string? Verbatim(object? value)
    {
        if (value is null) return null;
        var text = PyFormat.Str(value).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>MetaMorpheus separates alternatives with <c>|</c>; take the first and keep the level column.</summary>
    private static string First(object? value) =>
        (value is null ? "" : PyFormat.Str(value)).Split('|', 2)[0].Trim();

    private static long? Int(object? value)
    {
        if (!PyFormat.TryParseFloat(First(value), out var v) || double.IsNaN(v)) return null;
        if (double.IsInfinity(v)) throw new OverflowException("cannot convert float infinity to integer");
        return (long)Math.Truncate(v);
    }

    private static double? Float(object? value) =>
        PyFormat.TryParseFloat(First(value), out var v) && !double.IsNaN(v) ? v : null;

    /// <summary><c>D</c>, <c>C</c>, <c>T</c> (possibly <c>|</c>-joined) to the schema's TargetDecoy.</summary>
    /// <remarks>Decoy wins over contaminant, and contaminant over target: the least trustworthy label on an
    /// ambiguous match is the one a reader needs to see.</remarks>
    private static string TargetDecoy(object? value)
    {
        var text = (value is null ? "" : PyFormat.Str(value)).ToUpperInvariant();
        if (text.Contains('D')) return "decoy";
        if (text.Contains('C')) return "contaminant";
        return "target";
    }

    private static (long? Start, long? End) ResidueRange(object? value)
    {
        var m = Range.Match(value is null ? "" : PyFormat.Str(value));
        return m.Success ? (long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value)) : (null, null);
    }

    /// <summary>The lower of two producer ambiguity levels, tolerating one this list does not know.</summary>
    private static string? BetterLevel(string? current, string? candidate)
    {
        if (candidate is null) return current;
        if (current is null) return candidate;
        int Rank(string name)
        {
            var i = -1;
            for (var k = 0; k < AmbiguityOrder.Count; k++) if (AmbiguityOrder[k] == name) i = k;
            return i < 0 ? AmbiguityOrder.Count : i;
        }
        return Rank(candidate) < Rank(current) ? candidate : current;
    }

    /// <summary>Every start residue in a <c>[a to b]|[c to d]</c> field.</summary>
    /// <remarks><b>Not aligned with the accession column.</b> MetaMorpheus de-duplicates the spans and repeats
    /// one per occurrence, so the list can be shorter or longer than the accessions (aging 043).</remarks>
    private static List<long> ResidueStarts(object? value) =>
        Range.Matches(value is null ? "" : PyFormat.Str(value)).Select(m => long.Parse(m.Groups[1].Value)).ToList();

    /// <summary>One value per accession from a <c>|</c>-joined producer cell, or null where it cannot be aligned.</summary>
    /// <remarks><b>MetaMorpheus collapses a column to a single entry when every protein on the row shares it</b>,
    /// while <c>Accession</c> keeps them all. Counts match: zip. One entry for many accessions: broadcast, the
    /// producer collapsed it because they agree. Anything else: null for all, because a positional guess would
    /// put a real gene symbol on the wrong protein, and a wrong gene reads as a fact where a null reads as
    /// "not recorded". It cost 2,678 proteins their species once (0.13.0).</remarks>
    private static List<string?> PerAccession(object? value, int count)
    {
        var parts = (IsFalsy(value) ? "" : PyFormat.Str(value!)).Split('|').Select(p => p.Trim()).ToList();
        if (parts.Count == count) return parts.Select(p => p.Length == 0 ? null : p).ToList();
        if (parts.Count == 1) return Enumerable.Repeat(parts[0].Length == 0 ? null : parts[0], count).ToList();
        return Enumerable.Repeat<string?>(null, count).ToList();
    }

    /// <summary>Python truthiness for a cell value.</summary>
    private static bool IsFalsy(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        long l => l == 0,
        double d => d == 0,
        _ => false,
    };

    private static List<string> Accessions(object? value) =>
        (value is null ? "" : PyFormat.Str(value)).Split('|').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

    /// <summary>The first of several column names a reader uses for one field; an empty column counts as absent.</summary>
    private static List<object?>? Column(IReadOnlyDictionary<string, List<object?>> columns, params string[] names)
    {
        foreach (var name in names)
            if (columns.TryGetValue(name, out var c)) return c;
        return null;
    }

    private static List<object?> Or(List<object?>? column, int n, object? fill) =>
        column is { Count: > 0 } ? column : Enumerable.Repeat(fill, n).ToList();

    private static List<object?> Get(IReadOnlyDictionary<string, List<object?>> columns, string name, int n, object? fill = null) =>
        Or(columns.TryGetValue(name, out var c) ? c : null, n, fill);

    private static int Length(IReadOnlyDictionary<string, List<object?>> columns, string name) =>
        columns.TryGetValue(name, out var c) ? c.Count : 0;

    /// <summary>Builds Psm rows from the columns mzLib read out of <c>AllPSMs.psmtsv</c>.</summary>
    /// <param name="search">Which search produced these matches, e.g. <c>gptmd</c>.</param>
    /// <returns>One row per PSM, ordered as the file was.</returns>
    public static List<Row> PsmRows(
        IReadOnlyDictionary<string, List<object?>> columns, string datasetId, ProformaCache proforma, RunNameMap runNames, string search)
    {
        var n = Length(columns, "full_sequence");
        var files = Or(Column(columns, "file_name_without_extension", "file_name"), n, "");
        var scans = Or(Column(columns, "one_based_scan_number", "ms2_scan_number"), n, null);
        var full = columns.TryGetValue("full_sequence", out var f) ? f : Enumerable.Repeat<object?>("", n).ToList();
        var charges = Or(Column(columns, "precursor_charge", "charge_state"), n, null);
        var accession = Or(Column(columns, "accession", "protein_accession"), n, "");
        var ranges = Get(columns, "start_and_end_residues_in_parent_sequence", n, "");
        var precursorMz = Get(columns, "precursor_mz", n);
        var retention = Get(columns, "retention_time", n);
        var score = Get(columns, "score", n);
        var delta = Get(columns, "delta_score", n);
        var q = Get(columns, "q_value", n);
        var qNotch = Get(columns, "q_value_notch", n);
        var notch = Get(columns, "notch", n);
        var pep = Get(columns, "pep", n);
        var pepQ = Get(columns, "pep_q_value", n);
        var massDiff = Get(columns, "mass_diff_ppm", n);
        var status = Get(columns, "decoy_contam_target", n);
        var levels = Get(columns, "ambiguity_level", n);
        var missed = Get(columns, "missed_cleavage", n);

        var seen = new Dictionary<(string, long?), long>();
        var rows = new List<Row>(n);
        for (var i = 0; i < n; i++)
        {
            var reportedRun = IsFalsy(files[i]) ? "" : PyFormat.Str(files[i]!);
            var deposited = runNames.Resolve(reportedRun);
            var runId = string.IsNullOrEmpty(deposited) ? $"{datasetId}:{reportedRun}" : $"{datasetId}:{deposited}";
            var scan = Int(scans[i]);
            var key = (runId, scan);
            var rank = seen[key] = seen.GetValueOrDefault(key) + 1;

            var parsed = proforma.Get(First(full[i]));
            var charge = Int(charges[i]);
            var (start, end) = ResidueRange(ranges[i]);
            var usi = !string.IsNullOrEmpty(deposited) && scan is not null && charge is not null && !string.IsNullOrEmpty(parsed.Proforma)
                ? Usi.Mint(datasetId, deposited, scan.Value, parsed.Proforma, charge.Value)
                : null;
            var level = First(levels[i]);
            rows.Add(new Row
            {
                ["psm_id"] = $"{runId}:{(scan is null ? "None" : scan.Value.ToString())}:{rank}",
                ["run_id"] = runId,
                ["scan"] = scan,
                ["usi"] = usi,
                ["peptidoform"] = parsed.Proforma,
                ["base_sequence"] = parsed.BaseSequence,
                ["precursor_charge"] = charge,
                ["precursor_mz"] = Float(precursorMz[i]),
                ["retention_time_min"] = Float(retention[i]),
                ["score"] = Float(score[i]),
                ["delta_score"] = Float(delta[i]),
                ["q_value"] = Float(q[i]),
                ["q_value_notch"] = Float(qNotch[i]),
                // Verbatim, not First(): the whole point of this column is to carry the unresolved candidates
                // that notch_ambiguous is derived from.
                ["notch"] = Verbatim(notch[i]),
                ["notch_ambiguous"] = NotchAmbiguous(notch[i]),
                ["pep"] = Float(pep[i]),
                ["pep_q_value"] = Float(pepQ[i]),
                ["mass_error_ppm"] = Float(massDiff[i]),
                ["target_decoy"] = TargetDecoy(status[i]),
                ["protein_accessions"] = Accessions(accession[i]).Cast<object?>().ToList(),
                ["ambiguity_level"] = level.Length == 0 ? null : level,
                ["localization_score"] = null,
                ["search"] = search,
                ["missed_cleavages"] = Int(missed[i]),
                ["nonspecific_termini"] = null,
                ["start_residue"] = start,
                ["end_residue"] = end,
                // The matched-ion fields are composite and were never projected as columns (DATAREPO-13).
                ["matched_ion_series"] = null,
                ["matched_ion_count"] = null,
            });
        }
        return rows;
    }

    /// <summary>Where an accession came from. Decoy entries are kept, since PSM rows point at them and FDR
    /// cannot be recomputed without them, but they are not UniProt entries.</summary>
    private static string SourceDb(string accession, bool contaminant)
    {
        if (accession.ToUpperInvariant().StartsWith(DecoyPrefix, StringComparison.Ordinal)) return "decoy";
        return contaminant ? "contaminants" : "uniprot";
    }

    /// <summary>The NCBITaxon term for one database entry, or null where we do not have one.</summary>
    /// <remarks>The dataset's organism is evidence about the searched proteome and nothing else: a contaminant
    /// is bovine, porcine or bacterial by design, and a reversed decoy is no organism's protein. No
    /// name-to-taxon mapping happens here (D1, G36).</remarks>
    private static string? EntryTaxon(string sourceDb, string? datasetOrganism) =>
        sourceDb == "uniprot" ? datasetOrganism : null;

    private static string Canonical(string acc) => acc.Contains('-') ? acc.Split('-')[0] : acc;

    /// <summary>Builds Protein rows from the accession/name/gene columns of the identification files.</summary>
    /// <remarks>
    /// <para><b>The label is per accession, and the column is not</b> (G66, 0.19.0). MetaMorpheus writes
    /// <c>Decoy/Contaminant/Target</c> once per peptide-protein match and collapses it only when every match
    /// agrees, while <c>Accession</c> is de-duplicated, so the two cells cannot be zipped. One letter holds
    /// for every accession on the row; several are decided per accession by <paramref name="contaminantOf"/>,
    /// and where that cannot say, the row rule is kept and counted in <paramref name="unresolved"/>.</para>
    /// <para><b>The row's own organism wins over the dataset's.</b> All 339 contaminants once read
    /// <c>NCBITaxon:9606</c>, bovine albumin included.</para>
    /// </remarks>
    public static List<Row> ProteinRows(
        IEnumerable<IReadOnlyDictionary<string, List<object?>>> columnsList, string datasetId, string? organism,
        Func<string, bool?>? contaminantOf = null, Dictionary<string, long>? unresolved = null)
    {
        var output = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var columns in columnsList)
        {
            var n = Length(columns, "accession");
            var accessions = columns.TryGetValue("accession", out var a) && a.Count > 0 ? a : [];
            var names = Get(columns, "name", n, "");
            var genes = Get(columns, "gene_name", n, "");
            var organisms = Get(columns, "organism_name", n, "");
            var status = Get(columns, "decoy_contam_target", n, "");
            for (var i = 0; i < n; i++)
            {
                var rowContaminant = TargetDecoy(status[i]) == "contaminant";
                var letters = (IsFalsy(status[i]) ? "" : PyFormat.Str(status[i]!)).Split('|')
                    .Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToHashSet();
                var mixed = letters.Count > 1 && contaminantOf is not null;
                var rowAccessions = Accessions(accessions[i]);
                var geneParts = PerAccession(genes[i], rowAccessions.Count);
                var organismParts = PerAccession(organisms[i], rowAccessions.Count);
                for (var j = 0; j < rowAccessions.Count; j++)
                {
                    var acc = rowAccessions[j];
                    if (output.ContainsKey(acc)) continue;
                    var contaminant = rowContaminant;
                    if (mixed)
                    {
                        if (acc.ToUpperInvariant().StartsWith(DecoyPrefix, StringComparison.Ordinal))
                            contaminant = false;
                        else
                        {
                            var decided = contaminantOf!(acc);
                            if (decided is null)
                            {
                                if (unresolved is not null) unresolved[acc] = unresolved.GetValueOrDefault(acc) + 1;
                            }
                            else contaminant = decided.Value;
                        }
                    }
                    // MetaMorpheus writes `primary:TUBA1B, synonym:TUBA3`; the primary name is enough.
                    var gene = geneParts[j] ?? "";
                    var primary = gene.Split(',')[0].Replace("primary:", "").Trim();
                    var organismName = (organismParts[j] ?? "").Trim();
                    var sourceDb = SourceDb(acc, contaminant);
                    output[acc] = new Row
                    {
                        ["protein_accession"] = acc,
                        ["canonical_accession"] = Canonical(acc),
                        ["gene"] = primary.Length == 0 ? null : primary,
                        ["organism"] = EntryTaxon(sourceDb, organism),
                        // A reversed decoy is no organism's protein, so it carries no species name either (G44).
                        ["organism_name"] = sourceDb == "decoy" ? null : organismName.Length == 0 ? null : organismName,
                        ["length"] = null,
                        ["source_db"] = sourceDb,
                        ["uniprot_release"] = null,
                        ["is_contaminant"] = contaminant,
                    };
                }
            }
        }
        return output.Keys.Order(StringComparer.Ordinal).Select(k => output[k]).ToList();
    }

    /// <summary>Adds any accession that appears only in the protein-group table, so a group's
    /// <c>protein_accessions</c> never point at nothing.</summary>
    public static List<Row> AddGroupProteins(List<Row> proteins, IEnumerable<IReadOnlyDictionary<string, object?>> proteinGroups, string? organism)
    {
        var known = proteins.Select(r => (string)r["protein_accession"]!).ToHashSet(StringComparer.Ordinal);
        foreach (var group in proteinGroups)
        {
            var accessions = (group.GetValueOrDefault("protein_accessions") as IEnumerable<object?> ?? []).Select(x => (string)x!).ToList();
            // Same alignment hazard as ProteinRows: a group's gene list can be shorter than its accessions.
            var geneList = group.GetValueOrDefault("genes") as IEnumerable<object?> ?? [];
            var genes = PerAccession(string.Join("|", geneList.Select(g => IsFalsy(g) ? "" : PyFormat.Str(g!))), accessions.Count);
            var contaminant = Equals(group.GetValueOrDefault("target_decoy"), "contaminant");
            for (var j = 0; j < accessions.Count; j++)
            {
                var acc = accessions[j];
                if (!known.Add(acc)) continue;
                var sourceDb = SourceDb(acc, contaminant);
                proteins.Add(new Row
                {
                    ["protein_accession"] = acc,
                    ["canonical_accession"] = Canonical(acc),
                    ["gene"] = genes[j],
                    // The group table carries no per-accession species name: the taxon where it is safe, and a
                    // null organism_name, never a species guessed from the group's other members.
                    ["organism"] = EntryTaxon(sourceDb, organism),
                    ["organism_name"] = null,
                    ["length"] = null,
                    ["source_db"] = sourceDb,
                    ["uniprot_release"] = null,
                    ["is_contaminant"] = contaminant,
                });
            }
        }
        proteins.Sort((x, y) => string.CompareOrdinal((string)x["protein_accession"]!, (string)y["protein_accession"]!));
        return proteins;
    }

    /// <summary>Builds Peptidoform rows from <c>AllPeptides.psmtsv</c>, the producer's peptide-level FDR output.</summary>
    /// <param name="proteinGroups">The group ids that exist in this bundle. A peptidoform is linked to a group only
    /// when its accession set IS one of them: grouping is the producer's parsimony result, and a link invented
    /// here would dangle.</param>
    public static List<Row> PeptidoformRows(
        IReadOnlyDictionary<string, List<object?>> columns, string datasetId, ProformaCache proforma,
        IReadOnlyDictionary<string, long> psmCounts, IReadOnlySet<string>? proteinGroups = null)
    {
        var n = Length(columns, "full_sequence");
        var full = columns.TryGetValue("full_sequence", out var f) ? f : [];
        var accession = Or(Column(columns, "accession", "protein_accession"), n, "");
        var qs = Get(columns, "q_value", n);
        var qNotches = Get(columns, "q_value_notch", n);
        var peps = Get(columns, "pep", n);
        var statuses = Get(columns, "decoy_contam_target", n);
        var output = new Dictionary<string, Row>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var engine = First(full[i]);
            var parsed = proforma.Get(engine);
            if (string.IsNullOrEmpty(parsed.Proforma)) continue;
            var pid = $"{datasetId}:{parsed.Proforma}";
            var accessions = Accessions(accession[i]);
            var q = Float(qs[i]);
            var qNotch = Float(qNotches[i]);
            var pep = Float(peps[i]);
            var status = TargetDecoy(statuses[i]);
            if (!output.TryGetValue(pid, out var row))
            {
                output[pid] = new Row
                {
                    ["peptidoform_id"] = pid,
                    ["dataset_id"] = datasetId,
                    ["peptidoform"] = parsed.Proforma,
                    ["base_sequence"] = parsed.BaseSequence,
                    ["best_q_value"] = q,
                    ["best_q_value_notch"] = qNotch,
                    ["target_decoy"] = status,
                    ["best_pep"] = pep,
                    ["n_psms"] = psmCounts.GetValueOrDefault(parsed.Proforma),
                    ["protein_group_id"] = GroupId(datasetId, accessions, proteinGroups),
                    ["protein_accessions"] = accessions.Cast<object?>().ToList(),
                    ["is_unique"] = accessions.Count > 0 ? accessions.Count == 1 : null,
                    ["is_isoform_specific"] = null,
                    ["engine_full_sequences"] = new List<object?> { engine },
                };
                continue;
            }
            // Kept as a set, not a pick: two engine names that resolve to one UNIMOD accession make one
            // peptidoform id from two producer strings, and that must stay visible (ptmQtl 002, DATAREPO-P3).
            var engines = (List<object?>)row["engine_full_sequences"]!;
            if (!engines.Contains(engine))
                row["engine_full_sequences"] = engines.Append(engine).Cast<string>().Order(StringComparer.Ordinal).Cast<object?>().ToList();
            if (q is not null && (row["best_q_value"] is null || q < (double)row["best_q_value"]!)) row["best_q_value"] = q;
            if (qNotch is not null && (row["best_q_value_notch"] is null || qNotch < (double)row["best_q_value_notch"]!)) row["best_q_value_notch"] = qNotch;
            if (pep is not null && (row["best_pep"] is null || pep < (double)row["best_pep"]!)) row["best_pep"] = pep;
            if (Equals(row["target_decoy"], "decoy") || status == "target")
                row["target_decoy"] = status == "target" ? status : row["target_decoy"];
        }
        return output.Keys.Order(StringComparer.Ordinal).Select(k => output[k]).ToList();
    }

    /// <summary>Derives PtmSite rows from accepted, non-decoy PSMs that place a modification.</summary>
    /// <remarks>
    /// <para>A site is emitted when the match is not a decoy and is at or below <paramref name="qThreshold"/>, and
    /// the peptide's position in the protein is known from the searched database's sequence, never by pairing
    /// the producer's spans with its accessions (<see cref="Placements"/>, DATAREPO-32).</para>
    /// <para>No UNIMOD requirement (the key ends in the engine's own name), no ambiguity-level filter
    /// (<c>best_ambiguity_level</c> keeps it answerable), no target-only filter (contaminant sites are real
    /// measurements and kept), and no terminal-placement skip (2,091 terminal sites at q &lt;= 0.01 on the corpus,
    /// aging 028; 958 of the 1,220 protein N-termini sit at position 2 after initiator-Met excision).</para>
    /// </remarks>
    /// <param name="unplaced">Filled with <c>{reason: count}</c> of (PSM, accession) pairs that could not be placed.</param>
    public static List<Row> PtmSiteRows(
        IReadOnlyDictionary<string, List<object?>> columns, string datasetId, ProformaCache proforma,
        ProteinSequences? sequences = null, OrderedDictionary<string, long>? unplaced = null, double qThreshold = 0.01)
    {
        var n = Length(columns, "full_sequence");
        var full = columns.TryGetValue("full_sequence", out var f) ? f : [];
        var accession = Or(Column(columns, "accession", "protein_accession"), n, "");
        var ranges = Get(columns, "start_and_end_residues_in_parent_sequence", n, "");
        var levels = Get(columns, "ambiguity_level", n, "");
        var qs = Get(columns, "q_value", n);
        var status = Get(columns, "decoy_contam_target", n, "");
        // The residue before the peptide, which is how the producer says an initiator methionine was excised;
        // read only for a single-accession PSM with no sequence, otherwise the sequence itself says.
        var prevResidues = Or(Column(columns, "previous_residue", "previous_amino_acid"), n, "");
        sequences ??= new ProteinSequences();

        var sites = new Dictionary<string, Row>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var q = Float(qs[i]);
            if (q is null || q > qThreshold) continue;
            var state = TargetDecoy(status[i]);
            if (state == "decoy") continue;
            var levelText = (IsFalsy(levels[i]) ? "" : PyFormat.Str(levels[i]!)).Trim();
            var level = levelText.Length == 0 ? null : levelText;
            var parsed = proforma.Get(First(full[i]));
            if (parsed.Mods.Count == 0) continue;
            var placements = Placements(parsed.BaseSequence, Accessions(accession[i]), ranges[i], prevResidues[i], sequences, unplaced);
            foreach (var mod in parsed.Mods)
            {
                var name = SiteKeyName(mod.Name, PyStr(full[i]));
                var nTerminal = mod.Position == Proforma.NTerminus;
                var cTerminal = mod.Position == Proforma.CTerminus;
                // A terminus is a POSITION; the thing modified there is still a residue, so a terminal mod is keyed
                // on the residue it sits on (aging 045 section 2), never on a sentinel or on length + 1.
                string? residue;
                long offset;
                var bases = parsed.BaseSequence ?? "";
                if (nTerminal) (residue, offset) = (bases.Length > 0 ? bases[..1] : null, 1);
                else if (cTerminal) (residue, offset) = (bases.Length > 0 ? bases[^1..] : null, bases.Length);
                else (residue, offset) = (mod.Residue, mod.Position);
                foreach (var (acc, start, previous, length) in placements)
                {
                    var position = start + offset - 1;
                    string siteType;
                    if (cTerminal)
                    {
                        if (length is null)
                        {
                            // Protein or peptide C-terminus is a question only the sequence answers.
                            if (unplaced is not null) unplaced["c_term_no_sequence"] = unplaced.GetValueOrDefault("c_term_no_sequence") + 1;
                            continue;
                        }
                        siteType = position == length ? "protein_c_term" : "peptide_c_term";
                    }
                    else siteType = SiteType(nTerminal, start, previous);
                    var suffix = siteType == "residue" ? "" : $"@{siteType}";
                    var key = $"{datasetId}:{acc}:{residue ?? "None"}{position}:{name}{suffix}";
                    if (!sites.TryGetValue(key, out var row))
                    {
                        sites[key] = new Row
                        {
                            ["ptm_site_id"] = key,
                            ["dataset_id"] = datasetId,
                            ["protein_accession"] = acc,
                            ["position"] = position,
                            ["residue"] = residue,
                            ["site_type"] = siteType,
                            ["modification"] = mod.Unimod,
                            ["modification_name"] = name,
                            ["target_decoy"] = state,
                            ["best_ambiguity_level"] = level,
                            ["localization_score"] = null,
                            ["n_psms"] = 1L,
                            ["best_q_value"] = q,
                        };
                    }
                    else
                    {
                        row["n_psms"] = (long)row["n_psms"]! + 1;
                        if (q < (double)row["best_q_value"]!) row["best_q_value"] = q;
                        // A site seen by both a target and a contaminant PSM is a target site: the contaminant
                        // database also contains real proteins.
                        if (state == "target") row["target_decoy"] = "target";
                        row["best_ambiguity_level"] = BetterLevel((string?)row["best_ambiguity_level"], level);
                    }
                }
            }
        }
        return sites.Keys.Order(StringComparer.Ordinal).Select(k => sites[k]).ToList();
    }

    /// <summary>Checks every site against the searched sequence: is its residue really at its position?</summary>
    /// <remarks>The check aging ran from outside to find DATAREPO-32, run inside every ingest, because the one path
    /// that still trusts the producer's spans is exactly where a wrong position could come back.</remarks>
    public static OrderedDictionary<string, long> VerifySiteResidues(IEnumerable<IReadOnlyDictionary<string, object?>> sites, ProteinSequences sequences)
    {
        var counts = new OrderedDictionary<string, long> { ["residue_matches"] = 0, ["wrong_residue"] = 0, ["beyond_length"] = 0, ["no_sequence"] = 0 };
        foreach (var site in sites)
        {
            var candidates = sequences.Get((string)site["protein_accession"]!);
            var position = (long)site["position"]!;
            var residue = site["residue"] as string;
            if (candidates.Count == 0) counts["no_sequence"]++;
            else if (candidates.All(s => position < 1 || position > s.Length)) counts["beyond_length"]++;
            else if (residue is null || candidates.Any(s => 1 <= position && position <= s.Length && s[(int)position - 1].ToString() == residue))
                counts["residue_matches"]++;
            else counts["wrong_residue"]++;
        }
        return counts;
    }

    /// <summary><c>(accession, 1-based peptide start, residue before it, protein length)</c> for every place a PSM's
    /// peptide sits; the length is null when no sequence was available.</summary>
    /// <remarks><b>The spans cell is never paired with the accession cell.</b> MetaMorpheus de-duplicates it and lists a
    /// repeated peptide once per occurrence, so an index pairing is wrong in both directions, including when the
    /// counts agree. Positions come from the searched database instead; without a sequence the spans are used
    /// only for a single-accession PSM, and anything else is counted in <paramref name="unplaced"/>.</remarks>
    private static List<(string Acc, long Start, string Previous, long? Length)> Placements(
        string? baseSequence, List<string> accessions, object? ranges, object? previousCell,
        ProteinSequences sequences, OrderedDictionary<string, long>? unplaced)
    {
        var output = new List<(string, long, string, long?)>();
        var missed = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var acc in accessions)
        {
            var candidates = sequences.Get(acc);
            if (candidates.Count == 0)
            {
                missed[acc] = "no_sequence";
                continue;
            }
            var found = false;
            foreach (var sequence in candidates)
            {
                foreach (var start in ProteinDb.Occurrences(baseSequence ?? "", sequence))
                {
                    var previous = start >= 2 ? sequence[(int)start - 2].ToString() : "";
                    var placement = (acc, (long)start, previous, (long?)sequence.Length);
                    if (!output.Contains(placement)) output.Add(placement);
                    found = true;
                }
            }
            if (!found) missed[acc] = "peptide_not_in_sequence";
        }
        if (missed.Count == 0) return output;
        if (accessions.Count == 1)
        {
            var starts = ResidueStarts(ranges);
            var previous = starts.Count > 0 ? PerAccession(previousCell, starts.Count) : [];
            return starts.Select((s, k) => (accessions[0], s, previous[k] ?? "", (long?)null)).ToList();
        }
        if (unplaced is not null)
            foreach (var reason in missed.Values)
                unplaced[reason] = unplaced.GetValueOrDefault(reason) + 1;
        return output;
    }

    /// <summary>Which SiteType an N-terminal placement is. The initiator-methionine case is why this is not
    /// <c>start == 1</c>: co-translational N-terminal acetylation follows Met excision, so the modified residue is
    /// residue 2 and the previous residue is the excised <c>M</c>.</summary>
    private static string SiteType(bool terminal, long start, string previousResidue)
    {
        if (!terminal) return "residue";
        if (start == 1) return "protein_n_term";
        if (start == 2 && previousResidue.Trim().ToUpperInvariant() == "M") return "protein_n_term";
        return "peptide_n_term";
    }

    /// <summary>The modification name, refused if it holds the one character that would corrupt a site key.</summary>
    /// <exception cref="IngestException">The name contains <c>:</c>, the <c>ptm_site_id</c> separator: the mod token did
    /// not parse, so what we hold is not a modification name at all.</exception>
    private static string SiteKeyName(string name, string fullSequence)
    {
        if (name.Contains(':'))
            throw new IngestException(
                $"modification name {PyRepr(name)} (from full sequence {PyRepr(fullSequence)}) contains ':', "
                + "which is the ptm_site_id separator. The mod token did not parse, so the name is not "
                + "trustworthy as a site key; fix the token's parsing rather than storing this row.");
        return name;
    }

    private static string PyRepr(string s) =>
        s.Contains('\'') && !s.Contains('"') ? "\"" + s.Replace("\\", "\\\\") + "\"" : "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>The protein group this peptide's accession set names, if the producer built that group.</summary>
    private static string? GroupId(string datasetId, List<string> accessions, IReadOnlySet<string>? groups)
    {
        if (accessions.Count == 0) return null;
        var candidate = $"{datasetId}:{string.Join(";", accessions.Order(StringComparer.Ordinal))}";
        if (groups is null) return candidate;
        return groups.Contains(candidate) ? candidate : null;
    }

    /// <summary>How many PSMs support each peptidoform, for <c>Peptidoform.n_psms</c>.</summary>
    public static Dictionary<string, long> PsmCountsByPeptidoform(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row.GetValueOrDefault("peptidoform") is string key && key.Length > 0)
                counts[key] = counts.GetValueOrDefault(key) + 1;
        return counts;
    }

    /// <summary>Did the notch fail to resolve to a single value? Null when the producer reported no notch at all,
    /// which is not the same as a notch that resolved.</summary>
    public static bool? NotchAmbiguous(object? raw)
    {
        if (raw is null) return null;
        var text = PyFormat.Str(raw).Trim();
        return text.Length == 0 ? null : text.Contains(NotchSeparator);
    }

    /// <summary>Counts target matches the way the producing search engine counts them: target,
    /// <c>q_value &lt;= threshold</c> and <c>q_value_notch &lt;= threshold</c>, plus, for PSMs only, a notch that
    /// resolved.</summary>
    /// <remarks><b>The notch clause is a PSM rule and must not be carried to peptidoforms</b>: on PXD032202 the
    /// producer's peptide count agrees with the un-clause number exactly (DATAREPO-27, thread 033).</remarks>
    public static long ProducerCounts(
        IReadOnlyDictionary<string, List<object?>> columns, double threshold = ProducerThreshold, bool requireResolvedNotch = true)
    {
        var n = Length(columns, "q_value");
        var qs = columns.TryGetValue("q_value", out var c) ? c : [];
        var notches = Get(columns, "q_value_notch", n);
        var rawNotches = Get(columns, "notch", n);
        var status = Get(columns, "decoy_contam_target", n, "");
        long total = 0;
        for (var i = 0; i < n; i++)
        {
            if ((IsFalsy(status[i]) ? "" : PyFormat.Str(status[i]!)).Trim() != "T") continue;
            var q = Float(qs[i]);
            var notch = Float(notches[i]);
            if (q is null || q > threshold) continue;
            if (notch is not null && notch > threshold) continue;
            if (requireResolvedNotch && NotchAmbiguous(rawNotches[i]) == true) continue;
            total++;
        }
        return total;
    }
}
