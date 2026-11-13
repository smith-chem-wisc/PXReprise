using System.Globalization;
using System.Text;
using DataRepo.Bundle;
using DataRepo.Catalog;
using Quantification.PtmQtl;

namespace DataRepo.Runner;

/// <summary>The <c>ptmqtl.*</c> engines (ptmQtl 025; our 026, 031): site pairs per search, pooled pairs per species,
/// and site-trait fits per search and trait.</summary>
/// <remarks>
/// <para>The method is ptmQtl's (<c>ptmQtl:DEF-PTM-PAIR v2</c>, <c>ptmQtl:DEF-SITE-TRAIT v1</c>), applied by
/// <see cref="PtmQtlCore"/> through mzLib only. The inputs are read from the bundle by <see cref="PtmQtlBundle"/>.
/// This class decides what to run on, keys each artefact on its inputs, and writes it.</para>
/// <para><b>Inputs by role.</b></para>
/// <list type="bullet">
/// <item><c>enrichment</c>: the operator's list of PTM-enriched deposits (S3), a TSV with columns <c>dataset</c> and
/// <c>enriched_ptm</c>. It is hashed into every artefact (our ptmQtl 026, P23), so a changed list gives new artefacts.</item>
/// <item><c>traits</c> (<c>site_traits</c> only): a TSV with columns <c>dataset</c>, <c>run</c>, <c>replicate</c>,
/// <c>trait_id</c> and <c>value</c>, one trait per file.</item>
/// <item>The bundle, by its id: a bundle id is a hash of everything it holds.</item>
/// </list>
/// <para><b>Artefacts.</b> <c>site_pairs</c> writes <c>ptm_pairs</c> and <see cref="PairSitesFile"/>, each pair's two
/// sites in full, from which <c>pool_pairs</c> rebuilds mzLib's pairs without re-reading a bundle (P22).
/// <c>site_traits</c> writes <c>trait_effects</c> and <see cref="FitsFile"/>, the fields <c>trait_effects</c> has no
/// column for yet (our ptmQtl 031, P24).</para>
/// <para><b>S6, the search-version gate.</b> Each artefact records whether its search predates mzLib 1.0.592: such a
/// search wrote no occupancy for protein N-termini after initiator-Met removal (mzLib #1337). Nothing is filled in.</para>
/// </remarks>
public static class PtmQtlEngine
{
    public const string SitePairsEngine = DataRepo.Catalog.Runner.PtmQtlSitePairs;
    public const string PoolPairsEngine = DataRepo.Catalog.Runner.PtmQtlPoolPairs;
    public const string SiteTraitsEngine = DataRepo.Catalog.Runner.PtmQtlSiteTraits;

    public const string PairSitesFile = "pair_sites.tsv";
    public const string FitsFile = "site_trait_fits.tsv";
    public const string ExcludedFile = "excluded_sites.tsv";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The mzLib each MetaMorpheus release was built on, read from its release tag's
    /// <c>EngineLayer.csproj</c> (2026-10-06). A version not listed is recorded as unknown, never assumed.</summary>
    public static readonly IReadOnlyDictionary<string, string> MetaMorpheusMzLib = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["1.1.10"] = "1.0.589",
        ["1.1.11"] = "1.0.591",
    };

    /// <summary>The first mzLib that writes protein N-terminal occupancy after initiator-Met removal (#1337).</summary>
    public static readonly Version NTermFixedIn = new(1, 0, 592);

    public static Dictionary<string, string> Release() => new(StringComparer.Ordinal)
    {
        ["mzlib"] = typeof(PtmPairEngine).Assembly.GetName().Version!.ToString(3),
    };

    /// <summary>S6 for one bundle: true when its search lacks protein N-termini after Met removal, null when unknown.</summary>
    public static (string? Engine, string? Version, bool? LacksMetRemovedNTerm) SearchGate(BundleRef bundle)
    {
        var path = bundle.TablePath("datasets");
        if (path is null) return (null, null, null);
        var row = ArrowTables.ReadParquet(path).Rows.FirstOrDefault();
        var engine = row?.GetValueOrDefault("search_engine") as string;
        var version = row?.GetValueOrDefault("search_engine_version") as string;
        if (engine != "MetaMorpheus" || version is null || !MetaMorpheusMzLib.TryGetValue(version, out var mzlib)) return (engine, version, null);
        return (engine, version, Version.Parse(mzlib) < NTermFixedIn);
    }

    private static List<Dictionary<string, string>> ReadTsv(string path, IReadOnlyList<string> columns, string role)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) throw new RunnerException($"--input {role}={path} is empty");
        var names = lines[0].Split('\t');
        var missing = columns.Where(c => !names.Contains(c)).ToList();
        if (missing.Count > 0) throw new RunnerException($"--input {role}={path} lacks column(s) {string.Join(", ", missing)}");
        return lines.Skip(1).Select((l, i) =>
        {
            var cells = l.Split('\t');
            if (cells.Length != names.Length) throw new RunnerException($"--input {role}={path}: line {i + 2} has {cells.Length} cells, not {names.Length}");
            return names.Select((n, j) => (n, cells[j])).ToDictionary(t => t.n, t => t.Item2, StringComparer.Ordinal);
        }).ToList();
    }

    /// <summary>The datasets the operator's enrichment list names (S3).</summary>
    public static HashSet<string> Enriched(string path) =>
        ReadTsv(path, ["dataset"], "enrichment").Select(r => r["dataset"]).ToHashSet(StringComparer.Ordinal);

    /// <summary>The capture classes S3 and S3b know (ptmQtl's capture list v4).</summary>
    public static readonly IReadOnlySet<string> CaptureClasses = new HashSet<string>(StringComparer.Ordinal) { "ptm_capture", "capture_runs", "held", "no_ptm_capture" };

    /// <summary>The cysteine classes S2d knows.</summary>
    public static readonly IReadOnlySet<string> CysteineClasses = new HashSet<string>(StringComparer.Ordinal) { "nem", "cysteine_poor" };

    /// <summary>The operator's lists that every ptmQtl artefact is run under, by input role: hashed into each artefact,
    /// and one set of them per catalog and per pooled result.</summary>
    public static readonly IReadOnlyList<string> ListRoles = ["enrichment", "capture_runs", "cysteine"];

    /// <summary>Each deposit as the operator's lists describe it (S2d, S3, S3b).</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>enrichment</c>: ptmQtl's capture list, its <c>deposits.tsv</c> (columns <c>dataset</c>, <c>class</c>:
    /// <c>ptm_capture</c>, <c>capture_runs</c>, <c>held</c>, <c>no_ptm_capture</c>). A list without <c>class</c> (v1-v3,
    /// <c>dataset</c>, <c>enriched_ptm</c>) names PTM-capture deposits.</item>
    /// <item><c>capture_runs</c>: its <c>capture_runs.tsv</c> (<c>dataset</c>, <c>run_id</c>), needed when a deposit is
    /// <c>capture_runs</c>.</item>
    /// <item><c>cysteine</c>: S2d's deposits (<c>dataset</c>, <c>class</c>: <c>nem</c> or <c>cysteine_poor</c>, and an
    /// optional <c>reason</c>, e.g. the reagent). The definitions name them; a list keeps them out of the code, and a
    /// longer list gives new artefacts.</item>
    /// </list>
    /// </remarks>
    public static Dictionary<string, PtmQtlDeposit> Deposits(IReadOnlyDictionary<string, string> inputs)
    {
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in ReadTsv(inputs["enrichment"], ["dataset"], "enrichment"))
        {
            var cls = r.GetValueOrDefault("class") ?? "ptm_capture";
            if (!CaptureClasses.Contains(cls)) throw new RunnerException($"--input enrichment: {r["dataset"]} has class '{cls}', not one of {string.Join(", ", CaptureClasses.Order(StringComparer.Ordinal))}");
            if (!classes.TryAdd(r["dataset"], cls)) throw new RunnerException($"--input enrichment names {r["dataset"]} twice");
        }
        var captureRuns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (inputs.TryGetValue("capture_runs", out var runsPath))
            foreach (var r in ReadTsv(runsPath, ["dataset", "run_id"], "capture_runs"))
            {
                if (classes.GetValueOrDefault(r["dataset"]) != "capture_runs")
                    throw new RunnerException($"--input capture_runs names run {r["run_id"]} of {r["dataset"]}, which --input enrichment does not class capture_runs");
                if (!captureRuns.TryGetValue(r["dataset"], out var set)) captureRuns[r["dataset"]] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(r["run_id"]);
            }
        var unnamed = classes.Where(kv => kv.Value == "capture_runs" && !captureRuns.ContainsKey(kv.Key)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        if (unnamed.Count > 0) throw new RunnerException($"--input enrichment classes {string.Join(", ", unnamed)} capture_runs, and --input capture_runs names none of their runs");
        var cysteine = new Dictionary<string, (string Class, string? Reason)>(StringComparer.Ordinal);
        foreach (var r in ReadTsv(inputs["cysteine"], ["dataset", "class"], "cysteine"))
        {
            if (!CysteineClasses.Contains(r["class"])) throw new RunnerException($"--input cysteine: {r["dataset"]} has class '{r["class"]}', not nem or cysteine_poor");
            if (!cysteine.TryAdd(r["dataset"], (r["class"], r.GetValueOrDefault("reason") is { Length: > 0 } reason ? reason : null)))
                throw new RunnerException($"--input cysteine names {r["dataset"]} twice");
        }
        return classes.Keys.Concat(cysteine.Keys).Distinct(StringComparer.Ordinal).ToDictionary(d => d, d => new PtmQtlDeposit(
            classes.GetValueOrDefault(d), captureRuns.GetValueOrDefault(d),
            cysteine.TryGetValue(d, out var c) ? c.Class : null, cysteine.TryGetValue(d, out var c2) ? c2.Reason : null), StringComparer.Ordinal);
    }

    /// <summary>What the lists say of a bundle's dataset: by its id, else by its deposit's accession (a split search,
    /// G95, is listed under its deposit).</summary>
    public static PtmQtlDeposit DepositOf(IReadOnlyDictionary<string, PtmQtlDeposit> deposits, string datasetId) =>
        deposits.GetValueOrDefault(datasetId) ?? deposits.GetValueOrDefault(DataRepo.Ingest.Usi.DepositAccession(datasetId)) ?? new PtmQtlDeposit();

    private static readonly string[] ExcludedColumns = ["dataset", "protein", "position", "residue", "modification", "rule", "reason"];

    /// <summary>The sites S2c and S2d left out, as ptmQtl's <c>excluded_sites.tsv</c> (a shown, not hidden, list: S2d).
    /// An empty <c>modification</c> is every modification at that place.</summary>
    public static string WriteExcluded(string datasetId, IEnumerable<ExcludedSite> sites, string path)
    {
        var text = new StringBuilder(string.Join('\t', ExcludedColumns)).Append('\n');
        foreach (var s in sites)
        {
            if ((s.Modification ?? "").Contains('\t') || s.Reason.Contains('\t')) throw new RunnerException($"an excluded site's text holds a tab: {s}");
            text.Append(string.Join('\t', datasetId, s.Protein, s.Position.ToString(Inv), s.Residue?.ToString() ?? "", s.Modification ?? "", s.Rule, s.Reason)).Append('\n');
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>The sha256 of each list input given, by role, as every ptmQtl artefact hashes them.</summary>
    private static Dictionary<string, string> ListShas(IReadOnlyDictionary<string, string> inputs) =>
        ListRoles.Where(inputs.ContainsKey).ToDictionary(r => r, r => BundleWriter.Sha256File(inputs[r]), StringComparer.Ordinal);

    private static void RequireInputs(string engine, IReadOnlyDictionary<string, string> inputs, IReadOnlyList<string> roles, IReadOnlyList<string>? optional = null)
    {
        optional ??= [];
        var missing = roles.Where(r => !inputs.ContainsKey(r)).ToList();
        if (missing.Count > 0) throw new RunnerException($"{engine} needs --input {string.Join("=<path>, --input ", missing)}=<path>.");
        var unknown = inputs.Keys.Where(k => !roles.Contains(k) && !optional.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0) throw new RunnerException($"{engine} takes inputs {string.Join(", ", roles.Concat(optional))}; not {string.Join(", ", unknown)}");
        foreach (var (role, path) in inputs)
            if (!File.Exists(path)) throw new RunnerException($"--input {role}={path}: no such file");
    }

    private static string R(double v) => double.IsNaN(v) ? "" : v.ToString("R", Inv);

    private static double D(string v) => v.Length == 0 ? double.NaN : double.Parse(v, Inv);

    private static readonly string[] PairSiteColumns =
    [
        "result_type", "overlapping", "statistic", "p_value", "n", "statistic_n",
        "a_protein", "a_position", "a_residue", "a_modification", "a_protein_n_term", "a_canonical_key", "a_window",
        "b_protein", "b_position", "b_residue", "b_modification", "b_protein_n_term", "b_canonical_key", "b_window",
    ];

    /// <summary>Each pair, its mzLib fields and both sites, as <see cref="PairSitesFile"/>. Numbers are written
    /// round-trip (<c>R</c>), so <see cref="ReadPairSites"/> rebuilds the pair exactly.</summary>
    public static string WritePairSites(IEnumerable<(PtmPair Pair, PairSite A, PairSite B)> pairs, string path)
    {
        var text = new StringBuilder(string.Join('\t', PairSiteColumns)).Append('\n');
        foreach (var (p, a, b) in pairs)
        {
            foreach (var s in new[] { a.Modification, b.Modification, a.CanonicalKey, b.CanonicalKey })
                if (s.Contains('\t') || s.Contains('\n')) throw new RunnerException($"a site name holds a tab or a newline: {s}");
            text.Append(string.Join('\t', p.ResultType, p.Overlapping, R(p.Statistic), R(p.PValue), p.N.ToString(Inv), p.StatisticN.ToString(Inv),
                a.Protein, a.Position.ToString(Inv), a.Residue, a.Modification, a.ProteinNTerm, a.CanonicalKey, a.Window ?? "",
                b.Protein, b.Position.ToString(Inv), b.Residue, b.Modification, b.ProteinNTerm, b.CanonicalKey, b.Window ?? "")).Append('\n');
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>The pairs a <c>site_pairs</c> artefact stored, as mzLib pairs with their sites.</summary>
    public static List<(PtmPair Pair, PairSite A, PairSite B)> ReadPairSites(string path) =>
        ReadTsv(path, PairSiteColumns, PairSitesFile).Select(r =>
        {
            var a = new PairSite(r["a_protein"], int.Parse(r["a_position"], Inv), r["a_residue"][0], r["a_modification"], bool.Parse(r["a_protein_n_term"]), r["a_canonical_key"],
                r["a_window"].Length == 0 ? null : r["a_window"]);
            var b = new PairSite(r["b_protein"], int.Parse(r["b_position"], Inv), r["b_residue"][0], r["b_modification"], bool.Parse(r["b_protein_n_term"]), r["b_canonical_key"],
                r["b_window"].Length == 0 ? null : r["b_window"]);
            var pair = new PtmPair
            {
                ResultType = Enum.Parse<PairResultType>(r["result_type"]),
                SiteA = a.ToSite(),
                SiteB = b.ToSite(),
                Overlapping = bool.Parse(r["overlapping"]),
                Statistic = D(r["statistic"]),
                PValue = D(r["p_value"]),
                N = int.Parse(r["n"], Inv),
                StatisticN = int.Parse(r["statistic_n"], Inv),
            };
            return (pair, a, b);
        }).ToList();

    private static Dictionary<string, object?> Counts(IReadOnlyDictionary<string, long> counts) =>
        counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    private static Dictionary<string, object?> Gate(BundleRef bundle)
    {
        var (engine, version, lacks) = SearchGate(bundle);
        return new Dictionary<string, object?>
        {
            ["search_engine"] = engine, ["search_engine_version"] = version, ["lacks_met_removed_protein_n_term"] = lacks,
        };
    }

    /// <summary>Every database file the bundle's search read (ptmQtl D35: name, sha256, release), from its
    /// <c>bundle.json</c>. The release is not recorded by the ingest, so it is null: unknown, never guessed (G96).</summary>
    public static List<object?> Databases(BundleRef bundle)
    {
        var read = (bundle.Manifest.GetValueOrDefault("protein_databases") as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("read");
        return (read as IEnumerable<object?> ?? []).OfType<IReadOnlyDictionary<string, object?>>()
            .Select(d => (object?)new Dictionary<string, object?>
            {
                ["name"] = Path.GetFileName(((d.GetValueOrDefault("path") as string) ?? "").Replace('\\', '/')),
                ["sha256"] = d.GetValueOrDefault("sha256"),
                ["entries"] = d.GetValueOrDefault("entries"),
                ["release"] = null,
            }).ToList();
    }

    /// <summary>What the run record says of one bundle: its search gate (S6), its databases (D35).</summary>
    private static Dictionary<string, object?> BundleRecord(BundleRef bundle) => new()
    {
        ["dataset_id"] = bundle.DatasetId, ["bundle_id"] = bundle.BundleId, ["search_gate"] = Gate(bundle), ["databases"] = Databases(bundle),
    };

    /// <summary><c>ptmqtl.site_pairs</c>: one artefact per bundle.</summary>
    /// <exception cref="RunnerException">A missing or bad input, a bundle the reader refuses, or a development datarepo.</exception>
    public static RunResult SitePairs(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null)
    {
        RequireInputs(SitePairsEngine, inputs, ["enrichment", "cysteine"], ["capture_runs"]);
        if (bundles.Count == 0) throw new RunnerException($"{SitePairsEngine} needs at least one dataset");
        release ??= Release();
        var lists = ListShas(inputs);
        var deposits = Deposits(inputs);
        var result = new RunResult();
        foreach (var bundle in bundles)
        {
            var hashed = new Dictionary<string, string>(lists, StringComparer.Ordinal) { ["bundle"] = bundle.BundleId };
            var aid = EngineRunner.ArtefactId(SitePairsEngine, release, hashed, PtmQtlCore.PairDefinition);
            var existing = EngineRunner.ArtefactDir(store, SitePairsEngine, aid);
            if (File.Exists(Path.Combine(existing, DataRepo.Catalog.Runner.RunRecord)))
            {
                result.AlreadyDone.Add(ArtefactRef.Load(existing));
                continue;
            }
            install ??= EngineRunner.InstallIdentity();
            var (dataset, read, rules) = PtmQtlBundle.Read(bundle, DepositOf(deposits, bundle.DatasetId));
            var pairs = PtmQtlCore.SitePairs(dataset);
            var scratch = Path.Combine(Path.GetTempPath(), $"datarepo-ptmqtl-{Guid.NewGuid():N}.tsv");
            var excludedScratch = Path.Combine(Path.GetTempPath(), $"datarepo-ptmqtl-{Guid.NewGuid():N}.tsv");
            try
            {
                WritePairSites(pairs.Pairs, scratch);
                WriteExcluded(bundle.DatasetId, dataset.Excluded, excludedScratch);
                var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["definition_id"] = PtmQtlCore.PairDefinition,
                    ["release"] = release.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    ["inputs"] = hashed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    ["input_files"] = ListRoles.Where(inputs.ContainsKey).ToDictionary(r => r, r => (object?)BundleWriter.PathText(inputs[r])),
                    ["datarepo_install"] = install,
                    ["requested_for"] = new List<object?> { new Dictionary<string, object?> { ["dataset_id"] = bundle.DatasetId, ["bundle_id"] = bundle.BundleId } },
                    ["engine_summary"] = new Dictionary<string, object?>
                    {
                        ["dataset_id"] = bundle.DatasetId,
                        ["species"] = dataset.Species,
                        ["enriched"] = dataset.Enriched,
                        ["poolable"] = dataset.Poolable,
                        ["read"] = Counts(read),
                        ["left_out"] = Counts(pairs.LeftOut),
                        ["rules"] = rules,
                        ["rows_p"] = (long)pairs.Rows.Count(r => (string)r["result_type"]! == "P"),
                        ["rows_a"] = (long)pairs.Rows.Count(r => (string)r["result_type"]! == "A"),
                        ["search_gate"] = Gate(bundle),
                        ["quantified_psm_tier"] = dataset.Tiers.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                        ["databases"] = Databases(bundle),
                    },
                    ["acceptance"] = "passed",
                };
                result.Written.Add(EngineRunner.WriteArtefact(store, SitePairsEngine, aid, record,
                    new Dictionary<string, List<IReadOnlyDictionary<string, object?>>> { ["ptm_pairs"] = pairs.Rows.Cast<IReadOnlyDictionary<string, object?>>().ToList() },
                    new Dictionary<string, string> { [PairSitesFile] = scratch, [ExcludedFile] = excludedScratch }));
            }
            finally
            {
                if (File.Exists(scratch)) File.Delete(scratch);
                if (File.Exists(excludedScratch)) File.Delete(excludedScratch);
            }
        }
        return result;
    }

    /// <summary>The <c>site_pairs</c> artefact of each bundle under one set of operator lists, or a refusal naming the
    /// bundles without one.</summary>
    /// <param name="lists">The lists' sha256 by role (<see cref="ListRoles"/>); an artefact matches only when it was
    /// run under exactly these.</param>
    public static List<(BundleRef Bundle, ArtefactRef Artefact)> SitePairsOf(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> lists)
    {
        var all = DataRepo.Catalog.Runner.DiscoverArtefacts(store, SitePairsEngine);
        var found = new List<(BundleRef, ArtefactRef)>();
        var missing = new List<string>();
        bool SameLists(ArtefactRef a) => ListRoles.All(r => a.Inputs.GetValueOrDefault(r) == lists.GetValueOrDefault(r));
        foreach (var b in bundles)
        {
            var matches = all.Where(a => a.Inputs.GetValueOrDefault("bundle") == b.BundleId && SameLists(a)
                                         && a.Record.GetValueOrDefault("definition_id") as string == PtmQtlCore.PairDefinition).ToList();
            if (matches.Count == 0) missing.Add(b.DatasetId);
            else if (matches.Count > 1)
                throw new RunnerException($"{b.DatasetId}: {matches.Count} {SitePairsEngine} artefacts match ({string.Join(", ", matches.Select(m => m.ArtefactId))})");
            else found.Add((b, matches[0]));
        }
        if (missing.Count > 0)
            throw new RunnerException(
                $"{PoolPairsEngine}: no {SitePairsEngine} artefact under these lists and {PtmQtlCore.PairDefinition} for "
                + $"{string.Join(", ", missing)}. Run {SitePairsEngine} on them first, with the same --input enrichment, cysteine and capture_runs.");
        return found;
    }

    /// <summary>Whether a <c>site_pairs</c> artefact's rows may be pooled: its dataset's every run is a whole proteome (S3b).</summary>
    private static bool Poolable(ArtefactRef a) =>
        (a.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("poolable") is true;

    /// <summary><c>ptmqtl.pool_pairs</c>: one artefact per species, over the bundles' <c>site_pairs</c> artefacts (P22).</summary>
    /// <remarks>Every pooled artefact shares one definition and one enrichment list by construction (it is matched on
    /// both), as ptmQtl's pooling rule requires.</remarks>
    public static RunResult PoolPairs(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null)
    {
        RequireInputs(PoolPairsEngine, inputs, ["enrichment", "cysteine"], ["capture_runs"]);
        if (bundles.Count < 2) throw new RunnerException($"{PoolPairsEngine} pools two or more datasets");
        release ??= Release();
        var lists = ListShas(inputs);
        var result = new RunResult();
        string Species(ArtefactRef a) =>
            (a.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("species") as string
            ?? throw new RunnerException($"{SitePairsEngine} artefact {a.ArtefactId} records no species");
        // S3b: whole proteomes pool with whole proteomes; a dataset with any other run keeps its rows to itself.
        var all = SitePairsOf(store, bundles, lists);
        var notPooled = all.Where(x => !Poolable(x.Artefact)).Select(x => (object?)x.Bundle.DatasetId).ToList();
        foreach (var group in all.Where(x => Poolable(x.Artefact)).GroupBy(x => Species(x.Artefact)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.OrderBy(m => m.Bundle.DatasetId, StringComparer.Ordinal).ToList();
            if (members.Count < 2) continue;
            var hashed = new Dictionary<string, string>(lists, StringComparer.Ordinal);
            foreach (var (b, a) in members) hashed[$"site_pairs:{b.DatasetId}"] = a.ArtefactId;
            var aid = EngineRunner.ArtefactId(PoolPairsEngine, release, hashed, PtmQtlCore.PairDefinition);
            var existing = EngineRunner.ArtefactDir(store, PoolPairsEngine, aid);
            if (File.Exists(Path.Combine(existing, DataRepo.Catalog.Runner.RunRecord)))
            {
                result.AlreadyDone.Add(ArtefactRef.Load(existing));
                continue;
            }
            install ??= EngineRunner.InstallIdentity();
            var input = members.SelectMany(m => ReadPairSites(Path.Combine(m.Artefact.Path, PairSitesFile)).Select(p => (m.Bundle.DatasetId, p.Pair, p.A, p.B)));
            var pooled = PtmQtlCore.Pool(group.Key, input);
            var rows = pooled.Rows;
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["definition_id"] = PtmQtlCore.PairDefinition,
                ["release"] = release.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["inputs"] = hashed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["input_files"] = ListRoles.Where(inputs.ContainsKey).ToDictionary(r => r, r => (object?)BundleWriter.PathText(inputs[r])),
                ["datarepo_install"] = install,
                ["requested_for"] = members.Select(m => (object?)new Dictionary<string, object?> { ["dataset_id"] = m.Bundle.DatasetId, ["bundle_id"] = m.Bundle.BundleId }).ToList(),
                ["engine_summary"] = new Dictionary<string, object?>
                {
                    ["species"] = group.Key,
                    ["datasets"] = (long)members.Count,
                    ["rows_p"] = (long)rows.Count(r => (string)r["result_type"]! == "P"),
                    ["rows_a"] = (long)rows.Count(r => (string)r["result_type"]! == "A"),
                    ["left_out"] = Counts(pooled.LeftOut),
                    ["not_pooled_s3b"] = notPooled,
                    ["bundles"] = members.Select(m => (object?)BundleRecord(m.Bundle)).ToList(),
                },
                ["acceptance"] = "passed",
            };
            result.Written.Add(EngineRunner.WriteArtefact(store, PoolPairsEngine, aid, record,
                new Dictionary<string, List<IReadOnlyDictionary<string, object?>>> { ["ptm_pairs"] = rows.Cast<IReadOnlyDictionary<string, object?>>().ToList() }));
        }
        return result;
    }

    /// <summary>The operator's trait table for one dataset, per sample (after S4).</summary>
    /// <exception cref="RunnerException">Two traits in one file, a run of the dataset missing, or runs of one sample that disagree.</exception>
    public static (string TraitId, Dictionary<string, double> Trait, Dictionary<string, string> Replicate) Traits(
        string path, string datasetId, IReadOnlyDictionary<string, string> runToSample)
    {
        var rows = ReadTsv(path, ["dataset", "run", "replicate", "trait_id", "value"], "traits").Where(r => r["dataset"] == datasetId).ToList();
        var ids = rows.Select(r => r["trait_id"]).Distinct().ToList();
        if (ids.Count != 1) throw new RunnerException($"--input traits: {datasetId} has {ids.Count} trait ids; one trait per file");
        var trait = new Dictionary<string, double>(StringComparer.Ordinal);
        var replicate = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!runToSample.TryGetValue(r["run"], out var sample))
                throw new RunnerException($"--input traits: {datasetId} run {r["run"]} is not a run of the bundle");
            var value = D(r["value"]);
            if ((trait.TryGetValue(sample, out var t) && !t.Equals(value)) || (replicate.TryGetValue(sample, out var rep) && rep != r["replicate"]))
                throw new RunnerException($"--input traits: {datasetId} sample {sample}'s runs disagree on trait or replicate");
            trait[sample] = value;
            replicate[sample] = r["replicate"];
        }
        return (ids[0], trait, replicate);
    }

    private static readonly string[] FitColumns = ["scope", "trait_id", "feature_key", "status", "df", "replicates", "median_fraction",
        "covariate_effects", "replicate_variance", "residual_variance"];

    private static string WriteFits(IEnumerable<Row> fits, string path)
    {
        var text = new StringBuilder(string.Join('\t', FitColumns)).Append('\n');
        foreach (var f in fits)
            text.Append(string.Join('\t', FitColumns.Select(c => f[c] switch
            {
                null => "",
                double d => R(d),
                long l => l.ToString(Inv),
                IEnumerable<object?> list => string.Join(";", list.Select(x => x is double d ? R(d) : "")),
                var v => v.ToString(),
            }))).Append('\n');
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary><c>ptmqtl.site_traits</c>: one artefact per bundle, for the one trait in <c>--input traits</c>.</summary>
    public static RunResult SiteTraits(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null)
    {
        RequireInputs(SiteTraitsEngine, inputs, ["enrichment", "cysteine", "traits"], ["capture_runs"]);
        if (bundles.Count == 0) throw new RunnerException($"{SiteTraitsEngine} needs at least one dataset");
        release ??= Release();
        var lists = ListShas(inputs);
        var traitsSha = BundleWriter.Sha256File(inputs["traits"]);
        var deposits = Deposits(inputs);
        var result = new RunResult();
        foreach (var bundle in bundles)
        {
            var hashed = new Dictionary<string, string>(lists, StringComparer.Ordinal) { ["bundle"] = bundle.BundleId, ["traits"] = traitsSha };
            var aid = EngineRunner.ArtefactId(SiteTraitsEngine, release, hashed, PtmQtlCore.TraitDefinition);
            var existing = EngineRunner.ArtefactDir(store, SiteTraitsEngine, aid);
            if (File.Exists(Path.Combine(existing, DataRepo.Catalog.Runner.RunRecord)))
            {
                result.AlreadyDone.Add(ArtefactRef.Load(existing));
                continue;
            }
            install ??= EngineRunner.InstallIdentity();
            var (dataset, read, rules) = PtmQtlBundle.Read(bundle, DepositOf(deposits, bundle.DatasetId));
            var (traitId, trait, replicate) = Traits(inputs["traits"], bundle.DatasetId, dataset.RunToSample);
            var fit = PtmQtlCore.SiteTraits(dataset, traitId, trait, replicate);
            var scratch = Path.Combine(Path.GetTempPath(), $"datarepo-ptmqtl-{Guid.NewGuid():N}.tsv");
            var excludedScratch = Path.Combine(Path.GetTempPath(), $"datarepo-ptmqtl-{Guid.NewGuid():N}.tsv");
            try
            {
                WriteFits(fit.Fits, scratch);
                WriteExcluded(bundle.DatasetId, dataset.Excluded, excludedScratch);
                var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["definition_id"] = PtmQtlCore.TraitDefinition,
                    ["release"] = release.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    ["inputs"] = hashed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    ["input_files"] = ListRoles.Append("traits").Where(inputs.ContainsKey).ToDictionary(r => r, r => (object?)BundleWriter.PathText(inputs[r])),
                    ["datarepo_install"] = install,
                    ["requested_for"] = new List<object?> { new Dictionary<string, object?> { ["dataset_id"] = bundle.DatasetId, ["bundle_id"] = bundle.BundleId } },
                    ["engine_summary"] = new Dictionary<string, object?>
                    {
                        ["dataset_id"] = bundle.DatasetId,
                        ["trait_id"] = traitId,
                        ["read"] = Counts(read),
                        ["left_out"] = Counts(fit.LeftOut),
                        ["rules"] = rules,
                        ["sites"] = (long)fit.Rows.Count,
                        ["fitted"] = (long)fit.Rows.Count(r => r["estimable_int"] is true),
                        ["search_gate"] = Gate(bundle),
                        ["quantified_psm_tier"] = dataset.Tiers.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                        ["databases"] = Databases(bundle),
                    },
                    ["acceptance"] = "passed",
                };
                result.Written.Add(EngineRunner.WriteArtefact(store, SiteTraitsEngine, aid, record,
                    new Dictionary<string, List<IReadOnlyDictionary<string, object?>>> { ["trait_effects"] = fit.Rows.Cast<IReadOnlyDictionary<string, object?>>().ToList() },
                    new Dictionary<string, string> { [FitsFile] = scratch, [ExcludedFile] = excludedScratch }));
            }
            finally
            {
                if (File.Exists(scratch)) File.Delete(scratch);
                if (File.Exists(excludedScratch)) File.Delete(excludedScratch);
            }
        }
        return result;
    }
}
