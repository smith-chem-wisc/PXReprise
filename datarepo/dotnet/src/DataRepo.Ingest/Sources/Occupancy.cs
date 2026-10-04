using System.Globalization;
using DataRepo.Bundle;

namespace DataRepo.Ingest.Sources;

/// <summary>What one occupancy read produced: the rows, and everything not stored, by reason.</summary>
public sealed class OccupancyResult
{
    public List<Row> Rows { get; set; } = [];
    public long Entries { get; set; }
    /// <summary><c>{reason: entries}</c> for every entry not stored, in first-seen order.</summary>
    public OrderedDictionary<string, long> NotStored { get; } = new(StringComparer.Ordinal);
    public long TruncatedCells { get; set; }
    public List<string> FailedFields { get; set; } = [];
    /// <summary>Cells whose segments did not line up with their accessions and were resolved by sequence.</summary>
    public long RealignedCells { get; set; }
    /// <summary>Up to <see cref="Occupancy.MaxDuplicateExamples"/> dropped duplicates whose values differ from the stored row.</summary>
    public List<Row> DifferingDuplicates { get; } = [];

    internal void Count(string reason, long n = 1) => NotStored[reason] = NotStored.GetValueOrDefault(reason) + n;
}

/// <summary>MetaMorpheus's PTM site occupancy to <c>ptm_stoichiometry</c> rows (D29).</summary>
/// <remarks>
/// <para>MetaMorpheus writes, per protein group and per sample group, a <c>CountOccupancy_&lt;label&gt;</c> and
/// an <c>IntensityOccupancy_&lt;label&gt;</c> cell. mzLib parses them; this turns the entries into rows and
/// computes nothing about occupancy. The definitions are QuantProject's (<c>DEF-OCC-*</c>):</para>
/// <list type="bullet">
/// <item><b>DEF-OCC-ACCESSION.</b> A cell's <c>|</c> segments skip every protein with no entry, with no
/// placeholder, so segment i is NOT accession i whenever a member has no modified site: the fourth <c>|</c>-list
/// in MetaMorpheus's output that cannot be zipped by position. Segments are assigned to accessions by the one
/// thing that can tell, each entry's residue against its modification's motif in that accession's searched
/// sequence, keeping the accessions' order. A non-unique assignment is not guessed; it is counted.</item>
/// <item><b>DEF-OCC-KEY.</b> <c>pos{p}</c> is 1-based; 0 is the protein N-terminus and Length + 1 the
/// C-terminus, which <c>ptm_sites</c> keys on the residue they sit on (position 1 <c>@protein_n_term</c>,
/// position Length <c>@protein_c_term</c>).</item>
/// <item><b>DEF-OCC-COUNTONLY.</b> Count and intensity entries join per (site, run) into one row and its state:
/// quantified, floor, or count-only. Absent is no row.</item>
/// </list>
/// <para>Everything not stored is counted by reason and becomes a finding. Ported from <c>sources/occupancy.py</c>.</para>
/// </remarks>
public static class Occupancy
{
    /// <summary>Past this many candidate assignments of segments to accessions, a cell is reported rather than searched.</summary>
    public const int MaxAssignments = 20_000;

    public const int MaxDuplicateExamples = 10;

    /// <summary>mzLib's failed fields or truncation mean part of a cell never reached us.</summary>
    private const string Truncated = "cell_truncated";

    private static object? E(IReadOnlyDictionary<string, object?> entry, string key) => entry.TryGetValue(key, out var v) ? v : null;
    private static string S(object? v) => v is null ? "None" : PyFormat.Str(v);
    private static long Int(object? v) => v switch
    {
        long l => l,
        int i => i,
        double d => (long)Math.Truncate(d),
        string s => long.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };
    private static double Float(object? v) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        string s when PyFormat.TryParseFloat(s, out var d) => d,
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };
    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        long l => l != 0,
        double d => d != 0,
        _ => true,
    };

    /// <summary>Python's <c>==</c> between two numbers of any of the types an entry carries.</summary>
    private static bool NumEq(object? a, object? b) =>
        (a is null || b is null) ? a is null && b is null : Float(a) == Float(b);

    /// <summary>Counts a second entry for a (site, run) that already has a row, by whether it could matter.</summary>
    /// <remarks>The first entry in file order is kept, which is safe only when the dropped one says the same
    /// thing (aging 069, DATAREPO-53), so the two cases are counted apart, and so is whether the second came
    /// from the same protein group (MetaMorpheus writing one group twice, aging's S34) or another group.</remarks>
    private static void Duplicate(OccupancyResult output, Row row, string groupId, string basis, bool same)
    {
        var where = groupId == (string)row["protein_group_id"]! ? "same protein group" : "another protein group";
        var what = same ? "identical values" : "DIFFERENT values, first in file kept";
        output.Count($"duplicate entry for one site and run ({what}; {where})");
        if (!same && output.DifferingDuplicates.Count < MaxDuplicateExamples)
            output.DifferingDuplicates.Add(new Row
            {
                ["ptm_site_id"] = row["ptm_site_id"],
                ["assay_id"] = row["assay_id"],
                ["basis"] = basis,
                ["kept_group"] = row["protein_group_id"],
                ["dropped_group"] = groupId,
            });
    }

    /// <summary>The residue letter a MetaMorpheus <c>IdWithMotif</c> names (<c>Phosphorylation on S</c> to <c>S</c>).</summary>
    private static string? Motif(string modification)
    {
        var at = modification.LastIndexOf(" on ", StringComparison.Ordinal);
        if (at < 0) return null;
        var tail = modification[(at + 4)..].Trim();
        return tail.Length == 1 ? tail : null;
    }

    /// <summary>Can this entry belong to a protein with this sequence?</summary>
    private static bool Fits(IReadOnlyDictionary<string, object?> entry, string? sequence)
    {
        if (string.IsNullOrEmpty(sequence)) return false;
        var p = Int(E(entry, "position"));
        if (p == 0 || p == sequence.Length + 1) return true;  // a terminus slot: any protein has one
        if (p < 1 || p > sequence.Length) return false;
        var motif = Motif(S(E(entry, "modification")));
        return motif is null or "X" || sequence[(int)p - 1].ToString() == motif;
    }

    /// <summary>The accession for each segment index, or null when it cannot be told uniquely.</summary>
    /// <remarks>Segments are a subsequence of the accessions in order (DEF-OCC-ACCESSION), so a candidate is an
    /// increasing choice of accessions; it holds when every entry fits its accession's sequence.</remarks>
    private static List<string>? Assign(
        SortedDictionary<long, List<IReadOnlyDictionary<string, object?>>> segments, List<string> accessions, ProteinSequences sequences)
    {
        var k = segments.Count;
        var order = segments.Keys.ToList();

        string? Seq(string acc)
        {
            var found = sequences.Get(acc);
            return found.Count > 0 ? found[0] : null;
        }

        bool Holds(IReadOnlyList<int> choice)
        {
            for (var i = 0; i < order.Count; i++)
                foreach (var e in segments[order[i]])
                    if (!Fits(e, Seq(accessions[choice[i]]))) return false;
            return true;
        }

        if (k == accessions.Count)
        {
            var choice = Enumerable.Range(0, k).ToList();
            return Holds(choice) ? choice.Select(a => accessions[a]).ToList() : null;
        }
        if (k > accessions.Count) return null;
        var valid = new List<int[]>();
        var n = 0;
        foreach (var choice in Combinations(accessions.Count, k))
        {
            if (n++ >= MaxAssignments) return null;
            if (Holds(choice))
            {
                valid.Add(choice);
                if (valid.Count > 1) return null;
            }
        }
        return valid.Count == 1 ? valid[0].Select(a => accessions[a]).ToList() : null;
    }

    /// <summary><c>itertools.combinations(range(n), k)</c>, in its lexicographic order.</summary>
    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var indices = Enumerable.Range(0, k).ToArray();
        if (k > n) yield break;
        yield return (int[])indices.Clone();
        while (true)
        {
            var i = k - 1;
            while (i >= 0 && indices[i] == i + n - k) i--;
            if (i < 0) yield break;
            indices[i]++;
            for (var j = i + 1; j < k; j++) indices[j] = indices[j - 1] + 1;
            yield return (int[])indices.Clone();
        }
    }

    /// <summary>The <c>ptm_sites</c> key this entry describes (DEF-OCC-KEY), or null without a sequence.</summary>
    private static string? SiteKey(string datasetId, string accession, string? sequence, IReadOnlyDictionary<string, object?> entry)
    {
        if (string.IsNullOrEmpty(sequence)) return null;
        var p = Int(E(entry, "position"));
        var name = S(E(entry, "modification"));
        long position;
        string suffix;
        if (p == 0 || Truthy(E(entry, "is_n_terminus"))) (position, suffix) = (1, "@protein_n_term");
        else if (p == sequence.Length + 1) (position, suffix) = (sequence.Length, "@protein_c_term");
        else (position, suffix) = (p, "");
        if (position < 1 || position > sequence.Length) return null;
        return $"{datasetId}:{accession}:{sequence[(int)position - 1]}{position}:{name}{suffix}";
    }

    /// <summary><c>ptm_stoichiometry</c> rows for one label-free, design-less search.</summary>
    /// <param name="path">The search's <c>AllQuantifiedProteinGroups.tsv</c> (or <c>AllProteinGroups.tsv</c>).</param>
    /// <param name="runNames">Deposited run names, to map each <c>&lt;label&gt;</c> to a run.</param>
    /// <param name="sequences">The searched databases, to assign segments and read residues.</param>
    /// <param name="siteIds">The bundle's <c>ptm_sites</c> keys; an entry keyed elsewhere is not stored.</param>
    /// <param name="groupIds">The bundle's protein group ids.</param>
    /// <param name="assayIds">The bundle's assay ids; a run with no label-free assay stores nothing.</param>
    public static OccupancyResult Rows(
        string path, string datasetId, RunNameMap runNames, ProteinSequences sequences,
        IReadOnlySet<string> siteIds, IReadOnlySet<string> groupIds, IReadOnlySet<string> assayIds, ReaderLog? log = null)
    {
        var read = Readers.ReadOccupancy(path, log);
        // A private map: a label that is not a run must not be reported as an unmatched USI run name.
        var labels = new RunNameMap(runNames.Deposited);
        var output = new OccupancyResult
        {
            Entries = read.Records.Count,
            TruncatedCells = read.TruncatedCellCount,
            FailedFields = read.FailedFields.ToList(),
        };

        var cells = new Dictionary<(string Group, string Label, string Basis), SortedDictionary<long, List<IReadOnlyDictionary<string, object?>>>>();
        foreach (var entry in read.Records)
        {
            if (Truthy(E(entry, "cell_is_truncated")))
            {
                output.Count(Truncated);
                continue;
            }
            var key = (S(E(entry, "protein_group_name")), S(E(entry, "sample_label")), S(E(entry, "basis")));
            if (!cells.TryGetValue(key, out var segments)) cells[key] = segments = [];
            var index = Int(E(entry, "entity_index"));
            if (!segments.TryGetValue(index, out var list)) segments[index] = list = [];
            list.Add(entry);
        }

        var merged = new Dictionary<(string Site, string Assay), Row>();
        var unassigned = new HashSet<(string, string, string)>();
        var cellKeys = cells.Keys
            .OrderBy(k => k.Group, StringComparer.Ordinal).ThenBy(k => k.Label, StringComparer.Ordinal).ThenBy(k => k.Basis, StringComparer.Ordinal);
        foreach (var (groupName, label, basis) in cellKeys)
        {
            var segments = cells[(groupName, label, basis)];
            var nEntries = segments.Values.Sum(v => (long)v.Count);
            var accessions = groupName.Split('|').Where(a => a.Length > 0).ToList();
            if (accessions.All(a => a.StartsWith("DECOY_", StringComparison.Ordinal)))
            {
                output.Count("decoy group", nEntries);
                continue;
            }
            var groupId = $"{datasetId}:{string.Join(";", accessions.Order(StringComparer.Ordinal))}";
            if (!groupIds.Contains(groupId))
            {
                output.Count("protein group not in the bundle", nEntries);
                continue;
            }
            var run = labels.Resolve(label);
            if (run is null)
            {
                output.Count("label is not a deposited run (a design's sample group?)", nEntries);
                continue;
            }
            var assigned = Assign(segments, accessions, sequences);
            if (assigned is null)
            {
                output.Count("segment's accession not determinable from the sequences", nEntries);
                unassigned.Add((groupId, label, basis));
                continue;
            }
            if (segments.Count != accessions.Count) output.RealignedCells++;
            var assayId = $"{datasetId}:{run}:label_free";
            if (!assayIds.Contains(assayId))
            {
                output.Count("run has no label-free assay", nEntries);
                continue;
            }
            foreach (var (index, accession) in segments.Keys.Zip(assigned))
            {
                var found = sequences.Get(accession);
                var sequence = found.Count > 0 ? found[0] : null;
                foreach (var entry in segments[index])
                {
                    var site = SiteKey(datasetId, accession, sequence, entry);
                    if (site is null || !siteIds.Contains(site))
                    {
                        output.Count("site not in ptm_sites");
                        continue;
                    }
                    if (!merged.TryGetValue((site, assayId), out var row))
                    {
                        merged[(site, assayId)] = row = new Row
                        {
                            ["ptm_site_id"] = site,
                            ["assay_id"] = assayId,
                            ["denominator_grouping"] = "run",
                            ["sample_label"] = label,
                            ["protein_group_id"] = groupId,
                            ["mm_position"] = Int(E(entry, "position")),
                            ["mm_modification"] = S(E(entry, "modification")),
                            ["definition_id"] = Definitions.Occupancy.DefinitionId,
                        };
                    }
                    if (basis == "count")
                    {
                        if (row.GetValueOrDefault("n_covering_psms") is not null)
                        {
                            var same = NumEq(row["modified_fraction_count"], E(entry, "fraction"))
                                && Int(row["n_modified_psms"]) == Int(E(entry, "numerator"))
                                && Int(row["n_covering_psms"]) == Int(E(entry, "denominator"));
                            Duplicate(output, row, groupId, basis, same);
                            continue;
                        }
                        row["modified_fraction_count"] = E(entry, "fraction");
                        row["n_modified_psms"] = Int(E(entry, "numerator"));
                        row["n_covering_psms"] = Int(E(entry, "denominator"));
                        row["count_is_ceiling"] = Int(E(entry, "numerator")) == Int(E(entry, "denominator"));
                    }
                    else if (basis == "intensity")
                    {
                        if (row.GetValueOrDefault("intensity_total") is not null)
                        {
                            var same = NumEq(row["modified_fraction_intensity"], E(entry, "fraction"))
                                && Float(row["intensity_modified"]) == Float(E(entry, "numerator"))
                                && Float(row["intensity_total"]) == Float(E(entry, "denominator"));
                            Duplicate(output, row, groupId, basis, same);
                            continue;
                        }
                        row["modified_fraction_intensity"] = E(entry, "fraction");
                        row["intensity_modified"] = Float(E(entry, "numerator"));
                        row["intensity_total"] = Float(E(entry, "denominator"));
                        row["intensity_is_floor"] = Float(E(entry, "fraction")) == 0.0 && Float(E(entry, "numerator")) == 0.0;
                        row["intensity_is_ceiling"] = Float(E(entry, "fraction")) == 1.0;
                    }
                    else
                    {
                        output.Count($"unknown basis {basis}");
                    }
                }
            }
        }

        foreach (var row in merged.Values)
        {
            if (row.GetValueOrDefault("intensity_total") is null)
            {
                // An intensity cell that exists but could not be assigned to an accession is NOT "nothing was
                // quantified": calling it count_only would state a falsehood.
                var key = ((string)row["protein_group_id"]!, (string)row["sample_label"]!, "intensity");
                row["occupancy_state"] = unassigned.Contains(key) ? "intensity_unassigned" : "count_only";
            }
            else if (row["intensity_is_floor"] is true)
            {
                row["occupancy_state"] = "floor";
            }
            else
            {
                row["occupancy_state"] = "quantified";
            }
        }
        output.Rows = merged.Keys
            .OrderBy(k => k.Site, StringComparer.Ordinal).ThenBy(k => k.Assay, StringComparer.Ordinal)
            .Select(k => merged[k]).ToList();
        return output;
    }
}
