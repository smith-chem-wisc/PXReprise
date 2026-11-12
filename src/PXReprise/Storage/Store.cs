using System.Security.Cryptography;

namespace PXReprise.Storage;

// Ported from dataRepo's DataRepo.Storage (branch feat/storage-push-pull, 2026-11-11). dataRepo folds into PXReprise
// (Alex and the user, 2026-11-11), so this copy is the one that stays: at the merge, dataRepo's push/pull move onto it.

/// <summary>One object in a store: its key (a '/'-separated path below the store's root) and its size.</summary>
public sealed record StoreEntry(string Key, long Size);

/// <summary>What <see cref="IStore.WriteOnceAsync"/> did.</summary>
public enum WriteOutcome
{
    /// <summary>The object did not exist and now does.</summary>
    Written,
    /// <summary>An object with the same content was already there; nothing was written.</summary>
    AlreadyPresent,
}

/// <summary>A write-once object exists with different content. Nothing in a store is ever replaced, so this is a
/// refusal: the caller decides what losing means (for a library version, another machine claimed the number first).</summary>
public sealed class StoreConflictException(string message) : InvalidOperationException(message);

/// <summary>A local folder or an S3 prefix, behind the same few operations. Every object is written once and never
/// replaced or deleted. Keys are relative, '/'-separated and checked by <see cref="StoreKey.Check"/>.</summary>
public interface IStore : IDisposable
{
    /// <summary>The store's root as a person would type it: a folder path, or <c>s3://bucket/prefix/</c>.</summary>
    string Location { get; }

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Every object whose key starts with <paramref name="prefix"/>, in no promised order. Half-written local
    /// files are never listed.</summary>
    IAsyncEnumerable<StoreEntry> ListAsync(string prefix, CancellationToken ct = default);

    /// <summary>Copies the object to <paramref name="localPath"/>, which must not exist.</summary>
    Task DownloadAsync(string key, string localPath, CancellationToken ct = default);

    /// <summary>Writes <paramref name="localPath"/> as <paramref name="key"/> unless the key exists. If it exists with the
    /// same sha256 the write is <see cref="WriteOutcome.AlreadyPresent"/>; with another, a
    /// <see cref="StoreConflictException"/>.</summary>
    /// <param name="sha256">The file's sha256, lower-case hex, computed by the caller from the bytes it means to write.</param>
    Task<WriteOutcome> WriteOnceAsync(string key, string localPath, string sha256, CancellationToken ct = default);
}

/// <summary>Opens a store from what a person typed.</summary>
public static class Stores
{
    /// <summary><c>s3://bucket[/prefix]</c> opens S3 (credentials from the default chain, e.g. <c>AWS_PROFILE</c>);
    /// anything else is a local folder.</summary>
    public static IStore Open(string location) =>
        location.StartsWith("s3://", StringComparison.OrdinalIgnoreCase) ? S3Store.Open(location) : new LocalStore(location);

    /// <summary>A file's sha256, lower-case hex.</summary>
    public static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}

/// <summary>Store keys: relative, '/'-separated, no empty, '.' or '..' segment, no backslash or colon.</summary>
public static class StoreKey
{
    public static string Check(string key)
    {
        if (string.IsNullOrEmpty(key) || key.StartsWith('/') || key.EndsWith('/') || key.Contains('\\') || key.Contains(':')
            || key.Split('/').Any(s => s is "" or "." or ".."))
            throw new ArgumentException($"not a store key: '{key}' (relative, '/'-separated, no '.', '..' or empty segment)", nameof(key));
        return key;
    }

    /// <summary>A prefix may be empty or end in '/'; its segments follow <see cref="Check"/>.</summary>
    public static string CheckPrefix(string prefix)
    {
        if (prefix.Length == 0) return prefix;
        Check(prefix.TrimEnd('/'));
        return prefix;
    }
}
