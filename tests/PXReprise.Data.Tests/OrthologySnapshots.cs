using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Bundle;
using PXReprise.Runner;
using DuckDB.NET.Data;

namespace PXReprise.Data.Tests;

/// <summary>Small orthology snapshots in logs' file contract (<c>logs:DEF-ORTHOLOGY v1</c>), released and registered the
/// way logs and an operator do it: shared by <see cref="OrthologyEngineTests"/> and <see cref="OrthologyCatalogTests"/>.</summary>
/// <remarks>The real columns, the layout, and the release's own <c>views.sql</c> (Fixtures/orthology). Two species,
/// <c>sp_a</c> (genes A1-A4) and <c>sp_b</c> (B1, B2), with a gene of <c>sp_a</c> in each of the four <c>pair_status</c>
/// classes: A1 has an ortholog (B1, one-to-one); A2 shares tree T2 with B2 but no edge; A3's tree holds no <c>sp_b</c>
/// gene; A4 is in no tree. Paralogs A2-A3 and B1-B2 sit in the same-species files.</remarks>
internal static class OrthologySnapshots
{
    public static readonly Dictionary<string, object?> StandInInstall = new()
    {
        ["distribution"] = "datarepo", ["version"] = "test", ["source"] = "test-stand-in", ["commit"] = "0",
    };

    public const string GeneSetA = "aaaa000000000000000000000000000000000000000000000000000000000000";
    public const string GeneSetB = "bbbb000000000000000000000000000000000000000000000000000000000000";

    private static string ViewsSql => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "orthology", "views.sql");

    /// <summary>A snapshot directory in the contract's layout, under <paramref name="root"/>.</summary>
    /// <param name="extraMember">Puts gene A1 in a second tree, which makes <c>pair_status</c> give it two rows.</param>
    /// <param name="oneToMany">Adds A5 in <c>sp_a</c> and B3, B4 in <c>sp_b</c>, all in tree T4, with A5 an
    /// <c>ortholog_one2many</c> of both B3 and B4. B4 has no gene name.</param>
    public static string Make(string root, string name, bool extraMember = false, bool oneToMany = false)
    {
        var dir = Path.Combine(root, "src-" + name, name);
        foreach (var sub in new[] { "genes", "members", "pairs" }) Directory.CreateDirectory(Path.Combine(dir, sub));
        using (var con = new DuckDBConnection("DataSource=:memory:"))
        {
            con.Open();
            void Exec(string sql)
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            string P(string rel) => "'" + Path.Combine(dir, rel).Replace('\\', '/') + "'";
            const string gene = "gene_id VARCHAR, gene_version INTEGER, gene_biotype VARCHAR, gene_name VARCHAR, seq_region VARCHAR, species VARCHAR";
            const string member = "gene_id VARCHAR, species VARCHAR, group_id VARCHAR, source_group_id VARCHAR, canonical_protein_id VARCHAR";
            var geneCols = gene.Replace(" VARCHAR", "").Replace(" INTEGER", "");
            Exec($"COPY (SELECT * FROM (VALUES ('A1',1,'protein_coding','a1','1','sp_a'),('A2',1,'protein_coding','a2','1','sp_a'),"
                 + "('A3',1,'protein_coding','a3','1','sp_a'),('A4',1,'lncRNA','a4','1','sp_a')"
                 + (oneToMany ? ",('A5',1,'protein_coding','a5','2','sp_a')" : "")
                 + $") t({geneCols})) TO {P("genes/sp_a.parquet")}");
            Exec($"COPY (SELECT * FROM (VALUES ('B1',1,'protein_coding','b1','1','sp_b'),('B2',1,'protein_coding','b2','1','sp_b')"
                 + (oneToMany ? ",('B3',1,'protein_coding','b3','2','sp_b'),('B4',1,'protein_coding',NULL,'2','sp_b')" : "")
                 + $") t({geneCols})) TO {P("genes/sp_b.parquet")}");
            var cols = member.Replace(" VARCHAR", "");
            // A1 has an ortholog; A2 shares tree T2 with B2 but no edge; A3's tree holds no sp_b gene; A4 is in no tree.
            Exec($"COPY (SELECT * FROM (VALUES ('A1','sp_a','T1','s1','PA1'),('A2','sp_a','T2','s2','PA2'),('A3','sp_a','T3','s3','PA3')"
                 + (extraMember ? ",('A1','sp_a','T2','s2','PA1')" : "")
                 + (oneToMany ? ",('A5','sp_a','T4','s4','PA5')" : "") + $") t({cols})) TO {P("members/sp_a.parquet")}");
            Exec($"COPY (SELECT * FROM (VALUES ('B1','sp_b','T1','s1','PB1'),('B2','sp_b','T2','s2','PB2')"
                 + (oneToMany ? ",('B3','sp_b','T4','s4','PB3'),('B4','sp_b','T4','s4','PB4')" : "") + $") t({cols})) TO {P("members/sp_b.parquet")}");
            const string pair = "SELECT homology_id::VARCHAR AS homology_id, relationship_type::VARCHAR AS relationship_type, "
                + "relationship_class::VARCHAR AS relationship_class, species_a::VARCHAR AS species_a, species_b::VARCHAR AS species_b, "
                + "gene_a::VARCHAR AS gene_a, protein_a::VARCHAR AS protein_a, identity_a::DOUBLE AS identity_a, gene_b::VARCHAR AS gene_b, "
                + "protein_b::VARCHAR AS protein_b, identity_b::DOUBLE AS identity_b, NULL::DOUBLE AS dn, NULL::DOUBLE AS ds, "
                + "75::INTEGER AS goc_score, 99.0::DOUBLE AS wga_coverage, true AS is_high_confidence, 'test'::VARCHAR AS source_dump FROM ";
            const string pcols = "t(homology_id, relationship_type, relationship_class, species_a, species_b, gene_a, protein_a, identity_a, gene_b, protein_b, identity_b)";
            Exec($"COPY ({pair}(VALUES ('1','ortholog_one2one','ortholog','sp_a','sp_b','A1','PA1',70.0,'B1','PB1',69.0)"
                 + (oneToMany
                     ? ",('4','ortholog_one2many','ortholog','sp_a','sp_b','A5','PA5',60.0,'B3','PB3',61.0)"
                       + ",('5','ortholog_one2many','ortholog','sp_a','sp_b','A5','PA5',55.0,'B4','PB4',50.0)"
                     : "")
                 + $") {pcols}) TO {P("pairs/sp_a__sp_b.parquet")}");
            Exec($"COPY ({pair}(VALUES ('2','within_species_paralog','paralog','sp_a','sp_a','A2','PA2',40.0,'A3','PA3',41.0)) {pcols}) TO {P("pairs/sp_a__sp_a.parquet")}");
            Exec($"COPY ({pair}(VALUES ('3','within_species_paralog','paralog','sp_b','sp_b','B1','PB1',40.0,'B2','PB2',41.0)) {pcols}) TO {P("pairs/sp_b__sp_b.parquet")}");
        }
        File.Copy(ViewsSql, Path.Combine(dir, "views.sql"));
        WriteManifest(dir);
        return dir;
    }

    /// <summary>Writes manifest.json listing every file as it now is, each Parquet file's rows counted from the file.</summary>
    public static void WriteManifest(string dir, Action<JsonObject>? edit = null)
    {
        var files = new JsonArray();
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Where(f => f != "manifest.json").Order(StringComparer.Ordinal))
        {
            var full = Path.Combine(dir, path);
            var entry = new JsonObject { ["path"] = path };
            if (path.EndsWith(".parquet", StringComparison.Ordinal)) entry["rows"] = CountRows(full);
            entry["bytes"] = new FileInfo(full).Length;
            entry["sha256"] = BundleWriter.Sha256File(full);
            files.Add(entry);
        }
        var manifest = new JsonObject
        {
            ["format"] = "ensembl-orthology-snapshot", ["format_version"] = 1, ["snapshot_id"] = "test-" + Path.GetFileName(dir),
            ["source"] = "ensembl_compara", ["release"] = "116", ["species"] = new JsonArray("sp_a", "sp_b"),
            ["gene_set_sha256"] = new JsonObject { ["sp_a"] = GeneSetA, ["sp_b"] = GeneSetB },
            ["files"] = files,
        };
        edit?.Invoke(manifest);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static long CountRows(string parquet)
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM read_parquet('{parquet.Replace('\\', '/')}')";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>The tar and the manifest beside it, as logs releases them, and a release entry naming both.</summary>
    public static (Dictionary<string, string> Inputs, SnapshotRelease Release) Release(string dir)
    {
        var tar = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, Path.GetFileName(dir) + ".tar");
        if (File.Exists(tar)) File.Delete(tar);
        TarFile.CreateFromDirectory(dir, tar, includeBaseDirectory: true);
        var manifest = Path.Combine(Path.GetDirectoryName(tar)!, Path.GetFileName(dir) + ".manifest.json");
        File.Copy(Path.Combine(dir, "manifest.json"), manifest, overwrite: true);
        var release = new SnapshotRelease("orthology-test-" + Path.GetFileName(dir), BundleWriter.Sha256File(tar), BundleWriter.Sha256File(manifest), "test attribution");
        return (new Dictionary<string, string> { ["snapshot_tar"] = tar, ["snapshot_manifest"] = manifest }, release);
    }

    /// <summary>Registers a released snapshot into <paramref name="store"/> with a stand-in install record.</summary>
    public static RunResult Register(string store, Dictionary<string, string> inputs, SnapshotRelease release) =>
        OrthologyEngine.Run(store, inputs, [release], StandInInstall);
}
