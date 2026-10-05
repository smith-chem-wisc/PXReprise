// G18: starts the batch again after a reboot. A Windows scheduled task runs this at logon. It starts the last driver's
// own command line (driver.last.json, written by every driver since v0.3.8), so a pin swap needs no change here.
//
// It does nothing when:
// - STOP is present: someone asked the batch to hold, and `batch run` would delete STOP;
// - a driver is already running;
// - driver.last.json or its exe is missing.
// It never kills anything. Every step is appended to the log named by --log.
//
//   dotnet run BatchAutostart.cs -- --state-dir F:\aging_data\batch --log F:\aging_batch\autostart\autostart.log [--check]
//   --check: prints what it would do and touches nothing.
#:property OutputType=WinExe
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Diagnostics;
using System.Text.Json.Nodes;

string? Opt(string name) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault();
string stateDir = Opt("state-dir") ?? @"F:\aging_data\batch";
string myLog = Opt("log") ?? Path.Combine(AppContext.BaseDirectory, "autostart.log");
bool check = args.Contains("--check");

void Say(string m)
{
    string line = $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}  {m}";
    Console.WriteLine(line);
    if (check) return;
    try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(myLog))!); File.AppendAllText(myLog, line + Environment.NewLine); }
    catch (IOException) { }
}

DateTime boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
Say($"autostart (pid {Environment.ProcessId}{(check ? ", --check" : "")}): machine up since {boot:yyyy-MM-dd'T'HH:mm:ss'Z'}, state dir {stateDir}");

// A drive can mount after logon; wait up to 15 minutes for the state directory.
for (int i = 0; !Directory.Exists(stateDir); i++)
{
    if (i >= 15) { Say($"ABORT: {stateDir} did not appear in 15 min"); return 1; }
    Thread.Sleep(TimeSpan.FromMinutes(1));
}

string stop = Path.Combine(stateDir, "STOP"), pidFile = Path.Combine(stateDir, "driver.pid");
string recFile = Path.Combine(stateDir, "driver.last.json"), log = Path.Combine(stateDir, "batch.log");
if (File.Exists(stop))
{
    Say($"not starting: STOP is present ('{File.ReadAllText(stop).Trim()}'), so someone asked the batch to hold. Delete STOP and start the batch by hand, or run this again.");
    return 0;
}
if (!File.Exists(recFile)) { Say($"ABORT: no {recFile}; start the batch by hand once (a v0.3.8+ driver writes it)"); return 1; }
var rec = JsonNode.Parse(File.ReadAllText(recFile))!;
string exe = rec["exe"]?.GetValue<string>() ?? "";
var argv = rec["args"]?.AsArray().Select(a => a!.GetValue<string>()).ToList();
string cwd = rec["cwd"]?.GetValue<string>() ?? Path.GetDirectoryName(exe) ?? ".";
if (!File.Exists(exe) || argv is null || argv.Count == 0) { Say($"ABORT: {recFile} names no usable command (exe '{exe}', {argv?.Count ?? 0} args)"); return 1; }

// Is a driver running? The pid must be alive AND be the driver: after a reboot its number may belong to another program.
if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
{
    string verdict;
    try
    {
        using var p = Process.GetProcessById(pid);
        bool sameStart = rec["pid"]?.GetValue<int>() == pid && DateTime.TryParse(rec["start_utc"]?.GetValue<string>(), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var s) && Math.Abs((p.StartTime.ToUniversalTime() - s.ToUniversalTime()).TotalSeconds) <= 2;
        bool looksLikeDriver = p.ProcessName.Equals(Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase);
        verdict = p.HasExited ? "exited" : sameStart || (looksLikeDriver && p.StartTime.ToUniversalTime() > boot) ? "alive" : $"reused by {p.ProcessName}";
    }
    catch (ArgumentException) { verdict = "gone"; }
    if (verdict == "alive") { Say($"not starting: driver pid {pid} is running"); return 0; }
    Say($"driver.pid names {pid}, which is {verdict}: stale (the previous driver did not exit cleanly)");
    if (!check) File.Delete(pidFile);
}

string line = $"\"{exe}\" {string.Join(" ", argv.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
if (check) { Say($"would start, hidden, in {cwd}: {line}"); return 0; }

long logStart = File.Exists(log) ? new FileInfo(log).Length : 0;
var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd };
foreach (string a in argv) psi.ArgumentList.Add(a);
int child;
using (var p = Process.Start(psi)!) child = p.Id;
Say($"started driver pid {child}: {line}");

// The driver logs "batch start: pid N" within seconds, after its claim; give it 3 minutes.
Thread.Sleep(TimeSpan.FromMinutes(3));
string fresh = "";
if (File.Exists(log))
    using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        fs.Seek(Math.Min(logStart, fs.Length), SeekOrigin.Begin);
        fresh = new StreamReader(fs).ReadToEnd();
    }
foreach (string l in fresh.Split('\n').Where(l => l.Contains("batch start:") || l.Contains("engine   pxreprise") || l.Contains("RECONCILE") || l.Contains("INTERRUPTED")).Take(12))
    Say("  | " + l.TrimEnd());
bool ok = fresh.Contains($"batch start: pid {child}");
bool alive;
try { using var p = Process.GetProcessById(child); alive = !p.HasExited; } catch (ArgumentException) { alive = false; }
Say(ok && alive ? $"OK: driver {child} started and is running." : $"WARNING: driver {child} {(alive ? "is running but logged no start line" : "has exited")}; look at batch.log.");
return ok && alive ? 0 : 1;
