using System.Text.RegularExpressions;

namespace PXReprise.Config;

public static class QuestionLoader
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static Question Load(string file)
    {
        file = Path.GetFullPath(file);
        string dir = Path.GetDirectoryName(file)!;
        var root = TomlSection.Load(file);

        string name = root.RequiredString("question");
        if (!Regex.IsMatch(name, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            throw new ConfigException(file, $"'question' must be lower-case words joined by '-', not '{name}'");
        string description = root.OptionalString("description") ?? "";
        var profiles = root.StringList("profiles", required: true);
        foreach (string p in profiles)
            if (!Regex.IsMatch(p, @"^[a-z0-9]+(-[a-z0-9]+)*@\d+$"))
                throw new ConfigException(file, $"'profiles' entry '{p}' must be id@version, e.g. label-free-dda@1");

        var disc = root.RequiredTable("discover");
        var keywords = disc.StringList("keywords", required: true);
        // Exact names, as PRIDE spells them. PRIDE carries one species under several spellings ("Mus musculus (mouse)"
        // and "Mus musculus"), and a substring rule would sweep in "Rattus rattus" with "Rattus norvegicus" (aging S-notes).
        var organisms = disc.StringList("organisms");
        var diseaseKeywords = disc.StringList("disease_keywords");
        var watchKeywords = disc.StringList("watch_keywords");
        disc.RefuseUnknownKeys();
        // One keyword, one meaning: a keyword in two lists would be both queued and never queued.
        var twice = keywords.Concat(diseaseKeywords).Concat(watchKeywords).GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (twice.Count > 0)
            throw new ConfigException(file, $"keyword(s) in more than one of keywords / disease_keywords / watch_keywords: {string.Join(", ", twice)}");

        var rel = root.OptionalTable("relevance");
        var require = Compile(file, "relevance.require_any", rel?.StringList("require_any") ?? Array.Empty<string>());
        var exclude = Compile(file, "relevance.exclude_if_any", rel?.StringList("exclude_if_any") ?? Array.Empty<string>());
        var unless = Compile(file, "relevance.unless_any", rel?.StringList("unless_any") ?? Array.Empty<string>());
        string? decisionsFile = rel?.OptionalString("decisions");
        rel?.RefuseUnknownKeys();
        if (unless.Count > 0 && exclude.Count == 0)
            throw new ConfigException(file, "'relevance.unless_any' only qualifies 'relevance.exclude_if_any', which is empty");
        var decisions = decisionsFile is null
            ? new Dictionary<string, Decision>()
            : LoadDecisions(Path.Combine(dir, decisionsFile));

        var overlays = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (root.OptionalTable("overlays") is { } ov)
        {
            foreach (string organism in ov.Keys.ToList())
            {
                var o = ov.RequiredTable(organism);
                overlays[organism] = o.StringList("extra_xml", required: true);
                o.RefuseUnknownKeys();
            }
            ov.RefuseUnknownKeys();
        }

        var traits = root.OptionalTable("traits");
        var requiredTraits = traits?.StringList("required") ?? Array.Empty<string>();
        var optionalTraits = traits?.StringList("optional") ?? Array.Empty<string>();
        string? traitsSource = traits?.OptionalString("source");
        traits?.RefuseUnknownKeys();

        var holds = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (root.OptionalTable("holds") is { } h)
        {
            foreach (string acc in h.Keys.ToList())
            {
                if (!IsAccession(acc)) throw new ConfigException(file, $"'holds' key '{acc}' is not a PXD accession");
                holds[acc] = h.RequiredString(acc);
            }
            h.RefuseUnknownKeys();
        }

        var pub = root.OptionalTable("publish");
        string? studyLayer = pub?.OptionalString("study_layer");
        string? manifest = pub?.OptionalString("manifest");
        var command = pub?.StringList("command") ?? Array.Empty<string>();
        // D41 rule 6 (PXR-A72, PXR-A75): one build per 6 hours at most, not one per ingest; a build of aging's catalog
        // passed an hour on 2026-10-11, so the old fixed 1-hour limit killed every one.
        double everyHours = pub?.OptionalNumber("every_h") ?? 6, publishTimeout = pub?.OptionalNumber("timeout_h") ?? 4;
        if (everyHours < 0) throw new ConfigException(file, "publish.every_h must be 0 (after each ingest) or more");
        if (publishTimeout <= 0) throw new ConfigException(file, "publish.timeout_h must be positive");
        pub?.RefuseUnknownKeys();

        BatchSettings? batch = null;
        if (root.OptionalTable("batch") is { } b)
        {
            batch = new BatchSettings(Abs(dir, b.RequiredString("run_root")), Abs(dir, b.RequiredString("state_dir")),
                Abs(dir, b.RequiredString("queue")));
            b.RefuseUnknownKeys();
        }

        DesignSettings? designs = null;
        if (root.OptionalTable("designs") is { } dg)
        {
            designs = new DesignSettings(Abs(dir, dg.RequiredString("dir")), dg.StringList("condition_columns"));
            dg.RefuseUnknownKeys();
        }

        // D28 (D-2): per-deposit exceptions to the profile's [deposit] caps and file selection.
        var exceptions = new SortedDictionary<string, DepositException>(StringComparer.Ordinal);
        if (root.OptionalTable("exceptions") is { } ex)
        {
            foreach (string acc in ex.Keys.ToList())
            {
                if (!IsAccession(acc)) throw new ConfigException(file, $"'exceptions' key '{acc}' is not a PXD accession");
                var t = ex.RequiredTable(acc);
                var e = ParseException(file, dir, t, $"exceptions.{acc}", acc, t.RequiredString("reason"), null);
                t.RefuseUnknownKeys();
                if (!e.SelectsFiles && e.MaxFiles is null && e.MaxFileMb is null && e.MaxEstimatedMs2 is null && e.DeferIfMedianFileMb is null
                    && e.Alkylation is null && e.Species is null && e.ReplaceFiles is null && e.MinIdentificationRate is null)
                    throw new ConfigException(file, $"exceptions.{acc} changes nothing: give a cap, include_files / exclude_files, alkylation, species, replace_files or min_identification_rate");
                exceptions[acc] = e;
            }
            ex.RefuseUnknownKeys();
        }

        // v0.4.43 (D33, design/PLAN_split-deposits.md; the user, 2026-11-08: "These dual species repos should be split and
        // that should be an option for the human"): [splits.PXD...] takes `reason` and `by`, and one table per group, each
        // with the keys an [exceptions] entry takes. Each group is its own search, keyed <accession>-<group>.
        var splits = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (root.OptionalTable("splits") is { } sx)
        {
            foreach (string acc in sx.Keys.ToList())
            {
                if (!IsAccession(acc)) throw new ConfigException(file, $"'splits' key '{acc}' is not a PXD accession");
                // One deposit, one say: an [exceptions] entry beside its split would leave unclear which one a group follows.
                if (exceptions.ContainsKey(acc))
                    throw new ConfigException(file, $"{acc} has both [exceptions.{acc}] and [splits.{acc}]: put its settings in each group of the split");
                var s = sx.RequiredTable(acc);
                string reason = s.RequiredString("reason");
                string by = s.RequiredString("by");
                if (!SplitGroup.Reasons.Contains(by))
                    throw new ConfigException(file, $"splits.{acc}.by must be one of {string.Join(", ", SplitGroup.Reasons)} (got '{by}')");
                var groups = s.Keys.Where(k => k is not ("reason" or "by")).ToList();
                foreach (string g in groups)
                    if (!SearchKey.GroupToken.IsMatch(g))
                        throw new ConfigException(file, $"splits.{acc}.{g}: a group is named with lower-case letters and digits only (rat, mouse, phospho)");
                if (groups.Count < 2)
                    throw new ConfigException(file, $"splits.{acc} names {groups.Count} group: a split is two searches or more (one group is an [exceptions] entry)");
                var keys = groups.Select(g => SearchKey.Of(acc, g)).ToList();
                foreach (string g in groups)
                {
                    var t = s.RequiredTable(g);
                    var split = new SplitGroup(acc, g, by, reason, keys);
                    var e = ParseException(file, dir, t, $"splits.{acc}.{g}", SearchKey.Of(acc, g), t.OptionalString("reason") ?? reason, split);
                    t.RefuseUnknownKeys();
                    // The groups are files, not samples (plan rule 1): each group says which raw files are its own.
                    if (!e.SelectsFiles)
                        throw new ConfigException(file, $"splits.{acc}.{g} selects no files: give include_files or exclude_files, so each raw file is in one group");
                    if (by == "species" && e.Species is null)
                        throw new ConfigException(file, $"splits.{acc}.{g}: a split by species names each group's species (species = \"mouse\")");
                    exceptions[split.Key] = e;
                }
                if (by == "species" && keys.Select(k => exceptions[k].Species).Distinct(StringComparer.Ordinal).Count() < keys.Count)
                    throw new ConfigException(file, $"splits.{acc}: two groups name the same species; a split by species searches each species once");
                s.RefuseUnknownKeys();
                splits[acc] = keys;
            }
            sx.RefuseUnknownKeys();
        }

        // v0.4.46 (aging PXR-A179, their D171): [gate], rules over a deposit's own text before the census queues it.
        GateTerms? gate = null;
        if (root.OptionalTable("gate") is { } gt)
        {
            IReadOnlyList<GateTerm> Terms(string key) => gt.StringList(key).Select(t => Term(file, $"gate.{key}", t)).ToList();
            var strong = Terms("strong");
            if (strong.Count == 0) throw new ConfigException(file, "gate.strong names no term: the gate would queue nothing");
            string referenceGroup = gt.OptionalString("reference_group") ?? "required";
            if (referenceGroup is not ("required" or "off"))
                throw new ConfigException(file, $"gate.reference_group must be \"required\" or \"off\" (got '{referenceGroup}')");
            gate = new GateTerms(strong, Terms("weak"), Terms("other_sense"), Terms("watch_if_any"), Terms("watch_without_strong"),
                referenceGroup == "required");
            gt.RefuseUnknownKeys();
        }

        root.RefuseUnknownKeys();
        return new Question(name, description, file, profiles, keywords, organisms,
            new RelevanceRules(require, exclude, unless, decisions), overlays, requiredTraits, optionalTraits,
            traitsSource, holds, studyLayer, batch,
            new PublishSettings(manifest is null ? null : Abs(dir, manifest), command, everyHours, publishTimeout), designs,
            new DiscoverLists(diseaseKeywords, watchKeywords), exceptions, splits, gate);
    }

    /// <summary>
    /// A gate term, matched case-insensitively on word boundaries: no letter, digit or underscore just before or after it, so
    /// "old" does not match "threshold" and a term may itself start or end with punctuation. The text it is matched against
    /// has every Unicode dash read as a hyphen and runs of whitespace as one space (Census.Gate.Normalize).
    /// </summary>
    internal static GateTerm Term(string file, string key, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) throw new ConfigException(file, $"'{key}' holds an empty term");
        try { return new GateTerm(term, new Regex($@"(?<![\p{{L}}\p{{N}}_])(?:{term})(?![\p{{L}}\p{{N}}_])", Opt)); }
        catch (ArgumentException e) { throw new ConfigException(file, $"'{key}' term '{term}' is not a valid regular expression: {e.Message}"); }
    }

    /// <summary>The keys an <c>[exceptions]</c> entry and a <c>[splits]</c> group both take, read from <paramref name="t"/>.</summary>
    private static DepositException ParseException(string file, string dir, TomlSection t, string where, string key, string reason, SplitGroup? split)
    {
        Regex? Rx(string k) => t.OptionalString(k) is { } s
            ? TryRegex(s) ?? throw new ConfigException(file, $"{where}.{k} is not a valid regular expression: '{s}'")
            : null;
        int? Int(string k) => t.OptionalInteger(k) is { } v ? checked((int)v) : null;
        string? alkylation = t.OptionalString("alkylation");
        if (alkylation is not null && alkylation != DepositException.NoAlkylation)
            throw new ConfigException(file, $"{where}.alkylation can only be \"{DepositException.NoAlkylation}\" (got '{alkylation}'): name another alkylant in a curated SDRF");
        // A profile database key ("rat"), as the queue names organisms; whether a profile has it is checked
        // against the profiles (CheckAgainstProfiles), which this file does not load.
        string? species = t.OptionalString("species");
        if (species is not null && !Regex.IsMatch(species, "^[a-z]+(_[a-z]+)*$"))
            throw new ConfigException(file, $"{where}.species must be a profile database key such as \"rat\" (got '{species}')");
        // v0.4.39 (aging PXR-A158): [exceptions.PXD.replace_files."<raw file name>"] path, pride_sha1, sha256, note.
        Dictionary<string, FileReplacement>? replace = null;
        if (t.OptionalTable("replace_files") is { } rt)
        {
            replace = new Dictionary<string, FileReplacement>(StringComparer.Ordinal);
            foreach (string rawName in rt.Keys.ToList())
            {
                if (rawName.Length == 0 || rawName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                    throw new ConfigException(file, $"{where}.replace_files key '{rawName}' must be a raw file name as PRIDE lists it, not a path");
                var f = rt.RequiredTable(rawName);
                string Hex(string k, int length) => f.RequiredString(k).ToLowerInvariant() is var h && h.Length == length && h.All(Uri.IsHexDigit)
                    ? h : throw new ConfigException(file, $"{where}.replace_files.\"{rawName}\".{k} must be {length} hex digits");
                replace[rawName] = new FileReplacement(Abs(dir, f.RequiredString("path")), Hex("pride_sha1", 40), Hex("sha256", 64), f.RequiredString("note"));
                f.RefuseUnknownKeys();
            }
            if (replace.Count == 0) throw new ConfigException(file, $"{where}.replace_files names no file");
        }
        // v0.4.44: the deposit's own identification floor (BatchRunner.MinIdentificationRate); 0 delivers whatever the search found.
        double? minIdRate = t.OptionalNumber("min_identification_rate");
        if (minIdRate is < 0 or > 1)
            throw new ConfigException(file, $"{where}.min_identification_rate is a share of the MS2 spectra, from 0 to 1 (got {minIdRate})");
        return new DepositException(key, reason, Rx("include_files"), Rx("exclude_files"),
            Int("max_files"), Int("max_file_mb"), t.OptionalInteger("max_est_ms2"), Int("defer_if_median_file_mb"), alkylation, species, replace, split,
            minIdRate);
    }

    /// <summary>
    /// What the question file alone cannot check: each <c>[exceptions]</c> <c>species</c> is a database key of at least
    /// one of the question's profiles (v0.4.19). Run by <c>validate</c> and before a batch starts.
    /// </summary>
    public static void CheckAgainstProfiles(Question q, IReadOnlyDictionary<string, Profile> profiles)
    {
        var keys = q.Profiles.Where(profiles.ContainsKey).SelectMany(p => profiles[p].Databases.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var e in q.Exceptions?.Values ?? Enumerable.Empty<DepositException>())
            if (e.Species is { } sp && !keys.Contains(sp))
                throw new ConfigException(q.SourceFile, $"{(e.Split is { } s ? $"splits.{s.Accession}.{s.Group}" : $"exceptions.{e.Accession}")}.species '{sp}' is not a database of the question's profiles ({string.Join(", ", keys.Order(StringComparer.Ordinal))})");
    }

    /// <summary>A path in a question file is relative to the file, so a project folder can move.</summary>
    private static string Abs(string dir, string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dir, path));

    /// <summary>
    /// <c>decisions.tsv</c>: a header <c>accession	verdict	reason</c>, then one row per hand call. A reason is required:
    /// a decision nobody can explain later cannot be reviewed.
    /// </summary>
    public static IReadOnlyDictionary<string, Decision> LoadDecisions(string file)
    {
        if (!File.Exists(file)) throw new ConfigException(file, "the decisions file named by relevance.decisions does not exist");
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count == 0 || lines[0] != "accession\tverdict\treason")
            throw new ConfigException(file, "the header must be exactly: accession<TAB>verdict<TAB>reason");
        var result = new SortedDictionary<string, Decision>(StringComparer.Ordinal);
        foreach (var (line, n) in lines.Skip(1).Select((l, i) => (l, i + 2)))
        {
            string[] f = line.Split('\t');
            if (f.Length != 3) throw new ConfigException(file, $"line {n}: expected 3 tab-separated fields, found {f.Length}");
            if (!IsAccession(f[0])) throw new ConfigException(file, $"line {n}: '{f[0]}' is not a PXD accession");
            var verdict = ProfileLoader.ParseEnum<DecisionVerdict>(file, $"line {n} verdict", f[1]);
            if (string.IsNullOrWhiteSpace(f[2])) throw new ConfigException(file, $"line {n}: a decision needs a reason");
            if (!result.TryAdd(f[0], new Decision(f[0], verdict, f[2])))
                throw new ConfigException(file, $"line {n}: {f[0]} is decided twice");
        }
        return result;
    }

    private static bool IsAccession(string s) => Regex.IsMatch(s, @"^PXD\d{6}$");

    private static Regex? TryRegex(string pattern)
    {
        try { return new Regex(pattern, Opt); }
        catch (ArgumentException) { return null; }
    }

    private static IReadOnlyList<Regex> Compile(string file, string key, IReadOnlyList<string> patterns)
    {
        var list = new List<Regex>();
        foreach (string p in patterns)
        {
            try { list.Add(new Regex(p, Opt)); }
            catch (ArgumentException e) { throw new ConfigException(file, $"'{key}' pattern '{p}' is not a valid regex: {e.Message}"); }
        }
        return list;
    }
}
