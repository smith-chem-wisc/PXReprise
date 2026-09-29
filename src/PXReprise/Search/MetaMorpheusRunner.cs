using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PXReprise.Search;

public sealed record TaskMark(string Task, string Event, double Seconds);

/// <param name="TimedOut">Killed by the batch: the wall-clock ceiling, or a stall (<paramref name="Stalled"/>).</param>
/// <param name="Stalled">Killed because MetaMorpheus used no CPU for the stall window: hung, not slow.</param>
public sealed record RunResult(int ExitCode, bool TimedOut, double WallSeconds, double CpuSeconds, double PeakRssGb,
    IReadOnlyList<TaskMark> Marks, bool Stalled = false);

/// <summary>
/// Launches MetaMorpheus's CMD as a separate process, one per dataset: TaskLayer is not packaged, its events are
/// static and GlobalVariables is process-global, so an in-process host could not isolate two datasets or survive a
/// crash (oracle 2026-09-27). <c>CMD.dll</c> is framework-dependent: <c>dotnet CMD.dll</c> is the same program as
/// <c>CMD.exe</c>, on any OS.
/// </summary>
public sealed class MetaMorpheusRunner(string cmd, string dotnet)
{
    public IReadOnlyList<string> Launch { get; } =
        cmd.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new[] { dotnet, cmd } : new[] { cmd };

    /// <summary>
    /// Writes the pinned release's default task files into <paramref name="dir"/> (<c>CMD -g</c>) and returns the
    /// release from its banner ("Welcome to MetaMorpheus" then "1.1.11"). <c>--version</c> prints the help text
    /// instead, so the banner is the only reliable source.
    /// </summary>
    public (string Release, IReadOnlyList<string> Command) GenerateDefaults(string dir)
    {
        var argv = Launch.Concat(new[] { "-g", "-o", dir }).ToList();
        string stdout = Capture(argv, out int rc);
        if (rc != 0) throw new SearchSetupException($"MetaMorpheus -g exited {rc}");
        var lines = stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string release = lines.Count > 1 && Regex.IsMatch(lines[1], @"^\d+(\.\d+)+$") ? lines[1] : "unknown";
        return (release, argv);
    }

    /// <summary>The build's commit, from the help text's "CMD 1.0.0+&lt;40-hex sha&gt;".</summary>
    public string Commit()
    {
        string help = Capture(Launch.Concat(new[] { "--help" }).ToList(), out _);
        return Regex.Match(help, @"CMD \S+\+([0-9a-f]{40})") is { Success: true } m ? m.Groups[1].Value : "unknown";
    }

    /// <summary>
    /// Runs one invocation, stamping every output line with elapsed seconds so each task gets its own wall time. The
    /// deadline is real: on timeout the whole process tree is killed (with <c>dotnet CMD.dll</c>, CMD is a child).
    /// stdout and stderr both go to the log. Never pipe the output through something that can stop reading early: a
    /// closed pipe kills MetaMorpheus mid-run.
    ///
    /// <paramref name="stall"/> (zero: off) kills a search whose CPU time has not grown for that long. MetaMorpheus
    /// 1.1.11's PEP step writes nothing for hours on a busy box while 31 of 32 threads wait on Chronologer's lock
    /// (2026-09-29, PXD069093), so silence is not a hang; a process that uses no CPU at all is.
    /// </summary>
    public async Task<RunResult> RunAsync(IReadOnlyList<string> args, string logPath, TimeSpan timeout, CancellationToken ct,
        TimeSpan stall = default)
    {
        var psi = new ProcessStartInfo(Launch[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in Launch.Skip(1).Concat(args)) psi.ArgumentList.Add(a);

        var marks = new List<TaskMark>();
        var sw = Stopwatch.StartNew();
        var gate = new object();
        using var log = new StreamWriter(logPath);
        using var proc = new Process { StartInfo = psi };
        void OnLine(string? line)
        {
            if (line is null) return;
            double t = Math.Round(sw.Elapsed.TotalSeconds, 1);
            lock (gate)
            {
                log.WriteLine($"{t:0.0}\t{line}");
                var m = Regex.Match(line, @"^\s*(Starting|Finished) task: (\S+)");
                if (m.Success) marks.Add(new TaskMark(m.Groups[2].Value, m.Groups[1].Value == "Starting" ? "start" : "end", t));
            }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Start();
        proc.StandardInput.Close();   // no console prompt may ever wait for a person
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool timedOut = false, stalled = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        double peakRss = 0;
        using var sampler = new CancellationTokenSource();
        var every = stall > TimeSpan.Zero && stall / 4 < TimeSpan.FromSeconds(5) ? stall / 4 : TimeSpan.FromSeconds(5);
        var sampling = Task.Run(async () =>
        {
            double cpuAtProgress = 0;
            var progressAt = sw.Elapsed;
            while (!sampler.IsCancellationRequested)
            {
                try
                {
                    proc.Refresh();
                    peakRss = Math.Max(peakRss, proc.PeakWorkingSet64 / 1e9);
                    // Progress is a full CPU-second of work: an idle .NET process still spends milliseconds on timers.
                    double cpu = proc.TotalProcessorTime.TotalSeconds;
                    if (cpu - cpuAtProgress >= 1.0) { cpuAtProgress = cpu; progressAt = sw.Elapsed; }
                }
                catch (InvalidOperationException) { }
                if (stall > TimeSpan.Zero && sw.Elapsed - progressAt > stall)
                {
                    stalled = true;
                    deadline.Cancel();
                    return;
                }
                try { await Task.Delay(every, sampler.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        });
        try
        {
            await proc.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (!timedOut) throw;
        }
        proc.WaitForExit();   // flushes the async readers
        sampler.Cancel();
        await sampling.ConfigureAwait(false);
        double cpu = 0;
        try { cpu = proc.TotalProcessorTime.TotalSeconds; } catch (InvalidOperationException) { }
        lock (gate) log.Flush();
        return new RunResult(timedOut ? -1 : proc.ExitCode, timedOut, sw.Elapsed.TotalSeconds, cpu, Math.Round(peakRss, 2), marks, timedOut && stalled);
    }

    private static string Capture(IReadOnlyList<string> argv, out int exitCode)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string a in argv.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new SearchSetupException($"could not start {argv[0]}");
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = err.Result;
        exitCode = p.ExitCode;
        return output;
    }
}
