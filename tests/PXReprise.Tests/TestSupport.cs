using System.Net.Http;
using System.Net.Sockets;
using PXReprise.Census;
using UsefulProteomicsDatabases;

namespace PXReprise.Tests;

internal static class TestSupport
{
    /// <summary>The shipped profiles, copied beside the test assembly by the test project.</summary>
    public static string ProfilesDir => Path.Combine(TestContext.CurrentContext.TestDirectory, "profiles");

    public static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "pxreprise-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static string WriteFile(string dir, string name, string text)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    public static PrideProjectSearchResult Record(string accession, string title, string description = "",
        string[]? instruments = null, string[]? files = null, string[]? organisms = null, string protocol = "")
        => new()
        {
            Accession = accession,
            Title = title,
            ProjectDescription = description,
            SampleProcessingProtocol = protocol,
            Instruments = (instruments ?? new[] { "Q Exactive HF" }).ToList(),
            ProjectFileNames = (files ?? new[] { "a.raw", "b.raw" }).ToList(),
            Organisms = (organisms ?? new[] { "Homo sapiens (human)" }).ToList(),
            SubmissionType = "COMPLETE",
        };

    public const string MinimalQuestion = """
        question = "t2d"
        profiles = ["label-free-dda@1", "tmt-dda@1"]
        [discover]
        keywords = ["type 2 diabetes", "insulin resistance"]
        organisms = ["Homo sapiens (human)"]
        [relevance]
        require_any = ["type (2|ii) diabet", "insulin resistan"]
        exclude_if_any = ["type (1|i) diabet"]
        unless_any = ["type (2|ii) diabet"]
        """;
}

/// <summary>An offline PRIDE search: canned results per keyword, or an exception.</summary>
internal sealed class FakeSearch : IProjectSearch
{
    public Dictionary<string, List<PrideProjectSearchResult>> Results { get; } = new();
    public Dictionary<string, Exception> Failures { get; } = new();
    public List<string> Calls { get; } = new();

    public Task<List<PrideProjectSearchResult>> SearchAsync(string keyword, CancellationToken ct)
    {
        Calls.Add(keyword);
        if (Failures.TryGetValue(keyword, out var e)) throw e;
        return Task.FromResult(Results.TryGetValue(keyword, out var r) ? r : new List<PrideProjectSearchResult>());
    }
}

/// <summary>
/// Live tests skip when the service is unavailable and fail on anything else, as mzLib's ExternalServiceTestHelper
/// does: an outage is not our bug, but a contract break is.
/// </summary>
internal static class ExternalServiceTestHelper
{
    public static async Task RunAsync(string service, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or SocketException)
        {
            Assert.Ignore($"{service} unavailable: {e.Message}");
        }
    }
}
