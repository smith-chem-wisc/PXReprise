// PXRworker supervisor (2026-10-19; the user: "it would good if that restart was autonomous"). A scheduled task runs it
// every 5 minutes on the probe machine, as the user that holds the share's cmdkey credential. It starts the probe batch
// when the batch should run and does not, and never when a person stopped it:
//   - a live driver (driver.pid, checked against driver.last.json's start time): nothing to do, except to remember the
//     release it runs when that is the current one (a person started it by hand, as the README says for a release that
//     changes MetaMorpheus, .NET or the database set), so a later reboot restarts it instead of asking again;
//   - STOP in the state folder, control\STOP on the share, or a heartbeat saying "stopped: STOP", "stopped: disk floor"
//     or "not started: ..." (other than one that says to start again): left to a person;
//   - "stopped: question changed": started again on the same build;
//   - a new release (current.toml's generation is not the one last started): its build is copied from the share's
//     software folder and checked against its SHA256SUMS, then started; a release that also changes MetaMorpheus, .NET
//     or the database set is left to a person, since the machine file names those;
//   - anything else with no live driver (a reboot, a crash): started again;
//   - three starts within an hour: left to a person.
// Each action is logged to supervisor.log beside the machine file and to the share's status\supervisor.log; a note that
// leaves something to a person is logged once, not every 5 minutes.
//   supervisor.exe --share \\host\share --machine <machine.toml> --state-dir <state_dir> --software <local software folder>
#:property OutputType=WinExe
#:property PublishAot=false
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

string Arg(string name) => Opt(name) ?? throw new ArgumentException($"{name} is required");
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

string share = Arg("--share"), machine = Path.GetFullPath(Arg("--machine")), stateDir = Path.GetFullPath(Arg("--state-dir"));
string software = Path.GetFullPath(Arg("--software")), runDate = Opt("--run-date") ?? "2026-09-21";
string home = Path.GetDirectoryName(machine)!;
string localLog = Path.Combine(home, "supervisor.log"), recordFile = Path.Combine(home, "supervisor.json");
string shareLog = Path.Combine(share, "status", "supervisor.log");
string question = Path.Combine(share, "control", "question", "question.toml");

JsonObject record;
try { record = File.Exists(recordFile) ? JsonNode.Parse(File.ReadAllText(recordFile))!.AsObject() : new JsonObject(); }
catch (JsonException) { record = new JsonObject(); }

void Save() => File.WriteAllText(recordFile, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

void Say(string m)
{
    string line = $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}  {Environment.MachineName}  {m}";
    File.AppendAllText(localLog, line + Environment.NewLine);
    try { File.AppendAllText(shareLog, line + Environment.NewLine); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
}

// A note that leaves something to a person is said once, until it changes.
int Leave(string why)
{
    if (record["note"]?.GetValue<string>() != why) { Say($"left to a person: {why}"); record["note"] = why; Save(); }
    return 0;
}

bool DriverAlive()
{
    string pidFile = Path.Combine(stateDir, "driver.pid");
    if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) return false;
    try
    {
        using var p = Process.GetProcessById(pid);
        if (p.HasExited) return false;
        // A reused pid is not our driver: compare with the start time the driver recorded (to the second).
        string last = Path.Combine(stateDir, "driver.last.json");
        if (File.Exists(last) && JsonNode.Parse(File.ReadAllText(last))?["start_utc"]?.GetValue<string>() is { } s
            && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var started))
            return Math.Abs((p.StartTime.ToUniversalTime() - started).TotalSeconds) < 2;
        return true;
    }
    catch (ArgumentException) { return false; }
}

// The engine version the live driver recorded when it started (driver.last.json "engine"), or null.
string? RunningEngine()
{
    try
    {
        string last = Path.Combine(stateDir, "driver.last.json");
        return File.Exists(last) ? JsonNode.Parse(File.ReadAllText(last))?["engine"]?.GetValue<string>() : null;
    }
    catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return null; }
}

// control\current.toml: plain `key = value` lines.
Dictionary<string, string> Current()
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (string line in File.ReadAllLines(Path.Combine(share, "control", "current.toml")))
        if (Regex.Match(line, "^\\s*([a-z_]+)\\s*=\\s*\"?([^\"#]*?)\"?\\s*$") is { Success: true } m) d[m.Groups[1].Value] = m.Groups[2].Value.Trim();
    return d;
}

(int Rc, string Out) Run(string exe, params string[] a)
{
    var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string x in a) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    var err = p.StandardError.ReadToEndAsync();
    string o = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, (o + " " + err.Result).Replace('\r', ' ').Replace('\n', ' ').Trim());
}

try
{
    if (DriverAlive())
    {
        var now = Current();
        // First run beside a batch started by hand: that batch runs the current release, so remember it as the one in use.
        // Later, a batch running the current release's own build that is not the one recorded was started by a person (the
        // README's switch for a release that changes MetaMorpheus, .NET or the database set): remember it too. Without this,
        // generation 8 (started by hand on 2026-11-04) was still recorded as generation 6's MetaMorpheus 1.1.12, and after a
        // reboot the supervisor left the start to a person although the machine file already named 1.1.13.
        bool first = record["generation"] is null;
        bool byHand = !first && RunningEngine() is { } engine && engine == now.GetValueOrDefault("pxreprise")
                      && record["generation"]?.GetValue<string>() != now.GetValueOrDefault("generation");
        if (first || byHand)
        {
            record["generation"] = now["generation"];
            // One node per key: a JsonNode has one parent, so `a = b = value` fails ("The node already has a parent").
            record["build"] = now["software"];
            record["checked"] = now["software"];
            foreach (string key in new[] { "metamorpheus", "dotnet", "database_set" }) record[key] = now.GetValueOrDefault(key);
            record.Remove("note");
            Save();
            Say(first
                ? $"supervisor installed beside the running batch (release generation {now["generation"]}, {now["software"]})"
                : $"recorded the batch started by hand on release generation {now["generation"]} ({now["software"]}, {now.GetValueOrDefault("metamorpheus")}, database set {now.GetValueOrDefault("database_set")})");
        }
        return 0;
    }
    if (File.Exists(Path.Combine(stateDir, "STOP"))) return Leave("STOP is in the state folder");
    if (File.Exists(Path.Combine(share, "control", "STOP"))) return Leave("control\\STOP is on the share");
    string state = "";
    try { state = JsonNode.Parse(File.ReadAllText(Path.Combine(share, "status", "status.json")))?["state"]?.GetValue<string>() ?? ""; }
    catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    if (state.StartsWith("stopped: STOP", StringComparison.Ordinal) || state.StartsWith("stopped: disk floor", StringComparison.Ordinal)
        || (state.StartsWith("not started", StringComparison.Ordinal) && !state.Contains("start again", StringComparison.Ordinal)))
        return Leave($"the batch says \"{state}\"");

    var cur = Current();
    string build = cur["software"], gen = cur["generation"];
    // A release that changes what the machine file names needs a person: the machine file is not ours to edit.
    foreach (string key in new[] { "metamorpheus", "dotnet", "database_set" })
        if (record[key]?.GetValue<string>() is { } was && cur.GetValueOrDefault(key) is { } now && was != now)
            return Leave($"release generation {gen} changes {key} from {was} to {now}: copy it and update the machine file (README)");

    string exe = Path.Combine(software, build, "pxreprise.exe");
    if (!File.Exists(exe) || record["checked"]?.GetValue<string>() != build)
    {
        Say($"release generation {gen}: copying {build} from the share");
        var (rc, _) = Run("robocopy", Path.Combine(share, "software", build), Path.Combine(software, build), "/E", "/R:3", "/W:10", "/NP", "/NJH", "/NJS");
        if (rc >= 8) return Leave($"robocopy of {build} failed (exit {rc})");
        var (crc, cout) = Run(exe, "worker", "check", Path.Combine(software, build));
        if (crc != 0) return Leave($"{build} does not match its SHA256SUMS: {cout}");
        record["checked"] = build;
        Save();
    }

    // Three starts within an hour is a loop, not a restart.
    var starts = (record["starts"]?.AsArray().Select(x => DateTime.Parse(x!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)).ToList() ?? new())
        .Where(t => DateTime.UtcNow - t < TimeSpan.FromHours(1)).ToList();
    if (starts.Count >= 3) return Leave($"started {starts.Count} times in the last hour: look at batch.log");

    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
    foreach (string x in new[] { "batch", "run", question, "--machine", machine, "--run-date", runDate }) psi.ArgumentList.Add(x);
    using (var p = Process.Start(psi)!)
        Say($"started the probe batch (pid {p.Id}, {build}, release generation {gen}); it had stopped: \"{(state.Length > 0 ? state : "no heartbeat")}\"");
    starts.Add(DateTime.UtcNow);
    record["starts"] = new JsonArray(starts.Select(t => (JsonNode?)t.ToString("o")).ToArray());
    record["generation"] = gen;
    record["build"] = build;
    foreach (string key in new[] { "metamorpheus", "dotnet", "database_set" }) record[key] = cur.GetValueOrDefault(key);
    record.Remove("note");
    Save();
    return 0;
}
catch (Exception e)
{
    Say($"supervisor failed: {e.GetType().Name}: {e.Message}");
    return 1;
}
