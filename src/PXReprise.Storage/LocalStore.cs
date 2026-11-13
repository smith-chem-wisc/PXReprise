using System.Runtime.CompilerServices;

namespace PXReprise.Storage;

/// <summary>A store that is a folder on this machine (or a share).</summary>
/// <remarks>A write goes to a hidden partial file beside its target, is hashed, and is then renamed without overwrite,
/// so a reader sees the whole file or none, and two writers of the same key cannot both win. Partial files left by a
/// crash are never listed.</remarks>
public sealed class LocalStore : IStore
{
    internal const string PartialMarker = ".partial-";

    private readonly string _root;

    public LocalStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public string Location => _root.Replace('\\', '/');

    private string PathOf(string key) => Path.Combine(_root, StoreKey.Check(key).Replace('/', Path.DirectorySeparatorChar));

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathOf(key)));

    public async IAsyncEnumerable<StoreEntry> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        StoreKey.CheckPrefix(prefix);
        var slash = prefix.LastIndexOf('/');
        var start = slash < 0 ? _root : Path.Combine(_root, prefix[..slash].Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(start)) yield break;
        foreach (var file in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).Contains(PartialMarker, StringComparison.Ordinal)) continue;
            var key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (key.StartsWith(prefix, StringComparison.Ordinal)) yield return new StoreEntry(key, new FileInfo(file).Length);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task DownloadAsync(string key, string localPath, CancellationToken ct = default)
    {
        await using var source = File.OpenRead(PathOf(key));
        await using var target = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    public async Task<WriteOutcome> WriteOnceAsync(string key, string localPath, string sha256, CancellationToken ct = default)
    {
        var target = PathOf(key);
        if (File.Exists(target)) return Compare(key, target, sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + PartialMarker + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using (var source = File.OpenRead(localPath))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write))
                await source.CopyToAsync(output, ct).ConfigureAwait(false);
            var written = Stores.Sha256File(partial);
            if (written != sha256)
                throw new IOException($"{localPath}: sha256 {written}, expected {sha256}; not written to {key}");
            try
            {
                File.Move(partial, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                return Compare(key, target, sha256);   // another writer won the race
            }
            return WriteOutcome.Written;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private WriteOutcome Compare(string key, string target, string sha256)
    {
        var existing = Stores.Sha256File(target);
        if (existing == sha256) return WriteOutcome.AlreadyPresent;
        throw new StoreConflictException($"{Location}: {key} already exists with sha256 {existing}, not {sha256}; nothing in a store is replaced");
    }

    public string? LocalFile(string key) => PathOf(key);

    public async Task ReplaceAsync(string key, byte[] content, CancellationToken ct = default)
    {
        var target = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + PartialMarker + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllBytesAsync(partial, content, ct).ConfigureAwait(false);
        File.Move(partial, target, overwrite: true);
    }

    public void Dispose() { }
}
