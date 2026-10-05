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

        root.RefuseUnknownKeys();
        return new Question(name, description, file, profiles, keywords, organisms,
            new RelevanceRules(require, exclude, unless, decisions), overlays, requiredTraits, optionalTraits,
            traitsSource, holds, studyLayer, batch,
            new PublishSettings(manifest is null ? null : Abs(dir, manifest), command), designs,
            new DiscoverLists(diseaseKeywords, watchKeywords));
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
