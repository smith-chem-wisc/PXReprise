#:project ../../src/PXReprise.Runner/PXReprise.Runner.csproj
#:property PublishAot=false
// Real-data parity of the catalog's GO join (catalog format 17, CatalogGo.cs) against go.annotate_groups' per-bundle
// artefacts (up to 1.4.5; mzLib 1.0.598 builds of them are in the 1.5.0 re-ingest store).
//
// It reads bundles IN PLACE from a store it never writes to, runs go's three engines into a scratch store of its own,
// builds a one-file catalog there, and compares protein_localizations, organelle_term_categories and annotation_sources
// row for row with each bundle's old artefact.
//
//   dotnet run tools/go-parity/GoParity.cs -- <old store> <scratch dir> <ontology go.obo> <category map> <PXD...>
//
// The old store must hold a go.annotate_groups artefact for each dataset; its bundle is the one that artefact names.
// The engines run under go's definition ids with a stand-in install record (this is not a release build), so their
// artefacts are for this check only.
using System.Diagnostics;
using System.Globalization;
using PXReprise.Catalog;
using PXReprise.Runner;
using CatalogRunner = PXReprise.Catalog.Runner;

if (args.Length < 5)
{
    Console.Error.WriteLine("usage: GoParity.cs <old store> <scratch dir> <go.obo> <category map> <PXD...>");
    return 2;
}
string oldStore = Path.GetFullPath(args[0]), scratch = Path.GetFullPath(args[1]), obo = args[2], map = args[3];
var accessions = args.Skip(4).ToList();
var store = Path.Combine(scratch, "store");
Directory.CreateDirectory(store);
if (store.StartsWith(oldStore, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("the scratch store is inside the old store");
var install = new Dictionary<string, object?>
{
    ["distribution"] = "datarepo", ["version"] = "go-parity-check", ["source"] = "tools/go-parity", ["commit"] = "feat/catalog-go",
};

// Each dataset's old artefact, and the bundle it names.
var old = CatalogRunner.DiscoverArtefacts(oldStore, CatalogRunner.GoEngine);
var picked = new List<(string Accession, BundleRef Bundle, ArtefactRef Old)>();
foreach (var accession in accessions)
{
    var artefact = old.Single(a => ((IEnumerable<object?>)a.Record["requested_for"]!).OfType<IReadOnlyDictionary<string, object?>>()
        .Any(r => Equals(r["dataset_id"], accession)));
    var bundleId = (string)((IEnumerable<object?>)artefact.Record["requested_for"]!).OfType<IReadOnlyDictionary<string, object?>>().Single()["bundle_id"]!;
    picked.Add((accession, BundleRef.Load(Path.Combine(oldStore, accession, bundleId)), artefact));
}
var bundles = picked.Select(p => p.Bundle).ToList();

string Time(Action run)
{
    var clock = Stopwatch.StartNew();
    run();
    return $"{clock.Elapsed.TotalSeconds:0.0} s";
}

Console.WriteLine($"go.categorize_terms    {Time(() => GoReferenceEngines.CategorizeTermsRun(store, new Dictionary<string, string> { ["ontology"] = obo, ["category_map"] = map }, GoReferenceEngines.CategoryDefinitionId, install))}");
foreach (var (accession, bundle, _) in picked)
{
    var databases = Time(() =>
    {
        var r = GoReferenceEngines.AnnotateDatabasesRun(store, [bundle], new Dictionary<string, string> { ["ontology"] = obo }, GoReferenceEngines.AccessionDefinitionId, install);
        Console.Write($"go.annotate_databases  {accession}: {r.Written.Count} written, {r.AlreadyDone.Count} already done, ");
    });
    Console.WriteLine(databases);
    Console.WriteLine($"go.resolve_members     {accession}: {Time(() => GoReferenceEngines.ResolveMembersRun(store, [bundle], new Dictionary<string, string>(), GoReferenceEngines.MemberDefinitionId, install))}");
}

var output = Path.Combine(scratch, "catalog.duckdb");
var (artefacts, checks) = CatalogBuilder.SelectArtefacts(store, bundles);
CatalogResult built = null!;
Console.WriteLine($"build                  {Time(() => built = CatalogBuilder.BuildCatalog(bundles, output, overwrite: true, artefacts: artefacts, engineChecks: checks))}");
Console.WriteLine($"  checks {built.Checks.Count}, failed {built.FailedChecks.Count}");
foreach (var c in built.Checks.Where(c => c.Name.StartsWith("go", StringComparison.Ordinal) && c.Kind != "row_count"))
    Console.WriteLine($"  {c.Kind,-18} {c.Ok,-5} {c.Name}: {c.Observed} of {c.Expected}{(c.Detail is null ? "" : "; " + c.Detail)}");

string Q(string s) => "'" + s.Replace("'", "''") + "'";
long Scalar(string sql) => Convert.ToInt64(CatalogBuilder.RunQuery(output, sql).Rows.Single()[0], CultureInfo.InvariantCulture);

Console.WriteLine();
Console.WriteLine("table                      rows | on disk (artefacts)");
foreach (var table in CatalogBuilder.GoReferenceTables)
    Console.WriteLine($"  {table,-24} {Scalar($"SELECT count(*) FROM {table}"),10}");
long Bytes(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
foreach (var engine in CatalogRunner.GoEngines)
{
    var dirs = CatalogRunner.DiscoverArtefacts(store, engine).Where(a => artefacts.Any(s => s.ArtefactId == a.ArtefactId)).ToList();
    Console.WriteLine($"  {engine,-24} {dirs.Count} artefact(s), {Bytes(Path.Combine(store, CatalogRunner.EngineDir, engine)) / 1e6:0.0} MB "
        + $"(Parquet {dirs.Sum(a => Directory.EnumerateFiles(a.Path, "*.parquet").Sum(f => new FileInfo(f).Length)) / 1e6:0.0} MB)");
}
Console.WriteLine($"  go.annotate_groups (old)  {picked.Count} artefact(s), {picked.Sum(p => Bytes(p.Old.Path)) / 1e6:0.0} MB");

// Row for row, per bundle. source_id is excluded: it named the per-bundle annotation file and now names the artefacts.
var tables = new (string Table, string Columns)[]
{
    ("protein_localizations", "protein_accession, compartment, go_release, evidence, protein_group, q_value, n_members, n_with, inherited, propagated"),
    ("organelle_term_categories", "compartment, category_map_name, organelle_map_version, go_release, organelle_category, organelle_subcategory"),
    ("annotation_sources", "owner_project, doi"),
};
Console.WriteLine();
var allEqual = true;
foreach (var (accession, bundle, artefact) in picked)
    foreach (var (table, columns) in tables)
    {
        var path = artefact.TablePath(table)!.Replace('\\', '/');
        var sql = $@"WITH new AS (SELECT {columns} FROM {table} WHERE bundle_id = {Q(bundle.BundleId)}),
                          old AS (SELECT {columns} FROM read_parquet({Q(path)}))
                     SELECT (SELECT count(*) FROM new), (SELECT count(*) FROM old),
                            (SELECT count(*) FROM (SELECT * FROM new EXCEPT ALL SELECT * FROM old)),
                            (SELECT count(*) FROM (SELECT * FROM old EXCEPT ALL SELECT * FROM new))";
        var r = CatalogBuilder.RunQuery(output, sql).Rows.Single().Select(v => Convert.ToInt64(v, CultureInfo.InvariantCulture)).ToArray();
        var equal = r[0] == r[1] && r[2] == 0 && r[3] == 0;
        allEqual &= equal;
        Console.WriteLine($"{accession} {table,-26} new {r[0],8}  old {r[1],8}  only new {r[2],6}  only old {r[3],6}  {(equal ? "EQUAL" : "DIFFER")}");
        if (!equal)
            foreach (var side in new[] { ("new", "old"), ("old", "new") })
                foreach (var row in CatalogBuilder.RunQuery(output,
                             $"WITH new AS (SELECT {columns} FROM {table} WHERE bundle_id = {Q(bundle.BundleId)}), old AS (SELECT {columns} FROM read_parquet({Q(path)})) "
                             + $"SELECT * FROM (SELECT * FROM {side.Item1} EXCEPT ALL SELECT * FROM {side.Item2}) ORDER BY ALL LIMIT 5").Rows)
                    Console.WriteLine($"    only {side.Item1}: {string.Join(" | ", row.Select(v => v?.ToString() ?? "NULL"))}");
    }
Console.WriteLine(allEqual ? "ALL EQUAL" : "DIFFERENCES ABOVE");
return allEqual ? 0 : 1;
