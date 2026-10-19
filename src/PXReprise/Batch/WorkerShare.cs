using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PXReprise.Cli;
using PXReprise.Config;

namespace PXReprise.Batch;

/// <summary>
/// The folder a controlling machine shares with a probe worker (v0.4.26, design/PLAN_worker.md; the user, 2026-10-19: "it
/// would only run probes. and the full searches would only be run on this machine using those that passed the probe").
/// The controller writes <c>control\</c> and <c>accessions\</c>; the worker writes <c>deposits\</c> and <c>status\</c>.
/// A package is a probed deposit's folder with <c>READY.json</c> written last, naming every file with its size and
/// SHA-256, so the controller takes nothing half-copied or changed in transit.
/// </summary>
public sealed class WorkerShare(string root)
{
    public string Root { get; } = root;
    public string Control => Path.Combine(Root, "control");
    public string AssignmentsFile => Path.Combine(Root, "accessions", "assigned.tsv");
    public string Deposits => Path.Combine(Root, "deposits");
    public string HeartbeatFile => Path.Combine(Root, "status", "status.json");
    public string CurrentFile => Path.Combine(Control, "current.toml");
    public string LimitsFile => Path.Combine(Control, "deposit_limits.toml");
    public string ReleasesFile => Path.Combine(Control, "releases.tsv");
    /// <summary>While it exists the worker starts no new probe (STOP drains, as on the controller).</summary>
    public string StopFile => Path.Combine(Control, "STOP");
    public string Software => Path.Combine(Root, "software");
    public string DatabaseSets => Path.Combine(Root, "db");
    /// <summary>The question the controller's driver loaded, with the files it reads, for the worker to run.</summary>
    public string QuestionDir => Path.Combine(Control, "question");
    public string QuestionFile => Path.Combine(QuestionDir, "question.toml");

    public const string ReadyName = "READY.json";
    public const string StateEntryName = "state_entry.json";
    public const string DecisionsName = "decisions.tsv";
    public const string PackageSchema = "pxreprise probe package 1";

    private static readonly Regex Accession = new(@"^PXD\d+$", RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ assignments

    /// <summary>One deposit the controller asks the worker to probe. <see cref="Attempt"/> rises when a package is refused.</summary>
    public sealed record Assignment(string Accession, string Organism, int Attempt, string AssignedUtc, string Title);

    private const string AssignmentsHeader = "accession\torganism\tattempt\tassigned_utc\ttitle";

    /// <summary>Replaces the assignments whole (written beside, then renamed), and only when they changed.</summary>
    public bool WriteAssignments(IReadOnlyList<Assignment> rows)
    {
        var text = new StringBuilder(AssignmentsHeader).Append('\n');
        foreach (var a in rows)
            text.Append(string.Join('\t', a.Accession, a.Organism, a.Attempt.ToString(CultureInfo.InvariantCulture), a.AssignedUtc, Clean(a.Title))).Append('\n');
        return WriteIfChanged(AssignmentsFile, text.ToString());
    }

    /// <summary>The assignments in the controller's order; none when the file does not exist. Columns are read by name.</summary>
    public List<Assignment> ReadAssignments()
    {
        if (!File.Exists(AssignmentsFile)) return new List<Assignment>();
        var lines = File.ReadAllText(AssignmentsFile).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new List<Assignment>();
        var col = lines[0].Split('\t').Select((n, i) => (n.Trim(), i)).ToDictionary(x => x.Item1, x => x.i, StringComparer.Ordinal);
        foreach (string need in new[] { "accession", "organism", "attempt", "assigned_utc" })
            if (!col.ContainsKey(need)) throw new InvalidDataException($"{AssignmentsFile} has no '{need}' column (header: {lines[0]})");
        var rows = new List<Assignment>();
        foreach (string line in lines.Skip(1))
        {
            var c = line.Split('\t');
            string Get(string name) => col.TryGetValue(name, out int i) && i < c.Length ? c[i].Trim() : "";
            if (!Accession.IsMatch(Get("accession"))) continue;
            rows.Add(new Assignment(Get("accession"), Get("organism"),
                int.TryParse(Get("attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1, Get("assigned_utc"), Get("title")));
        }
        return rows;
    }

    // ------------------------------------------------------------------ heartbeat

    /// <summary>The worker's heartbeat: <paramref name="status"/> with the time it was written.</summary>
    public void WriteHeartbeat(JsonObject status)
    {
        status["utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        WriteReplacing(HeartbeatFile, status.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }) + "\n");
    }

    /// <summary>When the worker last wrote its heartbeat and what it said it was doing; nulls when it never has (or the file
    /// does not read).</summary>
    public (DateTime? Utc, string? State) Heartbeat()
    {
        try
        {
            if (!File.Exists(HeartbeatFile)) return (null, null);
            var o = JsonNode.Parse(File.ReadAllText(HeartbeatFile));
            DateTime? utc = o?["utc"]?.GetValue<string>() is { } s
                            && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t : null;
            return (utc, o?["state"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return (null, null); }
    }

    public DateTime? HeartbeatUtc() => Heartbeat().Utc;

    /// <summary>No heartbeat, none for longer than <paramref name="after"/>, or one saying the worker has stopped or never
    /// started (a fresh "stopped" must not keep the controller waiting for hours).</summary>
    public bool Quiet(TimeSpan after, DateTime nowUtc)
    {
        var (t, state) = Heartbeat();
        return t is not { } u || nowUtc - u > after
               || state is not null && (state.StartsWith("stopped", StringComparison.Ordinal) || state.StartsWith("not started", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ what to run

    /// <summary>
    /// <c>control\current.toml</c>: what the worker runs. <see cref="Generation"/> rises with every change; the worker stops
    /// between deposits when it does, so a change is acted on at once and never half-way through a probe.
    /// </summary>
    public sealed record Current(int Generation, string Pxreprise, string? Software, string? MetaMorpheus, string? Dotnet, string? DatabaseSet);

    /// <summary>The current release, or null when there is none yet.</summary>
    public Current? ReadCurrent()
    {
        if (!File.Exists(CurrentFile)) return null;
        var t = TomlSection.Load(CurrentFile);
        var c = new Current(checked((int)t.RequiredInteger("generation")), t.RequiredString("pxreprise"), t.OptionalString("software"),
            t.OptionalString("metamorpheus"), t.OptionalString("dotnet"), t.OptionalString("database_set"));
        t.RefuseUnknownKeys();
        return c;
    }

    /// <summary>The identity of the database set <paramref name="c"/> names: its SHA256SUMS's own hash, or null when it names none.</summary>
    public string? DatabaseSetIdentity(Current c) =>
        c.DatabaseSet is { } set ? Sha256Sums.Identity(Path.Combine(DatabaseSets, set, Sha256Sums.FileName)) : null;

    /// <summary>
    /// Publishes a build to the worker: copies <paramref name="build"/> into <c>software\pxreprise-&lt;commit&gt;</c> with
    /// its SHA256SUMS (unless that folder exists), then writes <c>current.toml</c> with the next generation and appends the
    /// change to <c>releases.tsv</c>. A value not given keeps the current release's.
    /// </summary>
    public Current Release(string build, string version, string? metamorpheus, string? dotnet, string? databaseSet, string reason)
    {
        if (!File.Exists(Path.Combine(build, "pxreprise.dll")) && !File.Exists(Path.Combine(build, "pxreprise.exe")))
            throw new UsageException($"{build} holds no pxreprise build (pxreprise.dll or pxreprise.exe)");
        var was = ReadCurrent();
        string folder = "pxreprise-" + Regex.Replace(version.Split('+') is { Length: > 1 } p ? p[1] : version, @"[^A-Za-z0-9._-]", "_");
        string dest = Path.Combine(Software, folder);
        if (!Directory.Exists(dest))
        {
            string partial = dest + ".copying";
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            CopyTree(build, partial);
            Sha256Sums.Write(partial);
            Directory.Move(partial, dest);
        }
        foreach (var (name, value) in new[] { ("metamorpheus", metamorpheus ?? was?.MetaMorpheus), ("dotnet", dotnet ?? was?.Dotnet) })
            if (value is not null && !Directory.Exists(Path.Combine(Software, value)))
                throw new UsageException($"{name} = {value}: there is no {Path.Combine(Software, value)}");
        string? set = databaseSet ?? was?.DatabaseSet;
        if (set is not null && !File.Exists(Path.Combine(DatabaseSets, set, Sha256Sums.FileName)))
            throw new UsageException($"database_set = {set}: {Path.Combine(DatabaseSets, set, Sha256Sums.FileName)} does not exist (run `pxreprise worker sums` on the set first)");
        var c = new Current((was?.Generation ?? 0) + 1, version, folder, metamorpheus ?? was?.MetaMorpheus, dotnet ?? was?.Dotnet, set);
        var toml = new StringBuilder();
        toml.Append($"# What the probe worker runs (pxreprise worker release, {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}, {Environment.MachineName}): {Clean(reason)}\n");
        toml.Append("# The worker stops between deposits when the generation changes, and its session switches it over (control\\README.md).\n");
        toml.Append($"generation = {c.Generation}\npxreprise = \"{c.Pxreprise}\"\nsoftware = \"{c.Software}\"\n");
        if (c.MetaMorpheus is not null) toml.Append($"metamorpheus = \"{c.MetaMorpheus}\"\n");
        if (c.Dotnet is not null) toml.Append($"dotnet = \"{c.Dotnet}\"\n");
        if (c.DatabaseSet is not null) toml.Append($"database_set = \"{c.DatabaseSet}\"\n");
        WriteReplacing(CurrentFile, toml.ToString());
        if (!File.Exists(ReleasesFile)) File.WriteAllText(ReleasesFile, "utc\tgeneration\tpxreprise\tsoftware\tmetamorpheus\tdotnet\tdatabase_set\treason\n");
        File.AppendAllText(ReleasesFile, string.Join('\t', DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), c.Generation,
            c.Pxreprise, c.Software, c.MetaMorpheus ?? "", c.Dotnet ?? "", c.DatabaseSet ?? "", Clean(reason)) + "\n");
        return c;
    }

    // ------------------------------------------------------------------ the question

    /// <summary>
    /// Publishes the question the controller's driver loaded: <paramref name="questionFile"/> and the files and folders it
    /// reads (decisions, designs), each at its own path relative to the question, with a SHA256SUMS. Built beside, then put
    /// in place whole. Returns the copy's identity and the paths that could not go (outside the question's folder: the
    /// worker cannot see them).
    /// </summary>
    public (string Identity, List<string> Left) PublishQuestion(string questionFile, IEnumerable<string> files, IEnumerable<string> dirs)
    {
        string home = Path.GetDirectoryName(Path.GetFullPath(questionFile))!;
        string staging = QuestionDir + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        File.Copy(questionFile, Path.Combine(staging, "question.toml"));
        var left = new List<string>();
        string? Inside(string p)
        {
            string rel = Path.GetRelativePath(home, Path.GetFullPath(p));
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) { left.Add(p); return null; }
            return rel;
        }
        foreach (string f in files.Where(File.Exists))
            if (Inside(f) is { } rel)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, rel))!);
                File.Copy(f, Path.Combine(staging, rel), overwrite: true);
            }
        foreach (string d in dirs.Where(Directory.Exists))
            if (Inside(d) is { } rel) CopyTree(d, Path.Combine(staging, rel));
        Sha256Sums.Write(staging);
        if (Directory.Exists(QuestionDir)) Directory.Delete(QuestionDir, recursive: true);
        Directory.Move(staging, QuestionDir);
        return (QuestionIdentity()!, left);
    }

    /// <summary>The published question's identity (its SHA256SUMS's hash), or null when none is published.</summary>
    public string? QuestionIdentity() => Sha256Sums.Identity(Path.Combine(QuestionDir, Sha256Sums.FileName));

    // ------------------------------------------------------------------ the controller's deposit limits

    /// <summary>
    /// The controller's <c>[deposit_limits]</c> and <c>probe_search_minutes</c> as TOML, for the worker's probes to gate by:
    /// the full search those gates are for runs on the controller. No time stamp, so an unchanged text is not rewritten.
    /// </summary>
    public static string LimitsText(DepositLimits? limits, double? probeMinutes, string machine)
    {
        var s = new StringBuilder($"# Written by the controlling machine's batch ({machine}): the gates its full searches use. The probe worker applies them.\n");
        if (probeMinutes is { } pm) s.Append("probe_search_minutes = ").Append(pm.ToString(CultureInfo.InvariantCulture)).Append('\n');
        s.Append("[deposit_limits]\n");
        if (limits?.MaxFiles is { } mf) s.Append($"max_files = {mf}\n");
        if (limits?.MaxFileMb is { } mm) s.Append($"max_file_mb = {mm}\n");
        if (limits?.MaxEstimatedMs2 is { } me) s.Append($"max_est_ms2 = {me}\n");
        if (limits?.DeferIfMedianFileMb is { } dm) s.Append($"defer_if_median_file_mb = {dm}\n");
        return s.ToString();
    }

    public bool WriteLimits(string text) => WriteIfChanged(LimitsFile, text);

    /// <summary>The controller's limits, or none when it has written none.</summary>
    public (DepositLimits? Limits, double? ProbeMinutes) ReadLimits()
    {
        if (!File.Exists(LimitsFile)) return (null, null);
        var t = TomlSection.Load(LimitsFile);
        var limits = DepositLimits.Load(t.OptionalTable("deposit_limits"));
        double? probe = t.OptionalNumber("probe_search_minutes");
        t.RefuseUnknownKeys();
        return (limits, probe);
    }

    // ------------------------------------------------------------------ packages

    /// <summary>A verified package: the worker's state entry for the deposit and its decision rows.</summary>
    public sealed record Package(string Dir, string Accession, JsonObject Ready, JsonObject Entry, IReadOnlyList<string> DecisionLines)
    {
        public string Outcome => Ready["outcome"]!.GetValue<string>();
        public string? Profile => Ready["profile"]?.GetValue<string>();
        public int Attempt => Ready["attempt"]?.GetValue<int>() ?? 1;
        public string? Pxreprise => Ready["pxreprise"]?.GetValue<string>();
    }

    /// <summary>
    /// Copies <paramref name="runDir"/> into <c>deposits\&lt;accession&gt;</c>, with the state entry and the deposit's
    /// decision rows beside it, then writes <c>READY.json</c> last (beside, then renamed). The probe's raw files travel
    /// only with a pass (<paramref name="withRaw"/>); partial downloads and files set aside never travel. An earlier,
    /// unfinished copy of the same deposit is replaced.
    /// </summary>
    public string WritePackage(string runDir, string accession, JsonObject entry, IReadOnlyList<string> decisionLines, JsonObject meta, bool withRaw)
    {
        if (!Accession.IsMatch(accession)) throw new ArgumentException($"not an accession: {accession}", nameof(accession));
        string dest = Path.Combine(Deposits, accession);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);
        var files = new JsonArray();
        foreach (string src in Directory.Exists(runDir)
                     ? Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList()
                     : new List<string>())
        {
            string rel = Path.GetRelativePath(runDir, src).Replace('\\', '/');
            if (rel.Contains(".partial", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("02_fetch/" + BatchRunner.NotSelectedFolder + "/", StringComparison.Ordinal))
                continue;
            if (rel.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) && !(withRaw && Path.GetDirectoryName(rel)!.Replace('\\', '/') == "02_fetch/spectra"))
                continue;
            files.Add(CopyListed(src, dest, rel));
        }
        File.WriteAllText(Path.Combine(dest, StateEntryName), entry.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }) + "\n");
        File.WriteAllText(Path.Combine(dest, DecisionsName), string.Concat(decisionLines.Select(l => l + "\n")));
        foreach (string name in new[] { StateEntryName, DecisionsName }) files.Add(Listed(Path.Combine(dest, name), name));
        meta["schema"] = PackageSchema;
        meta["accession"] = accession;
        meta["written_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        meta["files"] = files;
        WriteReplacing(Path.Combine(dest, ReadyName), meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }) + "\n");
        return dest;
    }

    /// <summary>The package folders with a READY.json, in name order. Set-aside folders (<c>PXD….rejected-…</c>) are not packages.</summary>
    public List<string> ReadyPackages() =>
        Directory.Exists(Deposits)
            ? Directory.EnumerateDirectories(Deposits).Where(d => Accession.IsMatch(Path.GetFileName(d)) && File.Exists(Path.Combine(d, ReadyName)))
                .OrderBy(d => d, StringComparer.Ordinal).ToList()
            : new List<string>();

    /// <summary>
    /// Checks a package against its READY.json: every listed file present with its size and SHA-256, nothing unlisted,
    /// the schema and accession right. The package, or the problems found.
    /// </summary>
    public static (Package? Package, List<string> Problems) Verify(string dir)
    {
        var problems = new List<string>();
        string acc = Path.GetFileName(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar));
        JsonObject ready;
        try { ready = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ReadyName)))!.AsObject(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or NullReferenceException)
        {
            return (null, new List<string> { $"{ReadyName} does not read: {ex.Message}" });
        }
        if (ready["schema"]?.GetValue<string>() != PackageSchema) problems.Add($"schema is '{ready["schema"]}', not '{PackageSchema}'");
        if (ready["accession"]?.GetValue<string>() != acc) problems.Add($"READY.json names {ready["accession"]}, but the folder is {acc}");
        if (ready["outcome"] is null) problems.Add("READY.json has no outcome");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in ready["files"]?.AsArray() ?? new JsonArray())
        {
            string rel = f!["path"]!.GetValue<string>();
            listed.Add(rel);
            string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) { problems.Add($"{rel}: not a path inside the package"); continue; }
            if (!File.Exists(p)) { problems.Add($"{rel}: missing"); continue; }
            long bytes = f["bytes"]!.GetValue<long>();
            if (new FileInfo(p).Length != bytes) { problems.Add($"{rel}: {new FileInfo(p).Length} bytes, READY.json says {bytes}"); continue; }
            if (Sha256(p) != f["sha256"]!.GetValue<string>()) problems.Add($"{rel}: SHA-256 differs from READY.json");
        }
        foreach (string p in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dir, p).Replace('\\', '/');
            if (rel != ReadyName && !listed.Contains(rel)) problems.Add($"{rel}: not listed in READY.json");
        }
        foreach (string need in new[] { StateEntryName, DecisionsName })
            if (!listed.Contains(need)) problems.Add($"{need}: not in the package");
        if (problems.Count > 0) return (null, problems);
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, StateEntryName)))!.AsObject();
        var decisions = File.ReadAllText(Path.Combine(dir, DecisionsName)).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return (new Package(dir, acc, ready, entry, decisions), problems);
    }

    // ------------------------------------------------------------------ files

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static JsonObject CopyListed(string src, string destRoot, string rel)
    {
        string to = Path.Combine(destRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        // The hash is of the source: the controller hashes what arrived, so a copy damaged in transit is caught there.
        var listed = Listed(src, rel);
        File.Copy(src, to, overwrite: true);
        return listed;
    }

    private static JsonObject Listed(string path, string rel) =>
        new() { ["path"] = rel, ["bytes"] = new FileInfo(path).Length, ["sha256"] = Sha256(path) };

    internal static void CopyTree(string from, string to)
    {
        foreach (string src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, src));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }
    }

    /// <summary>Moves a folder: a rename on one volume, else a copy and a delete.</summary>
    internal static void MoveTree(string from, string to)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }
        CopyTree(from, to);
        Directory.Delete(from, recursive: true);
    }

    private static bool WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        WriteReplacing(path, text);
        return true;
    }

    /// <summary>Writes beside, then renames over: a reader never sees half a file.</summary>
    private static void WriteReplacing(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>
/// <c>SHA256SUMS</c> (v0.4.26): one line per file, <c>&lt;sha256&gt;  &lt;relative/path&gt;</c>, as <c>sha256sum</c> writes
/// it. A software folder or database set the controller publishes carries one, and the worker checks its own copy
/// against it before it probes anything.
/// </summary>
public static class Sha256Sums
{
    public const string FileName = "SHA256SUMS";

    /// <summary>Writes <c>SHA256SUMS</c> for every file under <paramref name="dir"/> except itself. Returns the file count.</summary>
    public static int Write(string dir)
    {
        if (!Directory.Exists(dir)) throw new UsageException($"{dir} does not exist");
        var lines = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/'))
            .Where(rel => rel != FileName && rel != FileName + ".tmp")
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .Select(rel => $"{WorkerShare.Sha256(Path.Combine(dir, rel))}  {rel}")
            .ToList();
        string path = Path.Combine(dir, FileName), tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return lines.Count;
    }

    /// <summary>
    /// The files <paramref name="sumsFile"/> lists, checked in <paramref name="dir"/>: the problems, none when every
    /// listed file is there with its hash. Files the list does not name are allowed (a database folder holds others).
    /// </summary>
    public static (int Checked, List<string> Problems) Check(string dir, string? sumsFile = null)
    {
        sumsFile ??= Path.Combine(dir, FileName);
        if (!File.Exists(sumsFile)) throw new UsageException($"{sumsFile} does not exist");
        var problems = new List<string>();
        int n = 0;
        foreach (string line in File.ReadAllText(sumsFile).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int gap = line.IndexOf("  ", StringComparison.Ordinal);
            if (gap != 64) { problems.Add($"{Path.GetFileName(sumsFile)}: not a sum line: {line}"); continue; }
            string want = line[..64], rel = line[(gap + 2)..];
            string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            n++;
            if (!File.Exists(p)) problems.Add($"{rel}: missing");
            else if (WorkerShare.Sha256(p) != want) problems.Add($"{rel}: SHA-256 differs");
        }
        return (n, problems);
    }

    /// <summary>A set's identity: the SHA-256 of its SHA256SUMS with LF line ends, or null when there is none.</summary>
    public static string? Identity(string sumsFile) =>
        File.Exists(sumsFile)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(sumsFile).ReplaceLineEndings("\n")))).ToLowerInvariant()
            : null;
}
