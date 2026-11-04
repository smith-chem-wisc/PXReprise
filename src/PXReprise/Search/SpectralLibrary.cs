using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Omics.SpectrumMatch;
using PXReprise.Cli;
using PXReprise.Provenance;

namespace PXReprise.Search;

/// <summary>What the search stage needs to know before it runs: write a first library, or update the current one.</summary>
public sealed record LibraryPlan(string Organism, string Mode, string? LibraryIn, int? ParentVersion, string Root)
{
    /// <summary>MetaMorpheus's own output names (MetaMorpheusTask.cs:1125 and :1139). The trailing '_' keeps
    /// SpectralLibrary_* from also matching updateSpectralLibrary_* on a case-insensitive filesystem.</summary>
    public string OutputPattern => Mode == "write" ? "SpectralLibrary_*.msp" : "updateSpectralLibrary_*.msp";
}

/// <summary>
/// Spectral libraries, one chain per organism, built up across searches (aging D33). The first search of an organism
/// writes one; every later search updates the current one and consumes it; every version is kept so the chain can be
/// rolled back. Search task only. The registry format is aging's (<c>aging-spectral-library-registry/1</c>), so the
/// engine continues the chains the aging batch started.
///
/// MetaMorpheus's side, from the 1.1.11 source: the two TOML booleans act independently (both true writes two
/// libraries, so never both); an existing library is passed as another -d and is recognised by extension; an update
/// MERGES, keeping the better-evidenced spectrum per (sequence, charge), so a library only grows in coverage; and the
/// output name is timestamped, which is why the produced file must be discovered and registered.
/// </summary>
public static class SpectralLibrary
{
    public const string Schema = "aging-spectral-library-registry/1";

    public static string RegistryPath(string root) => Path.Combine(root, "registry.json");

    public static JsonObject ReadRegistry(string root)
    {
        string p = RegistryPath(root);
        if (!File.Exists(p)) return new JsonObject { ["schema"] = Schema, ["organisms"] = new JsonObject() };
        var reg = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        string? schema = reg["schema"]?.GetValue<string>();
        if (schema != Schema) throw new SearchSetupException($"{p}: registry schema is '{schema}', this code writes '{Schema}'");
        return reg;
    }

    /// <summary>Atomic replace, so an interrupted run cannot leave a half-written registry behind.</summary>
    private static void WriteRegistry(string root, JsonObject reg)
    {
        string p = RegistryPath(root);
        Directory.CreateDirectory(root);
        string tmp = p + ".tmp";
        File.WriteAllText(tmp, reg.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }) + "\n");
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>
    /// Write or update for this run. The organism key is stated, never inferred from a PRIDE facet string. A registry
    /// whose current library is missing on disk is refused, never silently restarted: that would start a second chain
    /// and discard every spectrum the first had accumulated.
    /// </summary>
    /// <param name="label">v0.4.34: an isobaric kit's modification id (e.g. "TMT10"). Its peptides weigh more and fragment
    /// differently, so its spectra get a chain of their own (<c>mouse_tmt10</c>), which starts blank and never reads or
    /// extends the label-free one (the user, 2026-10-29: "tmt will have to start with a blank spectrum library ... tmt
    /// modified peptides are all shifted"). Null is label-free: the organism's own chain.</param>
    public static LibraryPlan Plan(string root, string organism, string? label = null)
    {
        string key = organism.Trim().ToLowerInvariant().Replace(' ', '_');
        if (key.Length == 0) throw new UsageException("a spectral library needs an organism");
        if (label is { Length: > 0 }) key += "_" + new string(label.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var entry = ReadRegistry(root)["organisms"]![key]?.AsObject();
        string? current = entry?["current"]?.GetValue<string>();
        if (current is null) return new LibraryPlan(key, "write", null, null, root);
        string lib = Path.Combine(root, current);
        if (!File.Exists(lib))
            throw new SearchSetupException($"the registry says {key}'s current library is {current}, but {lib} does not exist. Restore it, or roll back to a version on disk (`pxreprise library list`).");
        return new LibraryPlan(key, "update", lib, VersionOf(entry!, current), root);
    }

    /// <summary>
    /// Copies the library MetaMorpheus produced into the registry as the next version and advances <c>current</c>.
    /// Refuses (without failing the search) when the file is absent, or when another search registered for this
    /// organism in between: registering then would silently discard that search's contribution. With
    /// <paramref name="cleaning"/>, the new version also leaves out what <see cref="LibraryCleaning"/> judges (v0.4.32), so
    /// a library cleaned once stays clean.
    /// </summary>
    /// <param name="routeClasses">v0.4.38: the <see cref="LibraryRouting"/> classes this deposit enriches. A spectrum
    /// carrying one of them goes to that class's chain (<see cref="LibraryRouting.ChainOf"/>) instead of this one, unless
    /// the parent version already holds its name ("PEPT[...]IDE/2"): then the parent's spectrum is kept, so an enriched
    /// deposit neither adds to nor replaces the regular chain's spectra of the class it enriches. Every other spectrum is
    /// registered here as before. Needs <paramref name="cleaning"/>, which resolves the modification names.</param>
    public static JsonObject Register(LibraryPlan plan, string searchTaskDir, string runLabel, string accession, string metamorpheus,
        LibraryCleaning? cleaning = null, IReadOnlyCollection<string>? routeClasses = null)
    {
        if (routeClasses is { Count: > 0 } && cleaning is null)
            throw new ArgumentException("routing a library by class needs the cleaning rules, which resolve modification names", nameof(cleaning));
        var produced = Directory.EnumerateFiles(searchTaskDir, plan.OutputPattern).Select(Path.GetFileName)
            .Where(n => plan.Mode != "write" || !n!.StartsWith("update", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (produced.Count == 0)
            throw new SearchSetupException($"spectral_library: {plan.Mode} mode was configured but no {plan.OutputPattern} was written to {searchTaskDir}. The library was NOT advanced.");
        string src = Path.Combine(searchTaskDir, produced[^1]!);
        // Two searches can finish together (search_slots > 1): one registers at a time, and the second then sees the
        // first's version and is refused below rather than overwriting it.
        lock (RegistryGate) return RegisterLocked(plan, src, runLabel, accession, metamorpheus, cleaning, routeClasses);
    }

    private static readonly object RegistryGate = new();

    private static JsonObject RegisterLocked(LibraryPlan plan, string src, string runLabel, string accession, string metamorpheus,
        LibraryCleaning? cleaning, IReadOnlyCollection<string>? routeClasses)
    {
        var reg = ReadRegistry(plan.Root);
        var organisms = reg["organisms"]!.AsObject();
        if (organisms[plan.Organism] is not JsonObject entry)
            organisms[plan.Organism] = entry = new JsonObject { ["current"] = null, ["versions"] = new JsonArray() };
        string? current = entry["current"]?.GetValue<string>();
        if (current is not null || plan.ParentVersion is not null)
        {
            int? live = current is null ? null : VersionOf(entry, current);
            if (live != plan.ParentVersion)
                throw new SearchSetupException($"spectral_library: {plan.Organism}'s current library moved from version {plan.ParentVersion} to {live} while this search was running. This run's library is at {src} and has NOT been registered, so nothing was lost; merge it deliberately.");
        }

        var (version, rel, dest) = NextVersion(plan.Root, plan.Organism, entry);
        JsonObject ProducedBy() => new()
        {
            ["run"] = runLabel, ["accession"] = accession, ["metamorpheus"] = metamorpheus,
            ["metamorpheus_path"] = src, ["metamorpheus_filename"] = Path.GetFileName(src),
        };
        // COPY, not move: a search that is not delivered keeps what MetaMorpheus wrote. After delivery the run folder's copy
        // is deleted when its bytes still match this version's sha256 (PruneStage, v0.4.18); a copy that lost unreadable
        // entries never matches, so MetaMorpheus's whole file stays in the run folder.
        if (routeClasses is not { Count: > 0 })
        {
            var dropped = CopyClean(src, dest, cleaning);
            var rec = VersionRecord(version, rel, plan.Mode, plan.ParentVersion, dest, dropped, cleaning, ProducedBy());
            entry["versions"]!.AsArray().Add(rec);
            entry["current"] = rel;
            WriteRegistry(plan.Root, reg);
            return rec;
        }

        // v0.4.38: route the enriched-for classes. The regular version first, then one new version per class chain that
        // received spectra, all in one registry write.
        var routed = CopyRouted(src, dest, cleaning!, routeClasses, plan.LibraryIn);
        var regular = VersionRecord(version, rel, plan.Mode, plan.ParentVersion, dest, routed.Dropped, cleaning, ProducedBy());
        var routing = new JsonObject
        {
            ["rule"] = "pxreprise-library-routing/1: a spectrum carrying a PTM class the deposit enriches goes to that class's chain, unless the parent version holds its name (then the parent's spectrum stays); classes by modification mass and residue",
            ["classes"] = new JsonArray(routeClasses.Select(c => (JsonNode)c).ToArray()),
            ["kept_parent_spectrum"] = routed.KeptParent,
        };
        var to = new JsonObject();
        foreach (var (cls, blocks) in routed.ByClass) to[LibraryRouting.ChainOf(plan.Organism, cls)] = blocks.Count;
        routing["routed"] = to;
        regular["routing"] = routing;
        entry["versions"]!.AsArray().Add(regular);
        entry["current"] = rel;
        var chains = new JsonArray();
        foreach (var (cls, blocks) in routed.ByClass)
        {
            if (blocks.Count == 0) continue;
            string chain = LibraryRouting.ChainOf(plan.Organism, cls);
            if (organisms[chain] is not JsonObject ce)
                organisms[chain] = ce = new JsonObject { ["current"] = null, ["versions"] = new JsonArray() };
            string? parentRel = ce["current"]?.GetValue<string>();
            string? parentPath = parentRel is null ? null : Path.Combine(plan.Root, parentRel);
            if (parentPath is not null && !File.Exists(parentPath))
                throw new SearchSetupException($"spectral_library: the registry says {chain}'s current library is {parentRel}, but {parentPath} does not exist. Nothing was registered.");
            var (cv, crel, cdest) = NextVersion(plan.Root, chain, ce);
            AppendBlocks(parentPath, blocks, cdest, NewLineOf(src));
            var produced = ProducedBy();
            produced["routed_from"] = new JsonObject { ["chain"] = plan.Organism, ["version"] = version, ["class"] = cls, ["spectra"] = blocks.Count };
            var crec = VersionRecord(cv, crel, parentRel is null ? "write" : "update", parentRel is null ? null : VersionOf(ce, parentRel),
                cdest, Dropped.None, null, produced);
            ce["versions"]!.AsArray().Add(crec);
            ce["current"] = crel;
            chains.Add(new JsonObject { ["chain"] = chain, ["version"] = cv, ["path"] = crel, ["n_spectra"] = crec["n_spectra"]!.DeepClone() });
        }
        WriteRegistry(plan.Root, reg);
        var result = (JsonObject)regular.DeepClone();
        result["routed_versions"] = chains;
        return result;
    }

    /// <summary>What <see cref="CopyRouted"/> did: the regular copy's drops, how many class spectra kept the parent's
    /// version, and the entries (each its lines, "Name:" first) for each class chain.</summary>
    public sealed record Routed(Dropped Dropped, int KeptParent, IReadOnlyDictionary<string, List<List<string>>> ByClass);

    /// <summary>
    /// v0.4.38: <see cref="CopyClean"/>, plus routing. An entry the cleaning keeps that carries a class in
    /// <paramref name="routeClasses"/> is not written to <paramref name="dest"/>: when <paramref name="parent"/> holds its
    /// name, the parent's entry is written in its place (the same bytes when MetaMorpheus kept it); otherwise it is
    /// returned for that class's chain (the first class in <see cref="LibraryRouting.Classes"/> order it carries).
    /// </summary>
    public static Routed CopyRouted(string src, string dest, LibraryCleaning cleaning, IReadOnlyCollection<string> routeClasses, string? parent)
    {
        string nl = NewLineOf(src);
        var verdicts = cleaning.Judge(File.ReadLines(src).Select(LibraryCleaning.FullSequenceOf).OfType<string>());
        var order = LibraryRouting.Classes.Select(c => c.Key).Where(routeClasses.Contains).ToList();
        // pass 1: the routed candidates (a kept entry carrying a routed class), by name, each with its entry's hash. An
        // update's output holds every parent entry too, so on a large chain these are many (mouse v119: ~106,000 phospho).
        var classOf = new Dictionary<string, (string Class, string Hash)>(StringComparer.Ordinal);
        foreach (var block in Entries(src))
        {
            if (UnreadableName(block[0])) continue;
            string full = LibraryCleaning.FullSequenceOf(block[0])!;
            if (verdicts.ContainsKey(full)) continue;
            var carried = cleaning.ClassesOf(full);
            if (order.FirstOrDefault(carried.Contains) is { } cls) classOf[NameOf(block[0])] = (cls, HashOf(block));
        }
        // pass 2: which names the parent holds, and the parent's entry wherever MetaMorpheus replaced it (only those are
        // kept in memory: an unchanged entry is written from the output, byte for byte the same)
        var inParent = new HashSet<string>(StringComparer.Ordinal);
        var replaced = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (parent is not null && classOf.Count > 0)
            foreach (var block in Entries(parent))
            {
                string name = NameOf(block[0]);
                if (!classOf.TryGetValue(name, out var c)) continue;
                inParent.Add(name);
                if (HashOf(block) != c.Hash) replaced[name] = block;
            }
        // pass 3: write
        int unreadable = 0, blocked = 0, doubtful = 0, keptParent = 0;
        var byMod = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byClass = order.ToDictionary(c => c, _ => new List<List<string>>(), StringComparer.Ordinal);
        using var w = new StreamWriter(new FileStream(dest, FileMode.CreateNew), new UTF8Encoding(false)) { NewLine = nl };
        foreach (var block in Entries(src, keepPreamble: true))
        {
            if (!block[0].StartsWith("Name:", StringComparison.Ordinal)) { foreach (string l in block) w.WriteLine(l); continue; }
            if (UnreadableName(block[0])) { unreadable++; continue; }
            if (verdicts.TryGetValue(LibraryCleaning.FullSequenceOf(block[0])!, out var v))
            {
                if (v.Reason == "blocked_cterm") blocked++; else doubtful++;
                byMod[v.Modification] = byMod.GetValueOrDefault(v.Modification) + 1;
                continue;
            }
            string name = NameOf(block[0]);
            if (classOf.TryGetValue(name, out var c))
            {
                if (inParent.Contains(name))
                {
                    foreach (string l in replaced.TryGetValue(name, out var kept) ? kept : block) w.WriteLine(l);
                    keptParent++;
                }
                else byClass[c.Class].Add(block);
                continue;
            }
            foreach (string l in block) w.WriteLine(l);
        }
        return new Routed(new Dropped(unreadable, blocked, doubtful, byMod), keptParent, byClass);
    }

    private static string HashOf(List<string> block) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', block))));

    /// <summary>A class chain's next version: its current library's entries (if any) without the names
    /// <paramref name="blocks"/> carry, then <paramref name="blocks"/>; a later spectrum replaces an earlier one.</summary>
    private static void AppendBlocks(string? parent, List<List<string>> blocks, string dest, string nl)
    {
        var names = blocks.Select(b => NameOf(b[0])).ToHashSet(StringComparer.Ordinal);
        using var w = new StreamWriter(new FileStream(dest, FileMode.CreateNew), new UTF8Encoding(false)) { NewLine = nl };
        if (parent is not null)
            foreach (var block in Entries(parent, keepPreamble: true))
                if (!block[0].StartsWith("Name:", StringComparison.Ordinal) || !names.Contains(NameOf(block[0])))
                    foreach (string l in block) w.WriteLine(l);
        foreach (var block in blocks)
            foreach (string l in block) w.WriteLine(l);
    }

    /// <summary>A library entry's name: its "Name:" line's text, charge included ("PEPT[...]IDE/2").</summary>
    private static string NameOf(string nameLine) => nameLine[5..].Trim();

    /// <summary>A library's entries, each from its "Name:" line to the next; with <paramref name="keepPreamble"/>, any lines
    /// before the first entry come first as a block of their own.</summary>
    private static IEnumerable<List<string>> Entries(string path, bool keepPreamble = false)
    {
        List<string>? current = null;
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("Name:", StringComparison.Ordinal))
            {
                if (current is { Count: > 0 } && (keepPreamble || current[0].StartsWith("Name:", StringComparison.Ordinal))) yield return current;
                current = new List<string> { line };
            }
            else (current ??= new List<string>()).Add(line);
        }
        if (current is { Count: > 0 } && (keepPreamble || current[0].StartsWith("Name:", StringComparison.Ordinal))) yield return current;
    }

    /// <summary>
    /// A new version of <paramref name="organism"/>'s current library without its unreadable entries and, with
    /// <paramref name="cleaning"/>, without what <see cref="LibraryCleaning"/> judges (mode <c>repair</c>), made current.
    /// Nothing is written when there is nothing to drop. For libraries registered before v0.4.20 dropped unreadable
    /// entries (mouse v065, human v057: Unimod <c>NEM:2H(5)</c>), and before v0.4.32 cleaned them. Run it with no driver
    /// running: a search that planned on the old current would then be refused at registration (nothing lost).
    /// </summary>
    public static JsonObject Repair(string root, string organism, string reason, LibraryCleaning? cleaning = null)
    {
        lock (RegistryGate)
        {
            var reg = ReadRegistry(root);
            var entry = reg["organisms"]![organism]?.AsObject() ?? throw new UsageException($"no libraries registered for '{organism}'");
            string current = entry["current"]?.GetValue<string>() ?? throw new UsageException($"{organism} has no current library");
            int parent = VersionOf(entry, current) ?? throw new SearchSetupException($"{organism}'s current library {current} is not a registered version");
            string src = Path.Combine(root, current);
            var (version, rel, dest) = NextVersion(root, organism, entry);
            var dropped = CopyClean(src, dest, cleaning);
            if (dropped.Total == 0)
            {
                File.Delete(dest);
                return new JsonObject { ["organism"] = organism, ["repaired"] = false, ["current"] = current, ["version"] = parent };
            }
            var rec = VersionRecord(version, rel, "repair", parent, dest, dropped, cleaning, new JsonObject
            {
                ["repair_of"] = current, ["reason"] = reason, ["pxreprise"] = RunRecord.Versions()["pxreprise"],
            });
            entry["versions"]!.AsArray().Add(rec);
            entry["current"] = rel;
            WriteRegistry(root, reg);
            var result = (JsonObject)rec.DeepClone();
            result["organism"] = organism;
            result["repaired"] = true;
            return result;
        }
    }

    /// <summary>Each organism's current library: its version, spectra and how many versions the chain holds.</summary>
    public static JsonObject List(string root)
    {
        var o = new JsonObject();
        foreach (var (organism, node) in ReadRegistry(root)["organisms"]!.AsObject())
        {
            string? current = node!["current"]?.GetValue<string>();
            var versions = node["versions"]!.AsArray();
            var rec = versions.FirstOrDefault(v => v!["path"]!.GetValue<string>() == current);
            o[organism] = new JsonObject
            {
                ["current"] = current, ["version"] = rec?["version"]?.DeepClone(), ["mode"] = rec?["mode"]?.DeepClone(),
                ["n_spectra"] = rec?["n_spectra"]?.DeepClone(), ["versions"] = versions.Count,
            };
        }
        return o;
    }

    /// <summary>
    /// A "Name:" line mzLib cannot read back. Its reader takes any name matching
    /// <see cref="CrosslinkLibrarySpectrum.CrosslinkRegex"/> (<c>\(\d+\)</c>) for a crosslink, so a Unimod name holding a
    /// count, such as <c>NEM:2H(5)</c> or <c>Label:13C(6)</c>, is misread, and lookups of other spectra then land on the
    /// wrong entry: "Bad spectral library formatting or indexing" ended PXD039172's search on 2026-10-16. The batch runs
    /// no crosslink search, so every such name is one of these.
    /// </summary>
    public static bool UnreadableName(string line) =>
        line.StartsWith("Name:", StringComparison.Ordinal) && CrosslinkLibrarySpectrum.CrosslinkRegex.IsMatch(line);

    /// <summary>What a copy left out: unreadable entries, and <see cref="LibraryCleaning"/>'s two kinds by modification.</summary>
    public sealed record Dropped(int Unreadable, int BlockedCTerm, int DoubtfulClass, IReadOnlyDictionary<string, int> ByModification)
    {
        public static readonly Dropped None = new(0, 0, 0, new Dictionary<string, int>());
        public int Total => Unreadable + BlockedCTerm + DoubtfulClass;
    }

    /// <summary>
    /// Copies <paramref name="src"/> to <paramref name="dest"/> without its unreadable entries and the ones
    /// <paramref name="cleaning"/> judges (each entry from its "Name:" line to the next), keeping the file's line ending;
    /// a byte-for-byte copy when nothing is dropped.
    /// </summary>
    public static Dropped CopyClean(string src, string dest, LibraryCleaning? cleaning = null)
    {
        var verdicts = cleaning?.Judge(File.ReadLines(src).Select(LibraryCleaning.FullSequenceOf).OfType<string>())
                       ?? new Dictionary<string, (string Reason, string Modification)>();
        if (verdicts.Count == 0 && !File.ReadLines(src).Any(UnreadableName))
        {
            File.Copy(src, dest, overwrite: false);
            return Dropped.None;
        }
        int unreadable = 0, blocked = 0, doubtful = 0;
        var byMod = new SortedDictionary<string, int>(StringComparer.Ordinal);
        bool skipping = false;
        using var w = new StreamWriter(new FileStream(dest, FileMode.CreateNew), new UTF8Encoding(false)) { NewLine = NewLineOf(src) };
        foreach (string line in File.ReadLines(src))
        {
            if (line.StartsWith("Name:", StringComparison.Ordinal))
            {
                skipping = true;
                if (UnreadableName(line)) unreadable++;
                else if (verdicts.TryGetValue(LibraryCleaning.FullSequenceOf(line)!, out var v))
                {
                    if (v.Reason == "blocked_cterm") blocked++; else doubtful++;
                    byMod[v.Modification] = byMod.GetValueOrDefault(v.Modification) + 1;
                }
                else skipping = false;
            }
            if (!skipping) w.WriteLine(line);
        }
        return new Dropped(unreadable, blocked, doubtful, byMod);
    }

    private static string NewLineOf(string path)
    {
        using var s = File.OpenRead(path);
        var buf = new byte[64 * 1024];
        int n = s.Read(buf, 0, buf.Length);
        int i = Array.IndexOf(buf, (byte)'\n', 0, n);
        return i > 0 && buf[i - 1] == (byte)'\r' ? "\r\n" : "\n";
    }

    private static (int Version, string Rel, string Dest) NextVersion(string root, string organism, JsonObject entry)
    {
        int version = entry["versions"]!.AsArray().Select(v => v!["version"]!.GetValue<int>()).DefaultIfEmpty(0).Max() + 1;
        string rel = $"{organism}/{organism}.v{version:000}.msp";
        string dest = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        return (version, rel, dest);
    }

    private static JsonObject VersionRecord(int version, string rel, string mode, int? parent, string dest, Dropped dropped,
        LibraryCleaning? cleaning, JsonObject producedBy)
    {
        var rec = new JsonObject
        {
            ["version"] = version, ["path"] = rel, ["mode"] = mode, ["parent_version"] = parent,
            ["sha256"] = Sha(dest), ["n_spectra"] = CountSpectra(dest), ["bytes"] = new FileInfo(dest).Length,
            ["written_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'"),
        };
        // Only when something was dropped: every earlier version's record keeps its shape.
        if (dropped.Unreadable > 0) rec["dropped_unreadable"] = dropped.Unreadable;
        if (cleaning is not null)
        {
            var c = cleaning.Describe();
            c["dropped_blocked_cterm"] = dropped.BlockedCTerm;
            c["dropped_doubtful_class"] = dropped.DoubtfulClass;
            var by = new JsonObject();
            foreach (var (mod, n) in dropped.ByModification) by[mod] = n;
            c["dropped_by_modification"] = by;
            rec["cleaning"] = c;
        }
        rec["produced_by"] = producedBy;
        return rec;
    }

    /// <summary>
    /// Points <c>current</c> at an earlier version. Versions are never removed, so searching again appends a NEW version
    /// whose parent is the one rolled back to: the chain records the decision.
    /// </summary>
    public static JsonObject Rollback(string root, string organism, int version)
    {
        var reg = ReadRegistry(root);
        var entry = reg["organisms"]![organism]?.AsObject() ?? throw new UsageException($"no libraries registered for '{organism}'");
        var rec = entry["versions"]!.AsArray().FirstOrDefault(v => v!["version"]!.GetValue<int>() == version)?.AsObject()
                  ?? throw new UsageException($"{organism} has no version {version}");
        string path = rec["path"]!.GetValue<string>();
        if (!File.Exists(Path.Combine(root, path))) throw new UsageException($"version {version} is registered as {path} but that file is missing");
        entry["current"] = path;
        WriteRegistry(root, reg);
        return rec;
    }

    /// <summary>"Name:" lines, one per library spectrum: makes a library that shrank visible in the registry.</summary>
    public static int CountSpectra(string msp) => File.ReadLines(msp).Count(l => l.StartsWith("Name:", StringComparison.Ordinal));

    private static int? VersionOf(JsonObject entry, string path) =>
        entry["versions"]!.AsArray().FirstOrDefault(v => v!["path"]!.GetValue<string>() == path)?["version"]?.GetValue<int>();

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
