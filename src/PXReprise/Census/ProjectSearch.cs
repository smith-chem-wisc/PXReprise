using PXReprise.Cli;
using UsefulProteomicsDatabases;

namespace PXReprise.Census;

/// <summary>The PRIDE calls the census makes, behind an interface so tests run offline.</summary>
public interface IProjectSearch
{
    Task<List<PrideProjectSearchResult>> SearchAsync(string keyword, CancellationToken ct);

    /// <summary>
    /// The project record (<c>/projects/{accession}</c>), or null when PRIDE has no such project. The search index's
    /// metadata fields can disagree with it (PXD043476, pride 001), so values the engine acts on come from here.
    /// </summary>
    Task<PrideProject?> TryGetProjectAsync(string accession, CancellationToken ct);
}

/// <summary>
/// mzLib's <see cref="PrideArchiveClient"/> with the engine's retry policy. mzLib never retries by design; the caller
/// owns the policy, keyed on unavailability only (408/429/5xx, timeouts, transport). A contract break
/// (<c>MzLibException</c>) is never retried: retrying a wrong answer only repeats it.
/// </summary>
public sealed class PrideProjectSearch(PrideArchiveClient client, int attempts = 3, TimeSpan? backoff = null) : IProjectSearch
{
    private readonly TimeSpan _backoff = backoff ?? TimeSpan.FromSeconds(10);

    public Task<List<PrideProjectSearchResult>> SearchAsync(string keyword, CancellationToken ct) =>
        RetryAsync($"pride search '{keyword}'", () => client.SearchProjectsAsync(keyword, cancellationToken: ct), ct);

    public async Task<PrideProject?> TryGetProjectAsync(string accession, CancellationToken ct)
    {
        var (found, project) = await RetryAsync($"pride project {accession}", () => client.TryGetProjectAsync(accession, ct), ct)
            .ConfigureAwait(false);
        return found ? project : null;
    }

    private async Task<T> RetryAsync<T>(string what, Func<Task<T>> call, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < attempts && Envelope.IsUnavailable(e) && !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"{what} attempt {attempt} unavailable ({e.Message}); retrying");
                await Task.Delay(_backoff * attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
