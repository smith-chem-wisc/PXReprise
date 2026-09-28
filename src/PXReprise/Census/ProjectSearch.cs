using PXReprise.Cli;
using UsefulProteomicsDatabases;

namespace PXReprise.Census;

/// <summary>The one PRIDE call the census makes, behind an interface so tests run offline.</summary>
public interface IProjectSearch
{
    Task<List<PrideProjectSearchResult>> SearchAsync(string keyword, CancellationToken ct);
}

/// <summary>
/// mzLib's <see cref="PrideArchiveClient"/> with the engine's retry policy. mzLib never retries by design; the caller
/// owns the policy, keyed on unavailability only (408/429/5xx, timeouts, transport). A contract break
/// (<c>MzLibException</c>) is never retried: retrying a wrong answer only repeats it.
/// </summary>
public sealed class PrideProjectSearch(PrideArchiveClient client, int attempts = 3, TimeSpan? backoff = null) : IProjectSearch
{
    private readonly TimeSpan _backoff = backoff ?? TimeSpan.FromSeconds(10);

    public async Task<List<PrideProjectSearchResult>> SearchAsync(string keyword, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await client.SearchProjectsAsync(keyword, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < attempts && Envelope.IsUnavailable(e) && !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"pride search '{keyword}' attempt {attempt} unavailable ({e.Message}); retrying");
                await Task.Delay(_backoff * attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
