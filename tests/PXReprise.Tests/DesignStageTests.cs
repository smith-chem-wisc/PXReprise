using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>G15: ExperimentalDesign.tsv from the question's curated SDRF first, then the deposit's.</summary>
public class DesignStageTests
{
    private const string Header = "source name\tcharacteristics[organism]\tcharacteristics[biological replicate]\tassay name\tcomment[label]\tcomment[data file]\tcomment[technical replicate]\tcomment[fraction identifier]";

    /// <summary>An SDRF over a.raw..d.raw: two ages, two biological replicates each, unless told otherwise.</summary>
    private static string Sdrf(string dir, string name, string? factor = "factor value[age]", Func<int, string>? biorep = null)
    {
        var lines = new List<string> { Header + (factor is null ? "" : $"\t{factor}") };
        string[] files = { "a.raw", "b.raw", "c.raw", "d.raw" };
        for (int i = 0; i < files.Length; i++)
            lines.Add($"s{i}\tMus musculus\t{biorep?.Invoke(i) ?? ((i % 2) + 1).ToString()}\trun {i}\tlabel free sample\t{files[i]}\t1\t1"
                      + (factor is null ? "" : $"\t{(i < 2 ? "3 months" : "24 months")}"));
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllLines(p, lines);
        return p;
    }

    private static (string Spectra, string Metadata, List<string> Files) Run()
    {
        string root = TestSupport.TempDir();
        string spectra = Path.Combine(root, "02_fetch", "spectra"), metadata = Path.Combine(root, "02_fetch", "metadata");
        Directory.CreateDirectory(spectra);
        var files = new[] { "a.raw", "b.raw", "c.raw", "d.raw" }.Select(f => Path.Combine(spectra, f)).ToList();
        foreach (string f in files) File.WriteAllText(f, "");
        return (spectra, metadata, files);
    }

    [Test]
    public void ACuratedDesignIsWrittenAheadOfTheDeposits()
    {
        var (spectra, metadata, files) = Run();
        Sdrf(metadata, "PXD000001.sdrf.tsv", factor: null);   // the deposit's: no factor, as 7 of 23 in the aging corpus
        string curated = Sdrf(TestSupport.TempDir(), "PXD000001.sdrf.tsv");
        var rec = DesignStage.Prepare(spectra, files, curated, null);
        Assert.That(rec["source"]!.GetValue<string>(), Is.EqualTo("question"));
        Assert.That(rec["conditions"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(new[] { "24 months", "3 months" }));
        var tsv = File.ReadAllLines(Path.Combine(spectra, DesignStage.FileName));
        Assert.That(tsv[0], Is.EqualTo("FileName\tCondition\tBiorep\tFraction\tTechrep"));
        Assert.That(tsv, Has.Length.EqualTo(5));
    }

    [Test]
    public void ARefusedCuratedDesignStopsTheSearchRatherThanBeingSkipped()
    {
        var (spectra, _, files) = Run();
        File.WriteAllText(Path.Combine(spectra, DesignStage.FileName), "stale");
        string curated = Sdrf(TestSupport.TempDir(), "x.sdrf.tsv", biorep: _ => "1");   // four samples, one replicate
        var e = Assert.Throws<SearchSetupException>(() => DesignStage.Prepare(spectra, files, curated, null));
        Assert.That(e!.Message, Does.Contain("refused"));
        Assert.That(File.Exists(Path.Combine(spectra, DesignStage.FileName)), Is.False, "a stale design is never left behind");
    }

    // v0.4.17: MetaMorpheus reads a design beside the first spectra file on every run. Ours is removed before a search that
    // brings none; a TMT design PXReprise did not write (no marker, v0.4.34) was put there by hand and stops the search instead.
    [Test]
    public void AStaleDesignIsRemovedAndAHandPlacedTmtDesignRefused()
    {
        var (spectra, _, files) = Run();
        Assert.That(DesignStage.RemoveStale(spectra), Is.False);
        File.WriteAllText(Path.Combine(spectra, DesignStage.FileName), "stale");
        Assert.That(DesignStage.RemoveStale(spectra), Is.True);
        Assert.That(File.Exists(Path.Combine(spectra, DesignStage.FileName)), Is.False);
        File.WriteAllText(Path.Combine(spectra, DesignStage.TmtFileName), "by hand");
        Assert.That(Assert.Throws<SearchSetupException>(() => DesignStage.RemoveStale(spectra))!.Message, Does.Contain(DesignStage.TmtFileName));
        Assert.Throws<SearchSetupException>(() => DesignStage.Prepare(spectra, files, null, null, "mouse", "PXD000001"));
        Assert.That(File.Exists(Path.Combine(spectra, DesignStage.TmtFileName)), Is.True, "never removed");
    }

    private static readonly string[] Tmt10Labels = { "TMT126", "TMT127N", "TMT127C", "TMT128N", "TMT128C", "TMT129N", "TMT129C", "TMT130N", "TMT130C", "TMT131" };

    /// <summary>
    /// A TMT10 SDRF, one row per file and channel: a.raw and b.raw are fractions 1 and 2 of one plex (samples s1-s10), c.raw
    /// and d.raw of a second (s11-s20). Channels 1-5 are young, 6-10 old. <paramref name="plexColumn"/> adds a plex column.
    /// </summary>
    internal static string TmtSdrf(string dir, string name, string? plexColumn = null, bool twoPlexes = true)
    {
        var lines = new List<string> { Header + "\tfactor value[age]" + (plexColumn is null ? "" : $"\t{plexColumn}") };
        var files = new[] { "a.raw", "b.raw", "c.raw", "d.raw" }.Take(twoPlexes ? 4 : 2).ToArray();
        for (int f = 0; f < files.Length; f++)
        {
            int plex = f / 2, fraction = f % 2 + 1;
            for (int c = 0; c < Tmt10Labels.Length; c++)
            {
                int sample = plex * 10 + c + 1, biorep = plex * 5 + c % 5 + 1;
                lines.Add($"s{sample}\tMus musculus\t{biorep}\trun {f}\t{Tmt10Labels[c]}\t{files[f]}\t1\t{fraction}\t{(c < 5 ? "young" : "old")}"
                          + (plexColumn is null ? "" : $"\tplex{plex + 1}"));
            }
        }
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllLines(p, lines);
        return p;
    }

    // v0.4.34 (PLAN_tmt-dda-2 stage 6): the deposit's isobaric SDRF gives TmtDesign.txt; with no plex column, one plex, which
    // mzLib accepts only when every file carries the same channels. Ours is marked, so the next RemoveStale removes it.
    [Test]
    public void AnIsobaricSdrfOfOnePlexWritesTmtDesignAndItsMarker()
    {
        var (spectra, metadata, files) = Run();
        files = files.Take(2).ToList();
        foreach (string f in Directory.EnumerateFiles(spectra).Where(f => !files.Contains(f))) File.Delete(f);
        TmtSdrf(metadata, "PXD000001.sdrf.tsv", twoPlexes: false);
        var rec = DesignStage.PrepareIsobaric(spectra, files, null, null, "TMT10");
        Assert.That((rec["source"]!.GetValue<string>(), rec["written"]!.GetValue<bool>()), Is.EqualTo(("deposit", true)));
        Assert.That(rec["plex_rule"]!.GetValue<string>(), Does.StartWith("single"));
        Assert.That(rec["conditions"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(new[] { "old", "young" }));
        var tsv = File.ReadAllLines(Path.Combine(spectra, DesignStage.TmtFileName));
        Assert.That(tsv, Has.Length.EqualTo(1 + 2 * 10), "a row per file and channel");
        Assert.That(tsv[1].Split('\t')[3], Is.EqualTo("126"));
        Assert.That(File.ReadAllText(Path.Combine(spectra, DesignStage.TmtMarkerName)), Is.EqualTo(rec["design_sha256"]!.GetValue<string>()));
        Assert.That(DesignStage.RemoveStale(spectra), Is.True, "ours is removed");
        Assert.That(File.Exists(Path.Combine(spectra, DesignStage.TmtFileName)) || File.Exists(Path.Combine(spectra, DesignStage.TmtMarkerName)), Is.False);
    }

    // QuantProject 085: an SDRF an earlier search wrote names that search's files in comment[searched data file]
    // (a-calib.mzML), and mzLib's reader keys on that column. SdrfSearchScope.Restrict puts this search's names there
    // first, so a re-search of the raw files reads it; read directly, it refused every calibrated file.
    [Test]
    public void AnSdrfAnEarlierSearchWroteIsRestrictedToThisSearchFirst()
    {
        var (_, metadata, files) = Run();
        files = files.Take(2).ToList();
        string sdrf = TmtSdrf(metadata, "PXD000001.sdrf.tsv", twoPlexes: false);
        var lines = File.ReadAllLines(sdrf).Select(l => l.Split('\t').ToList()).ToList();
        int dataFile = lines[0].IndexOf("comment[data file]");
        lines[0].Insert(dataFile + 1, "comment[searched data file]");
        foreach (var row in lines.Skip(1))
            row.Insert(dataFile + 1, row[dataFile] == "a.raw" ? "a-calib.mzML" : row[dataFile]);
        File.WriteAllLines(sdrf, lines.Select(r => string.Join('\t', r)));

        var d = DesignStage.FindIsobaric(metadata, files, null, null, "TMT10", out var rec);
        Assert.That(d, Is.Not.Null, string.Join(" | ", rec["sdrfs_refused"]?.AsArray().Select(x => x!.ToJsonString()) ?? []));
        Assert.That((rec["files"]!.GetValue<int>(), rec["plexes"]!.AsArray().Count), Is.EqualTo((2, 1)));
        Assert.That(rec["scope"]!["searched_without_row"]!.AsArray(), Is.Empty);
    }

    // Two plexes and no plex column: a single plex is refused (a channel names two samples), and nothing infers the plex.
    // With characteristics[plex], the same SDRF gives two plexes.
    [Test]
    public void SeveralPlexesNeedAPlexColumn()
    {
        var (_, metadata, files) = Run();
        TmtSdrf(metadata, "PXD000001.sdrf.tsv");
        Assert.That(DesignStage.FindIsobaric(metadata, files, null, null, "TMT10", out var rec), Is.Null);
        Assert.That(rec["sdrfs_refused"]!.AsArray(), Has.Count.EqualTo(1));

        string curated = TmtSdrf(TestSupport.TempDir(), "PXD000001.sdrf.tsv", plexColumn: "characteristics[plex]");
        var d = DesignStage.FindIsobaric(metadata, files, curated, null, "TMT10", out rec);
        Assert.That(d, Is.Not.Null, string.Join(" | ", rec["sdrfs_refused"]?.AsArray().Select(x => x!.ToJsonString()) ?? []));
        Assert.That(rec["source"]!.GetValue<string>(), Is.EqualTo("question"), "the curated SDRF comes first");
        Assert.That(rec["plex_rule"]!.GetValue<string>(), Is.EqualTo("column: characteristics[plex]"));
        Assert.That(rec["plexes"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "plex1", "plex2" }));
    }

    // v0.4.46 (G69; the user, 2026-11-11): no default isobaric design. Until v0.4.45 a deposit no SDRF described was searched
    // with each file its own plex, so the fractions a.raw/b.raw of one plex became two plexes and their channels 20 samples
    // (PXD034564: one TMT10 plex in 32 files came out as 32 plexes and 320 samples). The user: "These are definitely not
    // plexes ... These fractions also must correctly be collapsed ... I think this must be in SDRF." Now nothing is written
    // and the search does not run; the batch parks the deposit until an SDRF states its plexes.
    [Test]
    public void WithNoStatedPlexAnIsobaricSearchIsRefusedNotDefaulted()
    {
        var (spectra, metadata, files) = Run();
        TmtSdrf(metadata, "PXD000001.sdrf.tsv");   // two plexes, no plex column: refused
        var e = Assert.Throws<IsobaricDesignMissingException>(() => DesignStage.PrepareIsobaric(spectra, files, null, null, "TMT10"));
        Assert.That(e, Is.InstanceOf<SearchSetupException>(), "a setup refusal: MetaMorpheus never runs");
        Assert.That(e!.Message, Does.Contain("plexes, fractions and technical replicates").And.Contain("every SDRF was refused").And.Contain("waits"));
        Assert.That(e.Record["sdrfs_refused"]![0]!["sdrf"]!.GetValue<string>(), Is.EqualTo("PXD000001.sdrf.tsv"));
        Assert.That(File.Exists(Path.Combine(spectra, DesignStage.TmtFileName)), Is.False, "no design is invented");
        Assert.That(Directory.Exists(Path.Combine(Path.GetDirectoryName(spectra)!, "pxreprise_sdrf")), Is.False, "and no default SDRF is written");

        // With no SDRF at all, the same; with one that states the plex, the two fractions of each plex are one plex.
        Directory.Delete(metadata, recursive: true);
        Assert.That(Assert.Throws<IsobaricDesignMissingException>(() => DesignStage.PrepareIsobaric(spectra, files, null, null, "TMT10"))!.Message,
            Does.Contain("no curated design and no deposited SDRF"));
        string curated = TmtSdrf(TestSupport.TempDir(), "PXD000001.sdrf.tsv", plexColumn: "characteristics[plex]");
        var rec = DesignStage.PrepareIsobaric(spectra, files, curated, null, "TMT10");
        Assert.That(rec["plexes"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "plex1", "plex2" }));
        Assert.That(rec["flag"], Is.Null);
        Assert.Throws<SearchSetupException>(() => DesignStage.FindIsobaric(spectra, files, null, null, "TMT99", out _), "an unknown kit is a wrong call");
    }

    // As for label-free: a curated design MetaMorpheus would not accept is fixed, not searched around.
    [Test]
    public void ARefusedCuratedIsobaricDesignStopsTheSearch()
    {
        var (spectra, metadata, files) = Run();
        TmtSdrf(metadata, "PXD000001.sdrf.tsv", plexColumn: "characteristics[plex]");   // the deposit's would do
        string curated = TmtSdrf(TestSupport.TempDir(), "PXD000001.sdrf.tsv");          // two plexes, no column
        var e = Assert.Throws<SearchSetupException>(() => DesignStage.PrepareIsobaric(spectra, files, curated, null, "TMT10"));
        Assert.That(e!.Message, Does.Contain("the question's design").And.Contain("refused"));
        Assert.That(File.Exists(Path.Combine(spectra, DesignStage.TmtFileName)), Is.False);
    }

    [Test]
    public void ADepositSdrfIsUsedWhenValidAndEveryRefusalIsRecorded()
    {
        var (spectra, metadata, files) = Run();
        Sdrf(metadata, "a_community_annotated.sdrf.tsv", biorep: _ => "1");
        Sdrf(metadata, "b.sdrf.tsv");
        var rec = DesignStage.Prepare(spectra, files, curated: Path.Combine(metadata, "absent.sdrf.tsv"), null);
        Assert.That((rec["source"]!.GetValue<string>(), rec["written"]!.GetValue<bool>()), Is.EqualTo(("deposit", true)));
        Assert.That(rec["deposit_sdrfs_refused"]![0]!["sdrf"]!.GetValue<string>(), Is.EqualTo("a_community_annotated.sdrf.tsv"));
    }

    [Test]
    public void WithNothingUsableTheSearchGetsPXRepriseDefaultSdrfAndDesign()
    {
        var (spectra, metadata, files) = Run();
        Sdrf(metadata, "only.sdrf.tsv", factor: null);
        File.WriteAllText(Path.Combine(metadata, "junk_sdrf.tsv"), "not an sdrf");
        var rec = DesignStage.Prepare(spectra, files, null, null, "mouse", "PXD000001");
        Assert.That((rec["source"]!.GetValue<string>(), rec["written"]!.GetValue<bool>()), Is.EqualTo((DesignStage.DefaultSource, true)));
        Assert.That(rec["deposit_sdrfs_refused"]!.AsArray(), Has.Count.EqualTo(2));
        Assert.That(rec["conditions"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(new[] { DesignStage.DefaultCondition }));
        string sdrf = rec["sdrf"]!.GetValue<string>();
        Assert.That(Path.GetFileName(sdrf), Is.EqualTo("PXD000001.pxreprise_default.sdrf.tsv"));
        Assert.That(Path.GetDirectoryName(sdrf), Is.Not.EqualTo(Path.GetFullPath(metadata)), "never where a later attempt reads deposit SDRFs");
        Assert.That(File.ReadAllText(sdrf), Does.Contain("mus musculus"));
        var tsv = File.ReadAllLines(Path.Combine(spectra, DesignStage.FileName));
        Assert.That(tsv.Skip(1).Select(l => l.Split('\t')[2]), Is.EqualTo(new[] { "1", "2", "3", "4" }), "each file its own biological replicate");

        // A second attempt still finds no deposit SDRF: its own default is not read back as the deposit's.
        var again = DesignStage.Prepare(spectra, files, null, null, "mouse", "PXD000001");
        Assert.That(again["deposit_sdrfs_refused"]!.AsArray(), Has.Count.EqualTo(2));
        Assert.That(DesignStage.Prepare(Run().Spectra, files, null, null)["reason"]!.GetValue<string>(), Does.Contain("no deposited SDRF"));
    }

    [Test]
    public void DesignIsAProfileSettingAndADesignsTableIsQuestionData()
    {
        string dir = TestSupport.TempDir();
        string src = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-2.toml"));
        string withDesign = src.Replace("[quant]", "[quant]\ndesign = \"sdrf\"").Replace("version = 2", "version = 99");
        Assert.That(Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "p99.toml", withDesign)).Design, Is.EqualTo("sdrf"));
        Assert.That(Config.ProfileLoader.Load(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-1.toml")).Design, Is.EqualTo("none"));
        Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "bad.toml", src.Replace("[quant]", "[quant]\ndesign = \"guess\""))));

        var q = Config.QuestionLoader.Load(TestSupport.WriteFile(dir, "question.toml",
            TestSupport.MinimalQuestion + "\n[designs]\ndir = \"designs\"\ncondition_columns = [\"factor value[age]\"]\n"));
        Assert.That(q.Designs!.For("PXD000001"), Is.EqualTo(Path.Combine(dir, "designs", "PXD000001.sdrf.tsv")));
        Assert.That(q.Designs.ConditionColumns, Is.EqualTo(new[] { "factor value[age]" }));
    }
}
