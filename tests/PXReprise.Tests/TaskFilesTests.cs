using System.Text;
using System.Text.Json;
using PXReprise.Search;

namespace PXReprise.Tests;

public class TaskFilesTests
{
    private static readonly IReadOnlySet<(string, string)> Known = new HashSet<(string, string)>
    {
        ("Common Biological", "Acetylation on K"),
        ("Trypsin Digested", "GG (Ubiquitination Site) on K"),
    };

    private const string Search = """
        [SearchParameters]
        MatchBetweenRuns = false
        SearchType = "Classic"
        WriteSpectralLibrary = false
        UpdateSpectralLibrary = false
        [CommonParameters]
        MaxThreadsToUsePerFile = 63
        ProductMassTolerance = "±20.0000 PPM"
        ProductMassTolerance_LowRes = "±0.3500 Absolute"
        """;

    [Test]
    public void OnlyTheNamedSettingsChange()
    {
        var (text, _) = TaskFiles.Edit(Search, TaskKind.Search,
            new TaskSettings(32, true, Array.Empty<string>(), SpectralLibraryMode: "update", ProductMassTolerance: "±0.5000 Absolute"), Known);
        Assert.That(text, Is.EqualTo(Search
            .Replace("MatchBetweenRuns = false", "MatchBetweenRuns = true")
            .Replace("UpdateSpectralLibrary = false", "UpdateSpectralLibrary = true")
            .Replace("MaxThreadsToUsePerFile = 63", "MaxThreadsToUsePerFile = 32")
            .Replace("ProductMassTolerance = \"±20.0000 PPM\"", "ProductMassTolerance = \"±0.5000 Absolute\"")));
        Assert.That(text, Does.Contain("ProductMassTolerance_LowRes = \"±0.3500 Absolute\""), "the LowRes setting is never touched");
    }

    [Test]
    public void CrlfFilesAreEditedAndKeepTheirLineEndings()
    {
        string crlf = Search.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var (text, _) = TaskFiles.Edit(crlf, TaskKind.Search, new TaskSettings(32, true, Array.Empty<string>()), Known);
        Assert.That(text, Does.Contain("MatchBetweenRuns = true\r\n").And.Contain("MaxThreadsToUsePerFile = 32\r\n"));
        Assert.That(text.Replace("\r\n", ""), Does.Not.Contain("\n"), "no bare LF introduced");
    }

    [Test]
    public void AGptmdModIsAppendedOnceAndAnUnknownOneIsRefused()
    {
        const string g = "ListOfModsGptmd = \"Common Biological\\tAcetylation on K\\t\\t\"\nMaxThreadsToUsePerFile = 8";
        var mods = new[] { "Trypsin Digested\tGG (Ubiquitination Site) on K" };
        var (once, added) = TaskFiles.Edit(g, TaskKind.Gptmd, new TaskSettings(8, false, mods), Known);
        var (twice, again) = TaskFiles.Edit(once, TaskKind.Gptmd, new TaskSettings(8, false, mods), Known);
        Assert.That(once, Does.Contain(@"Common Biological\tAcetylation on K\t\tTrypsin Digested\tGG (Ubiquitination Site) on K"""));
        Assert.That(added, Has.Count.EqualTo(1));
        Assert.That(twice, Is.EqualTo(once));
        Assert.That(again, Is.Empty);
        Assert.Throws<SearchSetupException>(() =>
            TaskFiles.Edit(g, TaskKind.Gptmd, new TaskSettings(8, false, new[] { "Made Up\tNothing on Q" }), Known));
    }

    [Test]
    public void ASettingThePinnedEngineLacksIsRefusedNotSkipped()
    {
        var e = Assert.Throws<SearchSetupException>(() => TaskFiles.Edit("MaxThreadsToUsePerFile = 1", TaskKind.Search,
            new TaskSettings(1, false, Array.Empty<string>(), SearchType: "Modern"), Known));
        Assert.That(e!.Message, Does.Contain("SearchType"));
    }

    /// <summary>
    /// The equivalence proof against the aging pipeline: for every dataset its batch has searched, the saved default
    /// task files (<c>CMD -g</c> output) edited by this code must equal, byte for byte, the files that search actually
    /// ran with. Needs the aging work disk; skipped elsewhere.
    /// </summary>
    [Test, Category("LocalCorpus")]
    public void EveryAgingSearchsTaskFilesAreReproducedByteForByte()
    {
        const string root = @"F:\aging_data";
        if (!Directory.Exists(root)) Assert.Ignore("the aging work disk is not attached");
        var known = TaskFiles.KnownMods(Path.Combine(root, "mm_settings", "1.1.11", "Mods"));

        int datasets = 0, files = 0;
        var mismatches = new List<string>();
        foreach (string tasks in Directory.EnumerateDirectories(root, "tasks", SearchOption.AllDirectories)
                     .Where(d => Path.GetFileName(Path.GetDirectoryName(d)) == "04_search").OrderBy(d => d))
        {
            string provFile = Path.Combine(Path.GetDirectoryName(tasks)!, "provenance.json");
            if (!File.Exists(provFile)) continue;
            var prov = JsonDocument.Parse(File.ReadAllText(provFile)).RootElement;
            if (!prov.TryGetProperty("params", out var p)) continue;
            if (Str(prov, "tools", "MetaMorpheus", "release") is { } rel && rel != "1.1.11") continue;

            string? mode = prov.TryGetProperty("spectral_library", out var lib) && lib.ValueKind == JsonValueKind.Object
                ? Str(lib, "mode") : null;
            var settings = new TaskSettings(
                p.GetProperty("max_threads").GetInt32(),
                p.GetProperty("match_between_runs").GetBoolean(),
                p.TryGetProperty("gptmd_extra_mods", out var gm) ? gm.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>(),
                Str(p, "search_type"), mode, Str(p, "product_mass_tolerance"), Str(p, "precursor_mass_tolerance"));

            var kinds = p.GetProperty("tasks").EnumerateArray().Select(t => Enum.Parse<TaskKind>(t.GetString()!)).ToList();
            datasets++;
            for (int i = 0; i < kinds.Count; i++)
            {
                string def = Path.Combine(tasks, TaskFiles.FileName(kinds[i]));
                string ran = Path.Combine(tasks, $"{i + 1}_{TaskFiles.FileName(kinds[i])}");
                if (!File.Exists(def) || !File.Exists(ran)) continue;
                files++;
                try
                {
                    var (text, _) = TaskFiles.Edit(Read(def), kinds[i], settings, known);
                    if (!Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(File.ReadAllBytes(ran)))
                        mismatches.Add(ran);
                }
                catch (SearchSetupException e)
                {
                    mismatches.Add($"{ran}: {e.Message} (release {Str(prov, "tools", "MetaMorpheus", "release") ?? "?"})");
                }
            }
        }
        TestContext.Out.WriteLine($"{datasets} searches, {files} task files compared, {mismatches.Count} differ");
        Assert.That(files, Is.GreaterThan(0));
        Assert.That(mismatches, Is.Empty);
    }

    /// <summary>Exactly as written: no BOM stripping surprises, no newline translation.</summary>
    private static string Read(string path) => new UTF8Encoding(false).GetString(File.ReadAllBytes(path));

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (string k in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }
}
