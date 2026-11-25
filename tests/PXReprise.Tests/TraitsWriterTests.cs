using PXReprise.Batch;
using PXReprise.Config;

namespace PXReprise.Tests;

/// <summary>
/// D96: the traits file for ptmQtl's per-site trait fit, from the question's [traits.&lt;id&gt;] and the latest catalog,
/// under aging's rules (their 341). The batch step is tested in BatchRunnerTests.
/// </summary>
public class TraitsWriterTests
{
    private static readonly TraitSource Age = new("age", "age group", "sample_ages.age_years", 0.1,
        ["characteristics[sex]", "acquisition_batch"], "age_decades", ["age_is_range"], "condition", "stratum");

    private const string Header = "dataset_id\trun_id\treplicate\tsample_value\tfactor_value\tfactor_level\texcl_0\tcov_0\tcov_1";

    private static string Tsv(params string[] rows) => string.Join("\r\n", new[] { Header }.Concat(rows)) + "\r\n";

    private static IReadOnlyList<TraitsFile> Write(string tsv, string? roles = null, params string[] datasets) =>
        TraitsWriter.Write(Age, tsv, roles, TestSupport.TempDir(), (datasets.Length == 0 ? ["D1"] : datasets).ToHashSet(StringComparer.Ordinal));

    private static TraitsFile WriteOne(TraitSource t, string tsv) => TraitsWriter.Write(t, tsv, null, TestSupport.TempDir(), new HashSet<string> { "D1" }).Single();

    // ---------------------------------------------------------------- the question's [traits.<id>]

    private static Question Load(string traits) =>
        QuestionLoader.Load(TestSupport.WriteFile(TestSupport.TempDir(), "question.toml", TestSupport.MinimalQuestion + "\n" + traits));

    [Test]
    public void ATraitsTableNamesWhereATraitComesFrom()
    {
        var q = Load("[traits]\nrequired = [\"age\"]\n[traits.age]\ndesign_factor = \"age group\"\nsample_value = \"sample_ages.age_years\"\nscale = 0.1\n"
            + "trait_id = \"age_decades\"\nsample_exclude_when = [\"age_is_range\", \"age_is_lower_bound\"]\n"
            + "covariates = [\"characteristics[sex]\", \"acquisition_batch\"]\ncovariate_factor_role = \"condition\"\nstratum_factor_role = \"stratum\"\n"
            + "never_drop = [\"acquisition_batch\"]\n");
        var t = q.TraitSources!["age"];
        Assert.That(t.NeverDrop, Is.EqualTo(new[] { "acquisition_batch" }));
        Assert.That((t.DesignFactor, t.SampleValue, t.Scale, t.TraitId, t.CovariateFactorRole, t.StratumFactorRole),
            Is.EqualTo(("age group", "sample_ages.age_years", 0.1, "age_decades", "condition", "stratum")));
        Assert.That(t.SampleExclusions, Is.EqualTo(new[] { "age_is_range", "age_is_lower_bound" }));
        Assert.That(t.Covariates, Is.EqualTo(new[] { "characteristics[sex]", "acquisition_batch" }));
        Assert.That(Load("[traits]\noptional = [\"bmi\"]\n[traits.bmi]\nsample_value = \"samples.bmi\"\n").TraitSources!["bmi"].TraitId, Is.EqualTo("bmi"), "the id when no trait_id");
        Assert.That(Load("[traits]\nrequired = [\"age\"]\n").TraitSources, Is.Empty, "no table, no source");
    }

    [TestCase("[traits.age]\ndesign_factor = \"age group\"\n", "neither requires nor allows")]
    [TestCase("[traits]\nrequired = [\"age\"]\nage = \"x\"\n", "must be a table")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\nscale = 0.1\n", "needs design_factor, sample_value, or both")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\nsample_value = \"age_years\"\n", "must be <table>.<column>")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\ndesign_factor = \"a\"\nscale = 0\n", "scale must be positive")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\ndesign_factor = \"a\"\ncovariates = [\"sex; DROP\"]\n", "must be characteristics[...]")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\ndesign_factor = \"a\"\ntrait_id = \"Age Decades\"\n", "must be lower-case")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\ndesign_factor = \"a\"\nsample_exclude_when = [\"x\"]\n", "needs sample_value")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\nsample_value = \"s.v\"\nsample_exclude_when = [\"x y\"]\n", "must be a column name")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\ndesign_factor = \"a\"\ncovariate_factor_role = \"x\"\nstratum_factor_role = \"x\"\n", "are both 'x'")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\ndesign_factor = \"a\"\ncovariates = [\"x\"]\nnever_drop = [\"acquisition_batch\"]\n", "is not one of its covariates")]
    [TestCase("[traits]\nrequired = [\"age\"]\n[traits.age]\ndesign_factor = \"a\"\nunit = \"years\"\n", "unit")]
    public void ABadTraitsTableIsRefused(string traits, string message)
    {
        if (!traits.StartsWith("[traits]", StringComparison.Ordinal)) traits = "[traits]\nrequired = [\"x\"]\n" + traits;
        Assert.That(() => Load(traits), Throws.InstanceOf<ConfigException>().With.Message.Contains(message));
    }

    [Test]
    public void SiteTraitsNeedsACatalog()
    {
        string dir = TestSupport.TempDir();
        foreach (string f in new[] { "enrichment.tsv", "cysteine.tsv" }) File.WriteAllText(Path.Combine(dir, f), "dataset\n");
        string Machine(string extra) => TestSupport.WriteFile(dir, "machine.toml",
            $"work_root = '{dir}'\ndatarepo = 'datarepo'\n[ptmqtl]\nenrichment = 'enrichment.tsv'\ncysteine = 'cysteine.tsv'\n{extra}[metamorpheus]\n\"1.1.11\" = 'C:/x/CMD.exe'\n");
        Assert.That(() => Config.Machine.Load(Machine("site_traits = true\n")), Throws.InstanceOf<ConfigException>().With.Message.Contains("needs 'ptmqtl.catalog'"));
        var m = Config.Machine.Load(Machine("site_traits = true\ncatalog = 'catalogs'\n"));
        Assert.That((m.PtmQtl!.SiteTraits, m.PtmQtl.Catalog), Is.EqualTo((true, Path.Combine(dir, "catalogs"))));
        var plain = Config.Machine.Load(Machine("")).PtmQtl!;
        Assert.That((plain.SiteTraits, plain.Catalog), Is.EqualTo((true, (string?)null)), "on by default; with no catalog it is logged and not run");
        Assert.That(Config.Machine.Load(Machine("site_traits = false\ncatalog = 'catalogs'\n")).PtmQtl!.SiteTraits, Is.False, "turned off by name");
    }

    // ---------------------------------------------------------------- the catalog and the queries

    [Test]
    public void TheCatalogIsAFileOrTheOneAFoldersLatestNames()
    {
        string dir = TestSupport.TempDir();
        string file = TestSupport.WriteFile(dir, "catalog-abc.duckdb", "");
        Assert.That(TraitsWriter.CatalogFile(file), Is.EqualTo(file));
        TestSupport.WriteFile(dir, "latest.json", "{\"catalog\": \"catalog-abc.duckdb\", \"catalog_id\": \"abc\"}");
        Assert.That(TraitsWriter.CatalogFile(dir), Is.EqualTo(file));
        TestSupport.WriteFile(dir, "latest.json", "{\"catalog\": \"catalog-gone.duckdb\"}");
        Assert.That(() => TraitsWriter.CatalogFile(dir), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("names no catalog file"));
        Assert.That(() => TraitsWriter.CatalogFile(TestSupport.TempDir()), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("no latest.json"));
    }

    [Test]
    public void TheQueriesJoinEachRunToItsSampleAndTheQuestionsNamesOnly()
    {
        string sql = TraitsWriter.Sql(Age);
        Assert.That(sql, Does.Contain("FROM assays a").And.Contain("WHERE a.assay_id LIKE '%:label_free'"));
        Assert.That(sql, Does.Contain("LEFT JOIN curated_design_factors f ON f.run_id = a.run_id AND f.factor = 'age group'"));
        Assert.That(sql, Does.Contain("LEFT JOIN \"sample_ages\" sv ON sv.sample_id = a.sample_id").And.Contain("CAST(sv.\"age_years\" AS DOUBLE) AS sample_value"));
        Assert.That(sql, Does.Contain("CAST(sv.\"age_is_range\" AS VARCHAR) AS excl_0"));
        Assert.That(sql, Does.Contain("c0.name = 'characteristics[sex]'").And.Contain("CAST(d.\"acquisition_batch\" AS VARCHAR) AS cov_1"));
        Assert.That(TraitsWriter.Sql(Age with { DesignFactor = "the 'old' arm" }), Does.Contain("f.factor = 'the ''old'' arm'"), "a literal is quoted");
        var factorOnly = TraitsWriter.Sql(new TraitSource("age", "age group", null, 1, []));
        Assert.That(factorOnly, Does.Contain("CAST(NULL AS DOUBLE) AS sample_value").And.Not.Contain("sv."));
        var sampleOnly = TraitsWriter.Sql(new TraitSource("bmi", null, "samples.bmi", 1, []));
        Assert.That(sampleOnly, Does.Contain("CAST(NULL AS DOUBLE) AS factor_value").And.Not.Contain("curated_design_factors"));
        Assert.That(TraitsWriter.RoleSql(Age), Is.EqualTo("SELECT DISTINCT run_id, factor, role, level FROM curated_design_factors WHERE role IN ('condition', 'stratum') AND factor <> 'age group' ORDER BY run_id, factor"));
        Assert.That(TraitsWriter.RoleSql(new TraitSource("bmi", null, "s.bmi", 1, [], StratumFactorRole: "stratum")),
            Is.EqualTo("SELECT DISTINCT run_id, factor, role, level FROM curated_design_factors WHERE role IN ('stratum') ORDER BY run_id, factor"));
        Assert.That((TraitsWriter.NeedsRoles(Age), TraitsWriter.NeedsRoles(new TraitSource("bmi", null, "s.bmi", 1, []))), Is.EqualTo((true, false)));
    }

    // ---------------------------------------------------------------- the files (aging 341)

    [Test]
    public void AFileHasOneRowPerRunWithTheSamplesValueScaledAndTheTraitsUnitInItsId()
    {
        var f = Write(Tsv("D1\tD1:r1\tm1\t2.0\t2.0\told\tfalse\tmale\tb1", "D1\tD1:r2\tm2\t\t0.5\tyoung\tfalse\tmale\tb1")).Single();
        Assert.That(f.Path, Is.Not.Null);
        Assert.That(File.ReadAllLines(f.Path!), Is.EqualTo(new[]
        {
            "dataset\trun\treplicate\ttrait_id\tvalue",
            "D1\tD1:r1\tm1\tage_decades\t0.2",
            "D1\tD1:r2\tm2\tage_decades\t0.05",
        }), "the factor's value where the sample has none; one-level covariates are not written");
        Assert.That(f.Notes, Is.Empty);
    }

    [Test]
    public void ADisagreementIsSaidAndTheSamplesValueWins()
    {
        var f = Write(Tsv("D1\tD1:r1\tm1\t2.0\t3.0\told\tfalse\tmale\tb1", "D1\tD1:r2\tm2\t1.0\t1.0\tyoung\tfalse\tmale\tb1")).Single();
        Assert.That(File.ReadAllLines(f.Path!)[1], Does.EndWith("\t0.2"));
        Assert.That(f.Notes.Single(), Does.Contain("D1:r1: the sample's value 2 and the design factor's 3 disagree"));
    }

    [Test]
    public void ARangeAndARunWithNoNumberAreLeftOutAndCounted()
    {
        var f = Write(Tsv("D1\tD1:r1\tm1\t2.0\t\told\ttrue\tmale\tb1", "D1\tD1:r2\tm2\t\t\tyoung\tfalse\tmale\tb1",
            "D1\tD1:r3\tm3\t\t\t\tfalse\tmale\tb1", "D1\tD1:r4\tm4\t1.0\t\t\tfalse\tmale\tb1", "D1\tD1:r5\tm5\t3.0\t\t\tfalse\tmale\tb1")).Single();
        Assert.That(f.Runs, Is.EqualTo(2));
        Assert.That(f.Notes, Is.EqualTo(new[]
        {
            "1 run(s) of samples marked age_is_range left out",
            "1 run(s) with a 'age group' level and no number left out",
            "1 run(s) with no value left out",
        }));
    }

    [Test]
    public void EveryDepositLeftOutComesBackWithItsReason()
    {
        var files = Write(Tsv("D1\tD1:r1\tm1\t\t\told\tfalse\tmale\tb1",                                          // a level, no number
                "D2\tD2:r1\t\t2.0\t\t\tfalse\tmale\tb1", "D2\tD2:r2\tm2\t1.0\t\t\tfalse\tmale\tb1",                    // a run with no individual
                "D3\tD3:r1\tm1\t2.0\t\t\tfalse\tmale\tb1", "D3\tD3:r1\tm1\t2.0\t\t\tfalse\tfemale\tb1",                 // one run, two rows
                "D4\tD4:r1\tm1\t\t\t\tfalse\tmale\tb1",                                                                // no source at all: out of scope
                "D5\tD5:r1\tm1\t2.0\t\t\tfalse\tmale\tb1"),                                                            // not delivered here
            null, "D1", "D2", "D3", "D4");
        Assert.That(files.Select(f => (f.Dataset, f.Path, f.Notes.Last())), Is.EqualTo(new[]
        {
            ("D1", (string?)null, "no run has a value"),
            ("D2", null, "a run has no curated individual, so its replicate is unknown"),
            ("D3", null, "a run has two different rows in the catalog"),
        }));
    }

    [Test]
    public void ACovariateIsWrittenOnlyWhereItSplitsTwoOrMoreIndividualsPerLevelAndOnlyOnce()
    {
        string tsv = Tsv("D1\tD1:r1\tm1\t2.0\t\t\tfalse\tmale\tb1", "D1\tD1:r2\tm2\t2.0\t\t\tfalse\tfemale\tb1",
            "D1\tD1:r3\tm3\t1.0\t\t\tfalse\tmale\tb2", "D1\tD1:r4\tm4\t1.0\t\t\tfalse\tfemale\tb2");
        // Each of two arms (vehicle, drug) holds two mice, split unlike sex or batch; a second factor splits them exactly as
        // the batch does.
        // One stratum level on every run is no split; a factor of another role is not read.
        string roles = "run_id\tfactor\trole\tlevel\nD1:r1\ttreatment\tcondition\tvehicle\nD1:r2\ttreatment\tcondition\tdrug\n"
            + "D1:r3\ttreatment\tcondition\tdrug\nD1:r4\ttreatment\tcondition\tvehicle\n"
            + "D1:r1\tday\tcondition\tone\nD1:r2\tday\tcondition\tone\nD1:r3\tday\tcondition\ttwo\nD1:r4\tday\tcondition\ttwo\n"
            + string.Concat(Enumerable.Range(1, 4).Select(i => $"D1:r{i}\torganism part\tstratum\tliver\nD1:r{i}\tbait\tcontrol\tx{i}\n"));
        var f = Write(tsv, roles).Single();
        Assert.That(f.Covariates, Is.EqualTo(new[] { "sex", "acquisition_batch", "treatment" }));
        Assert.That(f.Notes.Single(), Is.EqualTo("covariate day splits the samples as another does, so it is left out"));
        Assert.That(File.ReadAllLines(f.Path!)[1], Is.EqualTo("D1\tD1:r1\tm1\tage_decades\t0.2\tmale\tb1\tvehicle"));

        var thin = Write(Tsv("D1\tD1:r1\tm1\t2.0\t\t\tfalse\tmale\tb1", "D1\tD1:r2\tm2\t2.0\t\t\tfalse\tmale\tb1",
            "D1\tD1:r3\tm3\t1.0\t\t\tfalse\tfemale\t", "D1\tD1:r4\tm3\t1.0\t\t\tfalse\tfemale\tb1")).Single();
        Assert.That(thin.Covariates, Is.Empty);
        Assert.That(thin.Notes, Is.EqualTo(new[]
        {
            "covariate sex has a level held by fewer than two individuals, so it is left out",
            "covariate acquisition_batch is incomplete, so it is left out",
        }), "sex is counted by animal (m3's two runs are one), not by run");
    }

    [Test]
    public void ABatchNeverDroppedIsWrittenHoweverThinAndAnIncompleteOneLeavesTheDepositOut()
    {
        var keepBatch = Age with { NeverDropCovariates = ["acquisition_batch"] };
        // b1 holds one mouse only: sex would be dropped for that, the batch is not (the engine judges confounding).
        var thin = WriteOne(keepBatch, Tsv("D1\tD1:r1\tm1\t2.0\t\t\tfalse\tmale\tb1", "D1\tD1:r2\tm2\t2.0\t\t\tfalse\tmale\tb2",
            "D1\tD1:r3\tm3\t1.0\t\t\tfalse\tmale\tb2"));
        Assert.That(thin.Covariates, Is.EqualTo(new[] { "acquisition_batch" }));
        Assert.That(File.ReadAllLines(thin.Path!)[0], Does.EndWith("\tvalue\tacquisition_batch"));

        var gap = WriteOne(keepBatch, Tsv("D1\tD1:r1\tm1\t2.0\t\t\tfalse\tmale\tb1", "D1\tD1:r2\tm2\t1.0\t\t\tfalse\tmale\t"));
        Assert.That((gap.Path, gap.Notes.Last()), Is.EqualTo(((string?)null, "covariate acquisition_batch is incomplete, and it is never dropped")));

        var oneBatch = WriteOne(keepBatch, Tsv("D1\tD1:r1\tm1\t2.0\t\t\tfalse\tmale\tb1", "D1\tD1:r2\tm2\t1.0\t\t\tfalse\tmale\tb1"));
        Assert.That(oneBatch.Covariates, Is.Empty, "one batch: no term");
    }

    [Test]
    public void ADepositWithOneValueOfItsTraitHasNoSlopeAndIsLeftOut()
    {
        // aging 343: PXD047289 and PXD051644 are all 24 months.
        var f = WriteOne(Age, Tsv("D1\tD1:r1\tm1\t2.0\t\t\tfalse\tmale\tb1", "D1\tD1:r2\tm1\t2.0\t\t\tfalse\tmale\tb1",
            "D1\tD1:r3\tm2\t2.0\t\t\tfalse\tmale\tb1", "D1\tD1:r4\tm3\t1.0\t\t\ttrue\tmale\tb1"));
        Assert.That(f.Path, Is.Null);
        Assert.That(f.Notes.Last(), Is.EqualTo("one age_decades value (0.2) in 2 individual(s), so there is no slope"), "after exclusions; counted by animal");
    }

    [Test]
    public void ADepositWhoseStratumVariesIsLeftOutNeverPooled()
    {
        string tsv = Tsv("D1\tD1:r1\tm1\t2.0\t\t\tfalse\tmale\tb1", "D1\tD1:r2\tm1\t2.0\t\t\tfalse\tmale\tb1",
            "D1\tD1:r3\tm2\t1.0\t\t\tfalse\tmale\tb1", "D1\tD1:r4\tm2\t1.0\t\t\tfalse\tmale\tb1");
        string roles = "run_id\tfactor\trole\tlevel\nD1:r1\torganism part\tstratum\tliver\nD1:r2\torganism part\tstratum\tbrain\n"
            + "D1:r3\torganism part\tstratum\tliver\nD1:r4\torganism part\tstratum\tbrain\n";
        var f = Write(tsv, roles).Single();
        Assert.That(f.Path, Is.Null);
        Assert.That(f.Notes.Last(), Is.EqualTo("its stratum factor 'organism part' has 2 levels, and a fit per level is not built yet"));
    }

    [TestCase("characteristics[sex]", "sex")]
    [TestCase("characteristics[organism part]", "organism_part")]
    [TestCase("fear conditioning", "fear_conditioning")]
    [TestCase("acquisition_batch", "acquisition_batch")]
    public void ACovariatesColumnIsItsPlainName(string covariate, string column) => Assert.That(TraitsWriter.ColumnName(covariate), Is.EqualTo(column));

    [Test]
    public void AQueryWithoutItsColumnsIsRefused()
    {
        Assert.That(() => Write("dataset_id\trun_id\n"), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("no 'replicate' column"));
        Assert.That(() => Write(""), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("not even a header"));
        Assert.That(() => Write(Tsv("D1\tD1:r1\tm1\t2.0\t\t\tfalse\tmale\tb1"), "run_id\tfactor\tlevel\n"), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("role query"));
    }
}
