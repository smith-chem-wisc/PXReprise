using System.Diagnostics;
using System.Globalization;
using DataRepo.Bundle;

namespace DataRepo.Catalog;

/// <summary>One staged catalog: its path and the bytes it and its write-ahead log hold.</summary>
public sealed record StagedCatalog(string Path, long Bytes);

/// <summary>The staged catalogs builds leave beside their output, and which of them nobody is writing any more (aging
/// DATAREPO-83 a).</summary>
/// <remarks>
/// <para>A build writes the new catalog as <c>.&lt;name&gt;.&lt;catalog id&gt;.building</c> and moves it into place at
/// the end. A build that refuses deletes it, but a killed one cannot: aging's 3600 s publish limit left three of 20 to
/// 34 GB in one day. So a build records its owner beside the staged file (<c>.owner</c>: process id and start time), and
/// every build first removes the staged files whose owner is no longer running.</para>
/// <para>A file with no owner record was staged by datarepo 1.3.2 or earlier. On Windows it is removed when nothing
/// holds it open, which a live DuckDB writer always does. Elsewhere an open file can be neither seen nor refused, so it
/// is left in place and named.</para>
/// </remarks>
internal static class StagedCatalogs
{
    public const string OwnerSuffix = ".owner";

    /// <summary>The staged path of catalog <paramref name="catalogId"/> built at <paramref name="output"/>.</summary>
    public static string PathFor(string output, string catalogId) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, $".{System.IO.Path.GetFileName(output)}.{catalogId}.building");

    /// <summary>Records this process as the builder of <paramref name="staging"/>.</summary>
    public static void Claim(string staging)
    {
        using var me = Process.GetCurrentProcess();
        File.WriteAllText(staging + OwnerSuffix,
            $"{me.Id} {me.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}\n");
    }

    /// <summary>Removes the owner record of <paramref name="staging"/>; a missing one is not an error.</summary>
    public static void Release(string staging) => TryDelete(staging + OwnerSuffix);

    /// <summary>Removes each staged catalog of <paramref name="output"/> whose builder is gone.</summary>
    /// <returns>The staged catalogs removed, and those left in place because nothing says whether a build is still
    /// writing them. A staged catalog whose builder is running is in neither.</returns>
    /// <exception cref="CatalogException">A running build is writing <paramref name="ownStaging"/>: two builds of one
    /// catalog would write one file.</exception>
    public static (List<StagedCatalog> Removed, List<StagedCatalog> Kept) Sweep(string output, string ownStaging)
    {
        var removed = new List<StagedCatalog>();
        var kept = new List<StagedCatalog>();
        var dir = System.IO.Path.GetDirectoryName(output)!;
        if (!Directory.Exists(dir)) return (removed, kept);
        var prefix = $".{System.IO.Path.GetFileName(output)}.";
        var staged = Directory.EnumerateFiles(dir, prefix + "*")
            .Select(StagedOf).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var path in staged)
        {
            var (state, pid) = Builder(path);
            var entry = new StagedCatalog(path, Size(path) + Size(path + ".wal"));
            if (state == State.Running)
            {
                if (string.Equals(path, ownStaging, StringComparison.Ordinal))
                    throw new CatalogException(
                        $"another build of this catalog is running ({(pid is null ? "a process holds it open" : $"pid {pid}")}) "
                        + $"and writing {path}. Wait for it to finish, or stop it, then build again.");
                continue;
            }
            if (state == State.Unknown)
            {
                kept.Add(entry);
                continue;
            }
            if (TryDelete(path) && TryDelete(path + ".wal") && TryDelete(path + OwnerSuffix)) removed.Add(entry);
            else kept.Add(entry);
        }
        return (removed, kept);
    }

    private enum State { Running, Gone, Unknown }

    /// <summary>The staged path a file in the output's folder belongs to: the file itself, its WAL or its owner record.</summary>
    private static string? StagedOf(string file)
    {
        foreach (var suffix in new[] { ".building", ".building.wal", ".building" + OwnerSuffix })
            if (file.EndsWith(suffix, StringComparison.Ordinal))
                return file[..^suffix.Length] + ".building";
        return null;
    }

    /// <summary>Whether the build that staged <paramref name="path"/> is still running.</summary>
    private static (State State, int? Pid) Builder(string path)
    {
        var owner = path + OwnerSuffix;
        if (File.Exists(owner))
        {
            var parts = File.ReadAllText(owner).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return (Alive(pid, ticks), pid);
            return (State.Unknown, null);
        }
        if (!File.Exists(path)) return (State.Gone, null); // a WAL left alone
        if (!OperatingSystem.IsWindows()) return (State.Unknown, null);
        try
        {
            // Windows refuses an exclusive open while any other handle is open, and a DuckDB writer holds one.
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return (State.Gone, null);
        }
        catch (IOException)
        {
            return (State.Running, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (State.Unknown, null);
        }
    }

    /// <summary>Whether process <paramref name="pid"/> is the one that started at <paramref name="startTicks"/>. A reused
    /// pid has another start time; a process we may not inspect is taken as still running.</summary>
    private static State Alive(int pid, long startTicks)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return State.Gone;
        }
        using (process)
        {
            try
            {
                if (process.HasExited) return State.Gone;
                var started = process.StartTime.ToUniversalTime().Ticks;
                return Math.Abs(started - startTicks) < TimeSpan.TicksPerSecond ? State.Running : State.Gone;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return State.Running;
            }
        }
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
