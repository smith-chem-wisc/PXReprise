namespace DataRepo.Bundle;

/// <summary>The core schema version this build writes, hashes and accepts.</summary>
/// <remarks>
/// <para>It is <see cref="Tables.SchemaVersion"/>, except inside a <see cref="Python0320"/> scope, which pins the
/// schema Python 0.32.0 wrote (0.0.13). That scope exists for parity alone: the C# release writes schema 0.0.14,
/// whose tables are 0.0.13's with one enum value added (<c>OccupancyState.covered_zero</c>), so a parity ingest
/// (<c>IngestRules.Python0320</c>) writes rows valid under both, and a Python-written bundle, catalog or study
/// layer can still be rebuilt and compared byte for byte.</para>
/// <para>The scope is an <see cref="AsyncLocal{T}"/>: it covers the code that opened it and whatever that code
/// awaits or starts, never a concurrent caller.</para>
/// </remarks>
public static class SchemaContract
{
    /// <summary>The core schema version Python 0.32.0 wrote.</summary>
    public const string Python0320SchemaVersion = "0.0.13";

    private static readonly AsyncLocal<string?> Pinned = new();

    /// <summary>The schema version in force here.</summary>
    public static string Version => Pinned.Value ?? Tables.SchemaVersion;

    /// <summary>True inside a <see cref="Python0320"/> scope: what is built must be what Python 0.32.0 built, so
    /// anything 0.32.0 did not have (the catalog's go engine tables, catalog version 9) is left out.</summary>
    public static bool IsPython0320 => Pinned.Value == Python0320SchemaVersion;

    /// <summary>Pins Python 0.32.0's schema version until the result is disposed.</summary>
    public static IDisposable Python0320()
    {
        var previous = Pinned.Value;
        Pinned.Value = Python0320SchemaVersion;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Pinned.Value = previous;
    }
}
