namespace PXReprise.Search;

/// <summary>
/// v0.4.35 (the user, 2026-11-02: "perhaps it should be a standing rule to delete raw files as their calibration is
/// successful"): once MetaMorpheus has calibrated a raw file, GPTMD and the search read only its <c>-calib.mzML</c>, and
/// after the search the cleanup deletes the raw anyway. So a raw is deleted as soon as its calibration is finished,
/// while the search runs, which frees its space hours earlier. Finished means a non-empty <c>&lt;name&gt;-calib.mzML</c>
/// AND its <c>&lt;name&gt;-calib.toml</c>, which MetaMorpheus writes after the file. A file whose calibration failed has
/// no calibrated partner and is kept (the later tasks read the raw). The cost: a re-search fetches the raws again, as it
/// does after cleanup today; the probe is what should catch chemistry before the full download.
/// </summary>
public static class CalibratedRaws
{
    /// <summary>
    /// One pass: deletes each of <paramref name="raws"/> still on disk whose calibration under <paramref name="mmDir"/>
    /// (any <c>Task*CalibrationTask</c>) is finished, and returns the ones it deleted. A raw MetaMorpheus still holds
    /// open is left for the next pass.
    /// </summary>
    public static IReadOnlyList<string> Sweep(string mmDir, IEnumerable<string> raws)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(mmDir)) return deleted;
        var calDirs = Directory.EnumerateDirectories(mmDir, "Task*CalibrationTask").ToList();
        if (calDirs.Count == 0) return deleted;
        foreach (string raw in raws)
        {
            if (!File.Exists(raw)) continue;
            string name = Path.GetFileNameWithoutExtension(raw);
            if (!calDirs.Any(c => Finished(c, name))) continue;
            try
            {
                File.Delete(raw);
                deleted.Add(raw);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    private static bool Finished(string calDir, string name)
    {
        var mzml = new FileInfo(Path.Combine(calDir, name + "-calib.mzML"));
        return mzml.Exists && mzml.Length > 0 && File.Exists(Path.Combine(calDir, name + "-calib.toml"));
    }

    /// <summary>
    /// Sweeps every <paramref name="every"/> until <paramref name="ct"/> is cancelled, collecting what it deleted into
    /// <paramref name="deleted"/>. Never throws: a sweep that fails is tried again at the next interval.
    /// </summary>
    public static async Task WatchAsync(string mmDir, IReadOnlyList<string> raws, List<string> deleted, TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(every, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                var gone = Sweep(mmDir, raws);
                lock (deleted) deleted.AddRange(gone);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
