namespace DataRepo.Storage;

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
/// refusal, never something to retry.</summary>
public sealed class StoreConflictException(string message) : DataRepo.Bundle.DataRepoException(message);

/// <summary>
/// Where bundles live: a local folder or an S3 prefix, behind the same few operations.
/// </summary>
/// <remarks>
/// Every object a reader may hold is written once and never replaced (dataRepo's rule since D1). The one exception is a
/// pointer such as <c>latest.json</c>, which <see cref="ReplaceAsync"/> swaps whole. A store never deletes.
/// Keys are relative, '/'-separated and checked by <see cref="StoreKey.Check"/>, so no key can leave the store's root.
/// </remarks>
public interface IStore : IDisposable
{
    /// <summary>The store's root as a person would type it: a folder path, or <c>s3://bucket/prefix/</c>.</summary>
    string Location { get; }

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Every object whose key starts with <paramref name="prefix"/> (string prefix, as S3 lists), in no
    /// promised order. Half-written local files are never listed.</summary>
    IAsyncEnumerable<StoreEntry> ListAsync(string prefix, CancellationToken ct = default);

    /// <summary>The object's file on this machine, when the store is a local folder; null otherwise.</summary>
    string? LocalFile(string key);

    /// <summary>Copies the object to <paramref name="localPath"/>, which must not exist.</summary>
    Task DownloadAsync(string key, string localPath, CancellationToken ct = default);

    /// <summary>Writes <paramref name="localPath"/> as <paramref name="key"/> unless the key exists. If it exists with the
    /// same sha256 the write is <see cref="WriteOutcome.AlreadyPresent"/>; with another, a
    /// <see cref="StoreConflictException"/>.</summary>
    /// <param name="sha256">The file's sha256, lower-case hex, computed by the caller from the bytes it means to write.</param>
    Task<WriteOutcome> WriteOnceAsync(string key, string localPath, string sha256, CancellationToken ct = default);

    /// <summary>Replaces a small pointer object whole. Only for pointers (<c>latest.json</c>), never for data.</summary>
    Task ReplaceAsync(string key, byte[] content, CancellationToken ct = default);
}

/// <summary>Opens a store from what a person typed.</summary>
public static class Stores
{
    /// <summary><c>s3://bucket[/prefix]</c> opens S3 (credentials from the default chain, e.g. <c>AWS_PROFILE</c>);
    /// anything else is a local folder.</summary>
    public static IStore Open(string location) =>
        location.StartsWith("s3://", StringComparison.OrdinalIgnoreCase) ? S3Store.Open(location) : new LocalStore(location);
}

/// <summary>Store keys: relative, '/'-separated, no empty, '.' or '..' segment, no backslash.</summary>
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
