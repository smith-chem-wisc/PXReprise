using PXReprise.Census;
using PXReprise.Config;
using PXReprise.Fetch;
using UsefulProteomicsDatabases;

namespace PXReprise.Batch;

/// <summary>
/// v0.4.43 (D33): PRIDE is asked about the deposit, whichever split search the batch runs. The runner keys everything by
/// search key (<c>PXD027754-mouse</c>); these two put the accession back at the one door to PRIDE, so no call site can
/// forget it. A whole deposit's key is its accession, which passes unchanged.
/// </summary>
internal sealed class DepositFiles(IPrideFiles inner) : IPrideFiles
{
    public Task<List<PrideArchiveFile>> ListFilesAsync(string key, CancellationToken ct) => inner.ListFilesAsync(SearchKey.Accession(key), ct);

    public Task<List<string>> ListFtpNamesAsync(string key, CancellationToken ct) => inner.ListFtpNamesAsync(SearchKey.Accession(key), ct);

    public Task<string> DownloadAsync(PrideArchiveFile file, string dir, CancellationToken ct) => inner.DownloadAsync(file, dir, ct);

    // Forwarded explicitly: the interface's default would download and check afterwards, not use the inner checked download.
    public Task<string> DownloadCheckedAsync(PrideArchiveFile file, string dir, string md5, long sizeBytes, CancellationToken ct) =>
        inner.DownloadCheckedAsync(file, dir, md5, sizeBytes, ct);
}

/// <inheritdoc cref="DepositFiles"/>
internal sealed class DepositSearch(IProjectSearch inner) : IProjectSearch
{
    public Task<List<PrideProjectSearchResult>> SearchAsync(string keyword, CancellationToken ct) => inner.SearchAsync(SearchKey.Accession(keyword), ct);

    public Task<PrideProject?> TryGetProjectAsync(string key, CancellationToken ct) => inner.TryGetProjectAsync(SearchKey.Accession(key), ct);
}
