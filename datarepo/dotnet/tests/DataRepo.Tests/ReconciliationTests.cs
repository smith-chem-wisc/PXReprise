using DataRepo.Catalog;
using DuckDB.NET.Data;

namespace DataRepo.Tests;

/// <summary>1.4.1's build-side reconciliation (aging DATAREPO-86 and -87): each check judged against the bound the
/// producer's file format leaves, from the bundle's stored rows.</summary>
public class ReconciliationTests
{
    private static BundleRef Bundle(params Dictionary<string, object?>[] checks) =>
        new("unused", new Dictionary<string, object?>
        {
            ["dataset_id"] = "PXD000001",
            ["bundle_id"] = "b1",
            ["reconciliation"] = checks.Cast<object?>().ToList(),
        });

    private static Dictionary<string, object?> Check(string name, double? observed, double? expected) => new()
    {
        ["name"] = name, ["observed"] = observed, ["expected"] = expected, ["ok"] = observed is null || expected is null || observed == expected,
    };

    private static string Status(BundleRef r, ReconciliationTerms? terms, string check) =>
        CatalogBuilder.JudgeReconciliation(r, terms).Single(x => x.Check == check).Status;

    [Test]
    public void APsmCountIsJudgedAgainstItsExactBound()
    {
        // 100 counted, 3 of them at the threshold, 5 passing with an unresolved notch: [97, 105].
        var terms = new ReconciliationTerms(100, 5, 3, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(Status(Bundle(Check("psms_target_1pct", 100, 100)), terms, "psms_target_1pct"), Is.EqualTo("agrees"));
            Assert.That(Status(Bundle(Check("psms_target_1pct", 100, 105)), terms, "psms_target_1pct"), Is.EqualTo("explained"));
            Assert.That(Status(Bundle(Check("psms_target_1pct", 100, 97)), terms, "psms_target_1pct"), Is.EqualTo("explained"));
            Assert.That(Status(Bundle(Check("psms_target_1pct", 100, 106)), terms, "psms_target_1pct"), Is.EqualTo("unexplained"));
            Assert.That(Status(Bundle(Check("psms_target_1pct", 100, 96)), terms, "psms_target_1pct"), Is.EqualTo("unexplained"));
        });
    }

    [Test]
    public void TheRecountReplacesAnIngestCountThatDroppedEntrapmentTargets()
    {
        // DATAREPO-87: an ingest that tested the label for equality with "T" recorded 90; the stored rows, which read ET
        // as target, count 100, the producer's number.
        var row = CatalogBuilder.JudgeReconciliation(Bundle(Check("psms_target_1pct", 90, 100)), new ReconciliationTerms(100, 0, 0, 0, 0)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Status, Is.EqualTo("agrees"));
            Assert.That(row.Observed, Is.EqualTo(100));
            Assert.That(row.RecordedObserved, Is.EqualTo(90));
            Assert.That(row.RecordedOk, Is.False);
        });
    }

    [Test]
    public void APeptidoformCountMayOnlyExceedTheProducersByTheEstimatedTerms()
    {
        // The peptide bound is [count - ambiguous - rounding, count]: the producer never counts more than the bundle.
        var terms = new ReconciliationTerms(0, 0, 0, 2, 1);
        Assert.Multiple(() =>
        {
            Assert.That(Status(Bundle(Check("peptidoforms_target_1pct", 50, 47)), terms, "peptidoforms_target_1pct"), Is.EqualTo("explained"));
            Assert.That(Status(Bundle(Check("peptidoforms_target_1pct", 50, 46)), terms, "peptidoforms_target_1pct"), Is.EqualTo("unexplained"));
            Assert.That(Status(Bundle(Check("peptidoforms_target_1pct", 50, 51)), terms, "peptidoforms_target_1pct"), Is.EqualTo("unexplained"));
        });
    }

    [Test]
    public void ACheckWithNoBoundKeepsItsRecordedVerdictAndAMissingSideIsNotCompared()
    {
        var r = Bundle(Check("protein_groups_1pct", 1184, 1185), Check("runs", 32, null));
        var rows = CatalogBuilder.JudgeReconciliation(r, new ReconciliationTerms(0, 0, 0, 0, 0));
        Assert.Multiple(() =>
        {
            Assert.That(rows.Single(x => x.Check == "protein_groups_1pct").Status, Is.EqualTo("unexplained"));
            Assert.That(rows.Single(x => x.Check == "runs").Status, Is.EqualTo("not_compared"));
        });
    }

    [Test]
    public void AProteinGroupTheProducerWroteTwiceExplainsACountOneShort()
    {
        // PXD027971 (aging 122 c): MetaMorpheus 1.1.11 wrote one group twice, identically, and counted 1,185.
        var manifest = new Dictionary<string, object?>
        {
            ["dataset_id"] = "PXD027971",
            ["bundle_id"] = "b1",
            ["reconciliation"] = new List<object?> { Check("protein_groups_1pct", 1184, 1185) },
            ["collapsed_duplicates"] = new List<object?>
            {
                new Dictionary<string, object?> { ["table"] = "protein_groups", ["written"] = 2L, ["dropped"] = 1L },
                new Dictionary<string, object?> { ["table"] = "quant_values", ["written"] = 2L, ["dropped"] = 1L },
            },
        };
        var row = CatalogBuilder.JudgeReconciliation(new BundleRef("unused", manifest), new ReconciliationTerms(0, 0, 0, 0, 0)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Status, Is.EqualTo("explained"));
            Assert.That((row.ExplainedLower, row.ExplainedUpper), Is.EqualTo(((long?)1184, (long?)1185)));
        });
    }

    [Test]
    public void TheTermsAreCountedFromTheStoredRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "datarepo-recon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using (var con = new DuckDBConnection("Data Source=:memory:"))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                // A: two PSMs tied at the best score, one notch-ambiguous, so A's best is ambiguous.
                // B: its best PSM resolved, a lower-scoring one did not, so B's best is not ambiguous.
                // Counted (target, both q <= 0.01, resolved): the A/B resolved ones and the one at exactly 0.01 = 3;
                // the threshold one is the rounding term; ambiguous passing = 2; the decoy and q 0.02 rows are not seen.
                cmd.CommandText = $@"
                    COPY (SELECT * FROM (VALUES
                        ('A', 10.0, 0.001, 0.001, true,  'target'),
                        ('A', 10.0, 0.001, 0.001, false, 'target'),
                        ('B', 12.0, 0.002, 0.002, false, 'target'),
                        ('B',  5.0, 0.003, 0.003, true,  'target'),
                        ('C',  4.0, 0.01,  0.005, false, 'target'),
                        ('D',  9.0, 0.001, 0.001, false, 'decoy'),
                        ('E',  3.0, 0.02,  0.001, false, 'target')
                    ) t(peptidoform, score, q_value, q_value_notch, notch_ambiguous, target_decoy))
                    TO '{dir.Replace('\\', '/')}/psms.parquet' (FORMAT parquet);
                    COPY (SELECT * FROM (VALUES
                        ('PXD000001', 'A', 0.001, 0.001, 'target'),
                        ('PXD000001', 'B', 0.01,  0.001, 'target'),
                        ('PXD000001', 'C', 0.02,  0.001, 'target')
                    ) t(dataset_id, peptidoform, best_q_value, best_q_value_notch, target_decoy))
                    TO '{dir.Replace('\\', '/')}/peptidoforms.parquet' (FORMAT parquet);";
                cmd.ExecuteNonQuery();
            }
            var terms = CatalogBuilder.ComputeReconciliationTerms(new BundleRef(dir, new Dictionary<string, object?>
            {
                ["dataset_id"] = "PXD000001", ["bundle_id"] = "b1",
            }));
            Assert.That(terms, Is.EqualTo(new ReconciliationTerms(
                PsmsRecount: 3, PsmsAmbiguous: 2, PsmsRounding: 1, PeptidoformsAmbiguous: 1, PeptidoformsRounding: 1)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
