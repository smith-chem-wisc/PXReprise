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
    public void WithNothingUsableTheSearchRunsWithoutADesignAndSaysWhy()
    {
        var (spectra, metadata, files) = Run();
        Sdrf(metadata, "only.sdrf.tsv", factor: null);
        File.WriteAllText(Path.Combine(metadata, "junk_sdrf.tsv"), "not an sdrf");
        var rec = DesignStage.Prepare(spectra, files, null, null);
        Assert.That((rec["source"]!.GetValue<string>(), rec["written"]!.GetValue<bool>()), Is.EqualTo(("none", false)));
        Assert.That(rec["deposit_sdrfs_refused"]!.AsArray(), Has.Count.EqualTo(2));
        Assert.That(File.Exists(Path.Combine(spectra, DesignStage.FileName)), Is.False);
        Assert.That(DesignStage.Prepare(TestSupport.TempDir(), files, null, null)["reason"]!.GetValue<string>(), Does.Contain("no deposited SDRF"));
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
