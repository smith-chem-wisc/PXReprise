using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;
using DataRepo.Study;
using DuckDB.NET.Data;

namespace DataRepo.Catalog;

/// <summary>One written bundle, as the catalog sees it: its manifest and where it sits.</summary>
/// <param name="Path">The bundle directory, as <c>str(Path)</c>.</param>
/// <param name="Manifest">The parsed <c>bundle.json</c>, as plain values.</param>
public sealed record BundleRef(string Path, IReadOnlyDictionary<string, object?> Manifest)
{
    /// <exception cref="CatalogException">The directory holds no <c>bundle.json</c>, or it cannot be read.</exception>
    public static BundleRef Load(string path)
    {
        var manifestPath = System.IO.Path.Combine(path, BundleWriter.ManifestName);
        if (!File.Exists(manifestPath))
            throw new CatalogException($"{SourcesPy.PathStr(path)} holds no {BundleWriter.ManifestName}, so it is not a bundle");
        return new BundleRef(SourcesPy.PathStr(path), CatalogBuilder.ReadJsonObject(manifestPath));
    }

    public string DatasetId => SourcesPy.Str(Manifest["dataset_id"]);

    public string BundleId => SourcesPy.Str(Manifest["bundle_id"]);

    public string SchemaVersion => SourcesPy.Str(SourcesPy.Get(Manifest, "schema_version", "?"));

    public string WrittenUtc => SourcesPy.Str(SourcesPy.Get(Manifest, "written_utc", ""));

    public IReadOnlyDictionary<string, long> RowCounts => CatalogBuilder.StrMap(SourcesPy.Get(Manifest, "tables"), CatalogBuilder.PyInt);

    /// <summary>The table's Parquet file, or null when the bundle omits the table (it had no rows).</summary>
    public string? TablePath(string table)
    {
        var path = System.IO.Path.Combine(Path, $"{table}.parquet");
        return File.Exists(path) ? SourcesPy.PathStr(path) : null;
    }
}

/// <summary>One written study bundle, as the catalog sees it.</summary>
/// <remarks>Separate from <see cref="BundleRef"/> because the two are not interchangeable: a search bundle is
/// one dataset's evidence and a study bundle is one layer's model results over however many datasets. They
/// share a store and nothing else. Only READ here; writing one is <c>study.py</c>'s job.</remarks>
public sealed record StudyBundleRef(string Path, IReadOnlyDictionary<string, object?> Manifest)
{
    /// <exception cref="CatalogException">The directory holds no <c>study.json</c>, or it cannot be read.</exception>
    public static StudyBundleRef Load(string path)
    {
        var manifestPath = System.IO.Path.Combine(path, CatalogBuilder.StudyBundleManifest);
        if (!File.Exists(manifestPath))
            throw new CatalogException($"{SourcesPy.PathStr(path)} holds no {CatalogBuilder.StudyBundleManifest}, so it is not a study bundle");
        return new StudyBundleRef(SourcesPy.PathStr(path), CatalogBuilder.ReadJsonObject(manifestPath));
    }

    public string Layer => SourcesPy.Str(Manifest["layer"]);

    public string BundleId => SourcesPy.Str(Manifest["bundle_id"]);

    public string LayerVersion => SourcesPy.Str(SourcesPy.Get(Manifest, "layer_version", "?"));

    public string SchemaVersion => SourcesPy.Str(SourcesPy.Get(Manifest, "schema_version", "?"));

    public string WrittenUtc => SourcesPy.Str(SourcesPy.Get(Manifest, "written_utc", ""));

    public IReadOnlyDictionary<string, long> RowCounts => CatalogBuilder.StrMap(SourcesPy.Get(Manifest, "tables"), CatalogBuilder.PyInt);

    public string? TablePath(string table)
    {
        var path = System.IO.Path.Combine(Path, $"{table}.parquet");
        return File.Exists(path) ? SourcesPy.PathStr(path) : null;
    }
}

/// <summary>One check a build ran: a row of <c>catalog_checks</c>.</summary>
/// <param name="Observed">Null where there was nothing to count (a table no schema knows).</param>
public sealed record CatalogCheck(string Name, string Kind, bool Ok, long? Observed, long? Expected, string? Detail);

/// <summary>One dataset's NON-content manifest fields as a build's manifest gives them: a row of
/// <c>dataset_annotations</c> (G84).</summary>
/// <param name="InManifest">False when the build had no manifest entry for the dataset; every field is then null.</param>
public sealed record DatasetAnnotation(
    string DatasetId,
    string BundleId,
    bool InManifest,
    string? Status,
    string? Reason,
    string? Notes,
    IReadOnlyList<string>? Flags,
    string? ProvenanceSchema,
    string? Sdrf)
{
    /// <summary>The fields as one line of JSON, keys sorted: what the catalog id hashes. Not the manifest's path,
    /// which moves when the file does while the content stays.</summary>
    public string Canonical() => PyFormat.Json(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["in_manifest"] = InManifest,
        ["status"] = Status,
        ["reason"] = Reason,
        ["notes"] = Notes,
        ["flags"] = Flags?.Cast<object?>().ToList(),
        ["provenance_schema"] = ProvenanceSchema,
        ["sdrf"] = Sdrf,
    }, sortKeys: true, ensureAscii: true);
}

/// <summary>What a build did, in the terms an operator or a release checklist needs.</summary>
public sealed record CatalogResult
{
    public required string Path { get; init; }
    public required string CatalogId { get; init; }
    public required IReadOnlyList<string> Datasets { get; init; }
    public required IReadOnlyList<BundleRef> Bundles { get; init; }
    public IReadOnlyList<StudyBundleRef> StudyBundles { get; init; } = [];
    public IReadOnlyList<ArtefactRef> Artefacts { get; init; } = [];
    public IReadOnlyDictionary<string, long> RowCounts { get; init; } = new Dictionary<string, long>();
    public IReadOnlyList<CatalogCheck> Checks { get; init; } = [];
    public int Indexes { get; init; }
    public bool Skipped { get; init; }

    public IReadOnlyList<CatalogCheck> FailedChecks => Checks.Where(c => !c.Ok).ToList();
}

/// <summary>A derived table's or view's meaning, for the MCP describe tool.</summary>
public sealed record DerivedDoc(string Description, IReadOnlyDictionary<string, string> Columns);

/// <summary>
/// Build the query catalog: many immutable bundles into one DuckDB file. Ported from <c>catalog.py</c>.
/// </summary>
/// <remarks>
/// Layer 2 of the architecture (FRAMEWORK section 2, roadmap step 2). Layer 1 is the product -- the Parquet
/// bundles are what gets cited and what a release freezes -- and this layer is a <i>derived</i> artifact built
/// from them and thrown away whenever it needs rebuilding. Nothing is ever written back into a bundle, and
/// deleting the catalog loses nothing.
/// <para>Three things make it a catalog rather than a pile of Parquet:</para>
/// <list type="bullet">
/// <item><b>It is one file.</b> Tables are materialised, not views over <c>read_parquet</c>, so the catalog can
/// be copied to a server, mounted in a container or handed to an agent on its own (D1: it has to move).</item>
/// <item><b>Rows carry where they came from.</b> Every table gains <c>dataset_id</c> and <c>bundle_id</c>, so a
/// cross-dataset answer can always be traced back to the bundle that holds the evidence.</item>
/// <item><b>Cross-dataset indexes exist.</b> A bundle can answer "what is in this dataset". Only the catalog
/// can answer "which datasets have this protein", which is the question the repository exists for.</item>
/// </list>
/// <para>Like a bundle, a catalog is content-addressed on what went into it -- the bundle ids, the schema
/// version and the builder version -- so rebuilding from the same bundles is a no-op, and a catalog that is
/// cited alongside a release can be checked against the bundles it claims to hold.</para>
/// <para>Every SQL statement is the Python's, run on the same DuckDB engine version (DuckDB.NET 1.5.5 ships
/// DuckDB 1.5.5), so the work is done where the Python did it: in SQL.</para>
/// </remarks>
public static class CatalogBuilder
{
    /// <summary>Bumped when a build produces a different catalog from the same bundles.</summary>
    /// <remarks>Part of the content hash, so a change to the builder gives every catalog a new id even from
    /// the same bundles. Separate from the package version on purpose: a change to what <c>build</c> writes
    /// must re-id catalogs, and must NOT re-id bundles holding byte-identical rows from an unchanged ingest
    /// path. "2" added the study layer's tables; "3" fills them -- a study bundle's rows, their two provenance
    /// columns and <c>catalog_study_bundles</c>; "4" adds <c>search_modifications_placed</c>; "5" adds
    /// <c>dataset_overview.enrichment_mixed</c> (G63); "6" loads engine artefacts (<c>gene_resolutions</c>,
    /// G64) and <c>catalog_engine_artefacts</c>; "7" makes the contaminant label per dataset
    /// (<c>protein_datasets.is_contaminant</c>, <c>protein_index.n_datasets_contaminant</c>; aging 070 57f);
    /// "8" adds <c>samples.&lt;column&gt;_name</c> beside each term-only sample column (G74); "9" loads go's
    /// engine artefacts into <c>protein_localizations</c>, <c>organelle_term_categories</c> and
    /// <c>annotation_sources</c>, with a go coverage check and <c>catalog_tables.kind</c>
    /// <c>engine:go.annotate_groups</c> (G86); "10" adds <c>dataset_annotations</c>, the producer's NON-content
    /// manifest fields taken from the manifest the build is given, and hashes them into the catalog id (G84).
    /// Inside a <see cref="SchemaContract.Python0320"/> scope it stays "8", and go and the annotations are left
    /// out, so a parity build is still what Python 0.32.0 built. Same principle as
    /// <c>manifest.CONTENT_FIELDS</c> one level down -- an id moves when its own content moves, and not
    /// otherwise.</remarks>
    public static string CatalogVersion => SchemaContract.IsPython0320 ? "8" : "10";

    /// <summary>Whether this build loads go's artefacts: always, except when reproducing Python 0.32.0, which had
    /// no go engine.</summary>
    private static bool GoActive => !SchemaContract.IsPython0320;

    /// <summary>Whether this build writes <c>dataset_annotations</c> and hashes it (G84): always, except when
    /// reproducing Python 0.32.0, whose catalogs carried no manifest prose.</summary>
    private static bool AnnotationsActive => !SchemaContract.IsPython0320;

    /// <summary>The derived table holding each dataset's non-content manifest fields (G84).</summary>
    public const string AnnotationsTable = "dataset_annotations";

    /// <summary>The <see cref="DatasetEntry.NonContentFields"/> that <c>dataset_annotations</c> carries, in column
    /// order. Every non-content field but <c>raw</c> (the source row, the container of these) is here, and a test
    /// fails on one that is in neither, so adding a manifest field means deciding whether a reader sees it.</summary>
    public static readonly IReadOnlyList<string> AnnotationFields = ["status", "reason", "notes", "flags", "provenance_schema", "sdrf"];

    /// <summary>The package version a catalog id hashes and <c>catalog_meta.builder_version</c> records
    /// (Python's <c>__version__</c>): this assembly's informational version, without build metadata.</summary>
    public static readonly string PackageVersion = BundleWriter.PackageVersion;

    /// <summary><c>study.STUDY_BUNDLE_MANIFEST</c>, from the study writer.</summary>
    public const string StudyBundleManifest = StudyWriter.StudyBundleManifest;

    /// <summary><c>study.STUDY_DIR</c>, from the study writer: where study bundles live in the store.</summary>
    public const string StudyDir = StudyWriter.StudyDir;

    /// <summary>Provenance columns prepended to every table. <c>dataset_id</c> is re-derived from the bundle
    /// rather than trusted from the row, so a table without one (proteins, definitions) still gets it.</summary>
    public static readonly IReadOnlyList<string> ProvenanceColumns = ["dataset_id", "bundle_id"];

    /// <summary>Provenance columns prepended to every STUDY table.</summary>
    /// <remarks>Deliberately not <see cref="ProvenanceColumns"/>: <c>dataset_id</c> there is a fact about the
    /// bundle, and a study bundle spans datasets -- an <c>age_effect_meta</c> row is pooled across several by
    /// construction, so stating one would be false. Study tables that <i>are</i> per dataset keep their own
    /// <c>dataset_id</c> column, which is a fact about the row instead.</remarks>
    public static readonly IReadOnlyList<string> StudyProvenanceColumns = ["study_layer", "study_bundle_id"];

    /// <summary>Provenance columns prepended to every ENGINE table (G64).</summary>
    /// <remarks>Not <c>dataset_id</c>: an engine artefact is keyed on its inputs, not on a dataset -- one logs
    /// resolution serves every dataset that searched the same database -- so stating a dataset would be false.
    /// Join through the table's own keys.</remarks>
    public static readonly IReadOnlyList<string> EngineProvenanceColumns = ["engine", "artefact_id"];

    /// <summary>Tables the catalog builds itself. They are not in the schema: they describe the catalog, not
    /// the science, and a caller can tell them apart by this prefix.</summary>
    public static readonly IReadOnlyList<string> CatalogTables =
    [
        "catalog_meta",
        "catalog_bundles",
        "catalog_study_bundles",
        "catalog_engine_artefacts",
        "catalog_tables",
        "catalog_checks",
    ];

    /// <summary>Derived cross-dataset tables. These are the reason the catalog exists.</summary>
    public static readonly IReadOnlyList<string> DerivedTables =
    [
        "dataset_overview",
        "protein_index",
        "protein_datasets",
        "peptide_index",
        "search_modifications_placed",
        "dataset_databases",
        "protein_genes",
        AnnotationsTable,
    ];

    /// <summary>Views that apply the producing search engine's acceptance rule, so no caller has to restate
    /// it. <c>{t}</c> is the producer's threshold.</summary>
    /// <remarks>
    /// The rule is target, <c>q_value</c> at or below 1% <b>and</b> <c>q_value_notch</c> at or below 1% where
    /// there is one, <b>and</b> -- for PSMs -- a notch that actually resolved (aging thread 008, worth 12 rows
    /// out of 26,594 on PXD036557); for protein groups, anything not a decoy -- contaminants count -- with a
    /// group q-value at or below 1%. It is the same rule <c>sources.identifications.producer_counts</c> and
    /// <c>sources.quant.accepted_group_count</c> apply when a bundle reconciles itself.
    /// <para><b>It is an acceptance rule, not a reproduction of MetaMorpheus's own count.</b> aging published
    /// it as a recipe for rebuilding <c>aging:DEF-PSM-1PCT</c> from <c>AllPSMs.psmtsv</c>; it is exact on
    /// PXD036557 and six short of 183,029 on PXD032202 (aging thread 016). The canonical number stays the
    /// producer's <c>results.txt</c> summary line, which is what <c>metrics</c> holds and what the
    /// reconciliation compares against; these views are how the catalog selects <i>rows</i>, and on a dataset
    /// where the predicate is imperfect they will differ from the canonical count by a small margin and the
    /// <c>count_mismatch</c> finding will say by how much.</para>
    /// </remarks>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> AcceptedViews =
    [
        new("psms_1pct",
            "SELECT * FROM psms WHERE target_decoy = 'target' AND q_value <= {t} "
            + "AND (q_value_notch IS NULL OR q_value_notch <= {t}) "
            + "AND coalesce(notch_ambiguous, false) = false"),
        new("peptidoforms_1pct",
            "SELECT * FROM peptidoforms WHERE target_decoy = 'target' AND best_q_value <= {t} "
            + "AND (best_q_value_notch IS NULL OR best_q_value_notch <= {t})"),
        new("protein_groups_1pct",
            "SELECT * FROM protein_groups WHERE target_decoy <> 'decoy' AND q_value <= {t}"),
    ];

    /// <summary>Views that restate a table at a coarser grain than it is stored at, so a caller never has to.</summary>
    /// <remarks>
    /// <c>ptm_sites</c> is keyed on the search engine's own name for a modification, which is the grain the
    /// engine measured at and the only grain at which every site can be represented (aging 019 section 3). One
    /// chemistry can reach a dataset under two names -- <c>Phosphorylation on S</c> from the search's
    /// variable-mod list and <c>Phosphoserine on S</c> from a UniProt annotation are the same thing -- so the
    /// stored table splits a handful of sites that a UNIMOD-keyed table merged: 2 in PXD027318 and 3 in
    /// PXD032202, out of 20,044 and 13,527. This view puts them back together.
    /// <para>The split is finer, not wrong, and this view is the proof: grouping the stored table this way
    /// reproduces the UNIMOD-keyed table exactly. Storing the merged row instead would have been storing
    /// coarser than the measurement, which is the half of the grain rule (thread 018 section 3) that is easiest
    /// to break without noticing.</para>
    /// <para>A site whose chemistry has no UNIMOD term groups on its name instead, so two different unmapped
    /// chemistries on one residue stay two rows. <c>_site_key_name</c> forbids ':' in a name, which is what
    /// makes <c>coalesce</c> safe: a name can never be mistaken for an accession.</para>
    /// </remarks>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> GrainViews =
    [
        new("ptm_sites_by_chemistry",
            "SELECT dataset_id, protein_accession, position, residue, modification, "
            + "       coalesce(modification, modification_name) AS chemistry_key, "
            + "       count(*) AS n_names, list(DISTINCT modification_name) AS modification_names, "
            + "       sum(n_psms) AS n_psms, min(best_q_value) AS best_q_value, "
            + "       min(best_ambiguity_level) AS best_ambiguity_level, "
            + "       max(target_decoy) AS target_decoy "
            + "FROM ptm_sites "
            + "GROUP BY dataset_id, protein_accession, position, residue, modification, chemistry_key"),
    ];

    /// <summary>Point-lookup indexes. Built only where the column exists and is not a list: DuckDB's ART index
    /// does not accept list types, and the list columns are covered by the derived tables instead.</summary>
    public static readonly IReadOnlyList<(string Table, string Column)> IndexColumns =
    [
        ("psms", "run_id"),
        ("psms", "peptidoform"),
        ("psms", "base_sequence"),
        ("psms", "usi"),
        ("peptidoforms", "base_sequence"),
        ("peptidoforms", "peptidoform"),
        ("peptidoforms", "protein_group_id"),
        ("protein_groups", "protein_group_id"),
        ("proteins", "protein_accession"),
        ("proteins", "gene"),
        ("ptm_sites", "protein_accession"),
        ("ptm_sites", "modification"),
        // The queryable identity of a site whose chemistry has no UNIMOD term (aging 019 section 3).
        ("ptm_sites", "modification_name"),
        ("quant_values", "feature_id"),
        ("quant_values", "assay_id"),
        ("assays", "sample_id"),
        ("runs", "dataset_id"),
        ("metrics", "name"),
        ("findings", "code"),
        ("protein_index", "protein_accession"),
        ("protein_datasets", "protein_accession"),
        ("peptide_index", "base_sequence"),
    ];

    /// <summary>What the derived tables and views MEAN, for <c>datarepo mcp</c>'s describe tool.</summary>
    /// <remarks>
    /// These tables exist in no LinkML schema -- <c>build</c> invents them -- so the schema docs cannot describe
    /// them and for one release nothing did. That was not cosmetic. <c>protein_index</c> and the <c>_1pct</c>
    /// views are the tables <c>search</c> answers from and the ones an agent is steered to first, and an
    /// undocumented column gets read as whatever its name suggests: agents took <c>n_datasets_1pct</c> for
    /// "identified at 1% FDR" and reported 879 of 9,130 accession/dataset pairs as protein-level identifications
    /// that are not in <c>protein_groups_1pct</c> at all.
    /// <para>Kept here rather than in the MCP server so a description sits beside the SQL that produces it, and
    /// the tests fail if a derived table or view has no entry: adding one means writing down what it means.</para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, DerivedDoc> DerivedDocs = new Dictionary<string, DerivedDoc>(StringComparer.Ordinal)
    {
        ["dataset_overview"] = new(
            "One row per dataset: its axes and its headline counts. The counts are the numbers the "
            + "bundle reconciled against the producer's own summary, so they agree with the bundle "
            + "by construction -- where they disagree with the PRODUCER, a `count_mismatch` finding "
            + "says by how much, and the producer's number stays canonical.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["n_psms_1pct"] = "PSMs passing the producer's acceptance rule (`psms_1pct`).",
                ["n_psms_all"] = "Every PSM row, decoys and above-threshold matches included.",
                ["n_protein_groups_1pct"] = "Protein groups at 1% group-level FDR, contaminants included.",
                ["n_ptm_sites"] = "Rows in `ptm_sites`, at any ambiguity level, contaminants included.",
                ["n_open_findings"] = "Findings at severity warning or error. Read them before quoting "
                    + "any number from this row.",
            }),
        ["protein_index"] = new(
            "One row per accession ACROSS datasets, built from `proteins` -- which is the search's "
            + "protein DATABASE, not its answer. So this table contains decoys (roughly half of it) "
            + "and contaminants, and a row here is not evidence that anything was identified.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["protein_accession"] = "The accession as the search database carried it. A `DECOY_` "
                    + "prefix marks a reversed sequence: FDR machinery, never a protein.",
                ["n_datasets"] = "Datasets whose search DATABASE held this accession. Says nothing about "
                    + "identification.",
                ["n_datasets_1pct"] = "Datasets where the accession has evidence that passed acceptance "
                    + "-- an accepted protein GROUP **or** an accepted PEPTIDOFORM. It is deliberately the "
                    + "looser of the two and is NOT 'identified at 1% protein FDR': on aging's catalog 879 "
                    + "of 9,130 pairs counted here are absent from `protein_groups_1pct`, because a peptide "
                    + "passed where the protein group did not. For protein-level identification query "
                    + "`protein_groups_1pct` directly.",
                ["dataset_ids_1pct"] = "The datasets `n_datasets_1pct` counted, same caveat.",
                ["dataset_ids"] = "Every dataset whose database held it, same caveat as `n_datasets`.",
                ["best_q_value"] = "Lowest PROTEIN-GROUP q-value across datasets, from "
                    + "`protein_groups_1pct`. NULL where no accepted group contains the accession -- which "
                    + "is why a row can carry `n_datasets_1pct > 0` beside a NULL here. The two columns are "
                    + "measured at different levels and a row where they disagree is telling you so.",
                ["gene"] = "One dataset's gene symbol, chosen arbitrarily but deterministically. "
                    + "`protein_datasets` keeps the per-dataset truth.",
                ["organism"] = "As above, per-accession. NULL for contaminant and decoy entries: a "
                    + "contaminant panel is bovine, porcine and bacterial by design.",
                ["n_datasets_contaminant"] = "Datasets in which this accession is labelled a contaminant. "
                    + "There is no corpus-wide contaminant flag, because the label is per dataset: P02768 "
                    + "(human albumin) is a target in every human search and a contaminant in every rodent "
                    + "one. Compare with `n_datasets`, and use `protein_datasets.is_contaminant` per dataset.",
            }),
        ["protein_datasets"] = new(
            "One row per (dataset, accession): the per-dataset truth that `protein_index` "
            + "flattens. Built from `proteins`, so it too spans the whole search database.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["n_protein_groups"] = "Accepted protein groups in that dataset containing the "
                    + "accession. Zero means no group passed, not that it was absent from the search.",
                ["n_peptidoforms"] = "Accepted peptidoforms mapping to it in that dataset.",
                ["best_q_value"] = "Lowest accepted group q-value; NULL when no group passed.",
                ["is_contaminant"] = "The contaminant label IN THIS DATASET, from the database the "
                    + "search read the accession from. It is per dataset on purpose: human albumin is a "
                    + "target in a human search and a contaminant in a rodent one. NULL where the label "
                    + "could not be resolved.",
            }),
        ["peptide_index"] = new(
            "One row per base (unmodified) sequence across datasets, built from accepted "
            + "peptidoforms only. A sequence absent here was not accepted anywhere; it may still "
            + "appear in `peptidoforms` below threshold.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["n_peptidoforms"] = "Accepted peptidoform rows with this base sequence, summed over "
                    + "datasets -- so one sequence seen in three datasets with two modification states "
                    + "each counts six.",
            }),
        ["search_modifications_placed"] = new(
            "What the search actually PLACED on accepted peptidoforms, as against what it was "
            + "told to look for (`search_modifications_declared`). Derived from the peptidoforms, "
            + "never from `ptm_sites`, because `ptm_sites` is per resolved protein position and so "
            + "drops terminal and positionally-indeterminate placements -- a view built on it "
            + "reported N-terminal acetylation as never placed while 3,085 peptidoforms carried it.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tag"] = "The ProForma tag verbatim: a UNIMOD accession, a mass shift, or an "
                    + "unresolved name. Exactly one of the three columns below is non-null.",
                ["modification"] = "Set when the tag is a UNIMOD accession. NOT comparable row-for-row "
                    + "with `search_modifications_declared`, which names chemistries: one accession spans "
                    + "entries with different position rules.",
            }),
        ["dataset_databases"] = new(
            "One row per database each dataset's search read, from the bundle's own record "
            + "(`bundle.json`), with its sha256. `datasets.search_database` names only the proteome; "
            + "a search that also read an isoform or custom database has more rows here, and a join "
            + "through `datasets` alone would miss the proteins that came from them.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role"] = "`target` or `contaminant`, by MetaMorpheus's own rule (a path containing "
                    + "'contaminant' or 'CRAP'). Engines such as logs resolve target databases only.",
                ["sha256"] = "The database file's sha256 as the ingest hashed it. Engine tables key on it "
                    + "(`gene_resolutions.search_database_sha256`).",
            }),
        ["protein_genes"] = new(
            "Each dataset's TARGET proteins joined to logs' gene resolution for a database that "
            + "dataset searched: the join done once, correctly, so no caller rebuilds it. One row "
            + "per (dataset, protein, gene) -- a `multi_gene` protein has several and is never "
            + "reduced to one. Contaminant proteins are EXCLUDED, never mapped (logs 002 section 0: "
            + "bovine albumin mapped to human ALB is a lie about the sample), and so is a protein whose "
            + "`is_contaminant` is NULL, because unknown is not safe to map. Like `proteins`, it "
            + "spans the search DATABASE, so a row is not evidence of identification: join "
            + "`protein_groups_1pct` for that. A protein with no row here was not resolved (its "
            + "database has no artefact yet: see `catalog_checks` kind `engine-coverage`).",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["outcome"] = "logs' outcome for the protein: `resolved`, `multi_gene`, "
                    + "`off_primary_only`, `not_in_source` or `unrecognized_accession`. Only the first two "
                    + "carry a `gene_id`; count genes over those, and report the others as unresolved, "
                    + "never as absent.",
                ["gene_id"] = "Stable Ensembl gene id, or NULL when the outcome has none.",
                ["gene_symbol"] = "The pinned Ensembl release's symbol, for display. Group on `gene_id`.",
                ["artefact_id"] = "The engine artefact the row came from (`catalog_engine_artefacts`).",
            }),
        [AnnotationsTable] = new(
            "One row per dataset: what the producer's manifest says ABOUT it that is not identity -- its "
            + "notes, flags, status and reason. Taken from the manifest `datarepo build` was given, NOT from "
            + "the bundle, so a reworded note reaches the catalog at the next build without a re-ingest (G84). "
            + "These fields never reach a bundle's rows and are not in its content hash, so the bundle holds no "
            + "copy to disagree with; the catalog id hashes them instead. READ `notes` before quoting a "
            + "number for a dataset: the producer records there, in prose, what a reader must know, such as "
            + "runs excluded from analysis that are still in the bundle.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["in_manifest"] = "False when the build's manifest has no entry for the dataset. Every other "
                    + "column is then NULL, and the `catalog_checks` row of kind `manifest` names it: NULL "
                    + "there means not stated, never 'no notes'.",
                ["status"] = "The manifest's status for the dataset (a built dataset is normally `include`).",
                ["reason"] = "The producer's reason for the status, verbatim.",
                ["notes"] = "The producer's notes, verbatim: prose, never parsed by datarepo.",
                ["flags"] = "The producer's flags, verbatim. `mixed_enrichment` among them is also lifted "
                    + "into `datasets.enrichment_mixed` at ingest, which is the column to query.",
                ["provenance_schema"] = "The provenance schema the producer declared. The ingest reads the "
                    + "schema from provenance.json itself; this is the declaration.",
                ["sdrf"] = "The SDRF the producer declared. The ingest finds and hashes the SDRF under the "
                    + "run folder; this is the declaration.",
            }),
        ["psms_1pct"] = new(
            "`psms` with the producing search engine's acceptance rule applied once, so no caller "
            + "restates it: target, `q_value <= 0.01`, `q_value_notch <= 0.01` where there is one, "
            + "AND a notch that actually resolved (aging thread 008 -- worth 12 rows of 26,594 on "
            + "PXD036557 and not guessable). It is an ACCEPTANCE RULE, not a reproduction of the "
            + "producer's own count: exact on PXD036557 and six short of 183,029 on PXD032202, "
            + "where the `count_mismatch` finding says so. The producer's `results.txt` number, in "
            + "`metrics`, stays canonical.",
            new Dictionary<string, string>(StringComparer.Ordinal)),
        ["peptidoforms_1pct"] = new(
            "`peptidoforms` with the acceptance rule applied: target, `best_q_value <= 0.01` and "
            + "`best_q_value_notch <= 0.01` where there is one. **No notch-resolution clause** -- "
            + "that is a PSM rule, and applying it here cost exactly 3 rows on each of aging's two "
            + "larger datasets and produced a `count_mismatch` against a dataset that matched the "
            + "producer perfectly (fixed in ingester 0.8.0).",
            new Dictionary<string, string>(StringComparer.Ordinal)),
        ["protein_groups_1pct"] = new(
            "`protein_groups` at 1% group-level FDR: anything not a decoy -- CONTAMINANTS ARE "
            + "INCLUDED, because the producer counts them and they are real measurements of real "
            + "molecules. Filter `target_decoy` yourself if you want them out. This is the table to "
            + "query for protein-level identification; `protein_index.n_datasets_1pct` is looser.",
            new Dictionary<string, string>(StringComparer.Ordinal)),
        ["ptm_sites_by_chemistry"] = new(
            "`ptm_sites` regrouped so one chemistry reaching a dataset under two engine names "
            + "becomes one row ('Phosphorylation on S' and 'Phosphoserine on S' are the same thing). "
            + "The stored table is keyed on the engine's own name because that is the grain it was "
            + "measured at and the only one every site can be represented in; this view coarsens it "
            + "and reproduces the accession-keyed table exactly. **The key is still per residue and "
            + "position** -- grouping on `modification` alone merges chemistries that share an "
            + "accession (UNIMOD:35 is oxidation on M and hydroxylation on P and K).",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chemistry_key"] = "The UNIMOD accession, or the engine's name when there is none.",
                ["n_names"] = "How many engine names collapsed into this row. Greater than 1 is the case "
                    + "this view exists for.",
            }),
    };

    /// <summary>The key an engine table's rows are unique on, checked across every artefact loaded.</summary>
    /// <remarks>For logs it is logs' own key (D28 U15) with the gene set's sha256 beside its release, because
    /// two gene sets can share a release number and must not collapse.</remarks>
    public static readonly IReadOnlyDictionary<string, string[]> EngineKeys = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["gene_resolutions"] = ["search_database_sha256", "gene_set_sha256", "gene_set_release", "accession", "gene_id"],
    };

    /// <summary><c>samples</c> columns that hold an ontology TERM only, and the SDRF headers whose verbatim cell
    /// names the same thing. <c>characteristics[...]</c> is preferred to <c>factor value[...]</c> when both are
    /// present.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string[]>> SampleNameColumns =
    [
        new("sex_name", ["characteristics[sex]", "factor value[sex]"]),
        new("organism_part_name", ["characteristics[organism part]", "factor value[organism part]"]),
        new("cell_type_name", ["characteristics[cell type]", "factor value[cell type]"]),
        new("disease_name", ["characteristics[disease]", "factor value[disease]"]),
    ];

    /// <summary>Descriptions for columns <c>build</c> adds to a schema table (they are in no LinkML file).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DerivedColumnDocs =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["samples"] = SampleNameColumns.ToDictionary(
                kv => kv.Key,
                kv => $"The NAME the SDRF gives for `{RemoveSuffix(kv.Key, "_name")}`, as written (from "
                    + $"{string.Join(" or ", kv.Value)}), whether or not it carries an ontology term. Added by "
                    + "`datarepo build` from `sample_characteristics` (G74): the term column beside it is "
                    + "NULL whenever the SDRF names a value without a term, which is the common case, so "
                    + "read this column for what the deposit says. NULL here means no SDRF cell named one: "
                    + "from schema 0.0.12, a `sample_characteristics` row with `value_reserved` true says "
                    + "the SDRF was asked and answered `not available` (G42), and no row says it was never "
                    + "asked. Never mapped to a term here.",
                StringComparer.Ordinal),
        };

    // --- discovery and selection ---------------------------------------------------------------------

    /// <summary>Every bundle written for one dataset, oldest first.</summary>
    /// <remarks>A dataset can have more than one: re-ingesting changed inputs writes a new content hash beside
    /// the old one rather than overwriting it, which is the whole point of content addressing.
    /// <c>written_utc</c> is recorded to the second, so two bundles written in the same second would otherwise
    /// order by hash -- which is arbitrary, and would make <c>--latest</c> mean "highest hash". The manifest's
    /// own mtime breaks the tie with the resolution the clock in the file lacks.</remarks>
    public static List<BundleRef> DiscoverBundles(string store, string datasetId)
    {
        var root = System.IO.Path.Combine(store, datasetId);
        if (!Directory.Exists(root)) return [];
        var found = SortedChildren(root)
            .Where(c => Directory.Exists(c) && File.Exists(System.IO.Path.Combine(c, BundleWriter.ManifestName)))
            .Select(BundleRef.Load)
            .ToList();
        return OldestFirst(found, r => r.WrittenUtc, r => System.IO.Path.Combine(r.Path, BundleWriter.ManifestName), r => r.BundleId);
    }

    /// <summary>Choose exactly one bundle per dataset, and refuse to guess.</summary>
    /// <param name="manifest">The producing instance's manifest. It stays the contract here for the same reason
    /// it is the contract for ingest (D9): which datasets belong in the repository is the producer's recorded
    /// decision, not something inferred from what happens to be on disk.</param>
    /// <param name="accessions">Datasets to include. Each is looked up in the manifest, so one the producer
    /// marked <c>hold</c> or <c>exclude</c> is refused with their own reason.</param>
    /// <param name="store">Override the manifest's store.</param>
    /// <param name="pins"><c>{accession: bundle id or unique prefix}</c>, for pinning a release to exact bundles.</param>
    /// <param name="latest">When a dataset has several bundles and none is pinned, take the newest instead of
    /// refusing.</param>
    /// <param name="release">The release this catalog is for. Every dataset must then be pinned, and
    /// <paramref name="latest"/> is refused: a release that can silently pick up a later re-ingest is not a
    /// release (aging thread 011). Pinning is enforced here rather than merely defaulted, because the failure it
    /// prevents is invisible -- the catalog builds fine and says the wrong thing.</param>
    /// <exception cref="CatalogException">A dataset has no bundle, a pin matches none or several, or a dataset
    /// has several bundles with no pin and no <paramref name="latest"/>.</exception>
    /// <exception cref="DatasetExcludedException">The producer marked the dataset unfit to load.</exception>
    public static List<BundleRef> SelectBundles(
        Manifest manifest,
        IReadOnlyList<string> accessions,
        string? store = null,
        IReadOnlyDictionary<string, string>? pins = null,
        bool latest = false,
        string? release = null)
    {
        var root = !string.IsNullOrEmpty(store) ? store : manifest.Store;
        var rootText = SourcesPy.PathStr(root);
        pins ??= new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(release))
        {
            if (latest)
                throw new CatalogException(
                    $"--release {release} and --latest are mutually exclusive. A release names the "
                    + "exact bundles it was checked against; --latest would let it pick up a later "
                    + "re-ingest silently. Pin each dataset with --bundle <accession>=<id>.");
            var unpinned = accessions.Where(a => !pins.ContainsKey(a)).ToList();
            if (unpinned.Count > 0)
                throw new CatalogException(
                    $"--release {release} needs every dataset pinned, and "
                    + $"{string.Join(", ", unpinned.Order(SourcesPy.CodePointOrder))} {(unpinned.Count == 1 ? "is" : "are")} not. Add "
                    + $"--bundle {unpinned[0]}=<id>; `datarepo build` without --release will list the "
                    + "ids on offer.");
        }
        var chosen = new List<BundleRef>();
        foreach (var accession in accessions)
        {
            manifest.Dataset(accession);  // the producer's gate, and it raises for us
            var candidates = DiscoverBundles(root, accession);
            if (candidates.Count == 0)
                throw new CatalogException($"{accession} has no bundle under {rootText}. Run `datarepo ingest` for it first.");
            var pin = pins.GetValueOrDefault(accession);
            if (!string.IsNullOrEmpty(pin))
            {
                var matches = candidates.Where(c => c.BundleId.StartsWith(pin, StringComparison.Ordinal)).ToList();
                if (matches.Count != 1)
                {
                    var known = string.Join(", ", candidates.Select(c => c.BundleId));
                    throw new CatalogException(
                        $"{accession}: --bundle {pin} matches {matches.Count} of the bundles on disk ({known})");
                }
                chosen.Add(matches[0]);
            }
            else if (candidates.Count == 1)
                chosen.Add(candidates[0]);
            else if (latest)
                chosen.Add(candidates[^1]);
            else
            {
                var listing = string.Join("\n    ", candidates.Select(c => $"{c.BundleId}  written {c.WrittenUtc}"));
                throw new CatalogException(
                    $"{accession} has {candidates.Count} bundles and nothing says which one this "
                    + $"catalog is of:\n    {listing}\n  Pin it with --bundle "
                    + $"{accession}=<id>, or pass --latest to take the newest.");
            }
        }
        return chosen;
    }

    /// <summary>Every study bundle written for one layer, oldest first. Same ordering rule as a dataset's.</summary>
    public static List<StudyBundleRef> DiscoverStudyBundles(string store, string layer)
    {
        var root = System.IO.Path.Combine(store, StudyDir, layer);
        if (!Directory.Exists(root)) return [];
        var found = SortedChildren(root)
            .Where(c => Directory.Exists(c) && File.Exists(System.IO.Path.Combine(c, StudyBundleManifest)))
            .Select(StudyBundleRef.Load)
            .ToList();
        return OldestFirst(found, r => r.WrittenUtc, r => System.IO.Path.Combine(r.Path, StudyBundleManifest), r => r.BundleId);
    }

    /// <summary>Every layer the store holds a delivery for, so <c>build</c> can say what is on offer.</summary>
    public static Dictionary<string, List<StudyBundleRef>> AvailableStudyLayers(string store)
    {
        var root = System.IO.Path.Combine(store, StudyDir);
        var found = new Dictionary<string, List<StudyBundleRef>>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return found;
        foreach (var child in SortedChildren(root))
        {
            if (!Directory.Exists(child)) continue;
            var name = System.IO.Path.GetFileName(child);
            var bundles = DiscoverStudyBundles(store, name);
            if (bundles.Count > 0) found[name] = bundles;
        }
        return found;
    }

    /// <summary>Choose at most one study bundle per layer, and load none unless asked.</summary>
    /// <remarks><b>Study bundles are opt-in.</b> A build that names no layer gets the empty study tables it has
    /// had since 0.6.0, which is the honest default: a catalog that silently picked up whichever model results
    /// happened to be in the store would answer a benchmark question differently from one built an hour
    /// earlier, with nothing in either to say why.</remarks>
    /// <param name="store">The instance's bundle store; study bundles live under its <c>_study/</c> directory.</param>
    /// <param name="pins"><c>{layer: bundle id or unique prefix}</c>.</param>
    /// <param name="latest">Layers to take the newest delivery of.</param>
    /// <param name="release">The release this catalog is for. <paramref name="latest"/> is then refused, for
    /// the same reason D11 refuses it for datasets -- a release that can pick up a later re-fit is not a release.</param>
    /// <exception cref="CatalogException">A layer has no delivery, a pin matches none or several, a layer is both
    /// pinned and asked for latest, or <paramref name="latest"/> is combined with <paramref name="release"/>.</exception>
    public static List<StudyBundleRef> SelectStudyBundles(
        string store,
        IReadOnlyDictionary<string, string>? pins = null,
        IReadOnlyList<string>? latest = null,
        string? release = null)
    {
        pins ??= new Dictionary<string, string>();
        latest ??= [];
        var both = pins.Keys.Intersect(latest).Order(SourcesPy.CodePointOrder).ToList();
        if (both.Count > 0)
            throw new CatalogException(
                $"study layer {string.Join(", ", both)} is both pinned with --study and asked for with "
                + "--study-latest. Say which delivery this catalog is of, once.");
        if (!string.IsNullOrEmpty(release) && latest.Count > 0)
            throw new CatalogException(
                $"--release {release} and --study-latest are mutually exclusive. A release names the "
                + "exact deliveries it was checked against; --study-latest would let it pick up a later "
                + "re-fit silently. Pin each layer with --study <layer>=<id>.");
        var chosen = new List<StudyBundleRef>();
        foreach (var layer in pins.Keys.Union(latest).Distinct().Order(SourcesPy.CodePointOrder))
        {
            var candidates = DiscoverStudyBundles(store, layer);
            if (candidates.Count == 0)
                throw new CatalogException(
                    $"study layer '{layer}' has no delivery under {SourcesPy.PathStr(System.IO.Path.Combine(store, StudyDir, layer))}. "
                    + "Run `datarepo study` for it first.");
            if (!pins.TryGetValue(layer, out var pin))
            {
                chosen.Add(candidates[^1]);
                continue;
            }
            var matches = candidates.Where(c => c.BundleId.StartsWith(pin, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
            {
                var known = string.Join(", ", candidates.Select(c => c.BundleId));
                throw new CatalogException(
                    $"study layer '{layer}': --study {pin} matches {matches.Count} of the deliveries on disk ({known})");
            }
            chosen.Add(matches[0]);
        }
        return chosen;
    }

    /// <summary>Content hash of the bundles, the schema, the study layers and the builder.</summary>
    /// <remarks>The same bundles built by the same code give the same id, so <c>build</c> can tell a rebuild
    /// from a no-op, and a release can record which catalog its citations were checked against. The package
    /// version hashed is this build's (<see cref="PackageVersion"/>), so a C# catalog's id differs from the
    /// Python's for the same bundles, by design.</remarks>
    /// <param name="packageVersion">The version to hash in place of this build's: what a release build of that
    /// version would print for the same bundles. Only a test needs it (the tutorial's ids, docs/getting-started.md).</param>
    /// <param name="manifest">The manifest the build is given: its non-content fields for these datasets are part
    /// of the catalog (G84), so rewording a note moves the id. Null builds as though no dataset had an entry.</param>
    public static string CatalogId(
        IReadOnlyList<BundleRef> bundles,
        IReadOnlyList<StudyBundleRef>? studyBundles = null,
        IReadOnlyList<ArtefactRef>? artefacts = null,
        string? packageVersion = null,
        Manifest? manifest = null) =>
        CatalogIdOf(bundles, studyBundles, artefacts, packageVersion, Annotations(bundles, manifest));

    /// <summary>Each bundle's dataset with its non-content manifest fields from <paramref name="manifest"/>, in
    /// dataset order (G84).</summary>
    /// <remarks>The catalog takes these from the manifest it is BUILT with, not from the bundle, because they are
    /// not identity: rewording a note must not re-id a bundle (<see cref="DatasetEntry.NonContentFields"/>), and
    /// before this it therefore waited for the next re-ingest to reach anyone -- in fact it never reached the
    /// catalog at all, since no ingest wrote it into a row. A dataset the manifest does not list is kept, with
    /// <see cref="DatasetAnnotation.InManifest"/> false and every field null.</remarks>
    public static List<DatasetAnnotation> Annotations(IReadOnlyList<BundleRef> bundles, Manifest? manifest) =>
        bundles.OrderBy(r => r.DatasetId, SourcesPy.CodePointOrder).Select(r =>
        {
            if (manifest is null || !manifest.Datasets.TryGetValue(r.DatasetId, out var e))
                return new DatasetAnnotation(r.DatasetId, r.BundleId, false, null, null, null, null, null, null);
            return new DatasetAnnotation(r.DatasetId, r.BundleId, true, e.Status, DatasetEntry.Text(e.Reason),
                DatasetEntry.Text(e.Notes), e.Flags.ToList(), DatasetEntry.Text(e.ProvenanceSchema), DatasetEntry.Text(e.Sdrf));
        }).ToList();

    private static string CatalogIdOf(
        IReadOnlyList<BundleRef> bundles,
        IReadOnlyList<StudyBundleRef>? studyBundles,
        IReadOnlyList<ArtefactRef>? artefacts,
        string? packageVersion,
        IReadOnlyList<DatasetAnnotation> annotations)
    {
        var text = new StringBuilder();
        text.Append($"datarepo/{packageVersion ?? PackageVersion}\ncatalog/{CatalogVersion}\nschema/{SchemaContract.Version}\n");
        // A study layer's tables are part of what a catalog holds, so its version is part of the catalog's
        // identity. Without this, adding a column to `age_effect` would leave two different catalogs sharing an id.
        foreach (var (layer, version) in Tables.StudyVersions.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            text.Append($"study/{layer}/{version}\n");
        foreach (var r in bundles.OrderBy(r => r.DatasetId, SourcesPy.CodePointOrder).ThenBy(r => r.BundleId, SourcesPy.CodePointOrder))
            text.Append($"{r.DatasetId}\t{r.BundleId}\n");
        // And so are its ROWS: a catalog built with a delivery of age effects and one built without it answer
        // 46 of aging's benchmark questions differently.
        foreach (var r in (studyBundles ?? []).OrderBy(r => r.Layer, SourcesPy.CodePointOrder).ThenBy(r => r.BundleId, SourcesPy.CodePointOrder))
            text.Append($"study-bundle/{r.Layer}\t{r.BundleId}\n");
        // And an engine's: a catalog serving one gene resolution and one serving another answer a gene question
        // differently, so they must not share an id.
        foreach (var r in (artefacts ?? []).OrderBy(r => r.Engine, SourcesPy.CodePointOrder).ThenBy(r => r.ArtefactId, SourcesPy.CodePointOrder))
            text.Append($"engine/{r.Engine}\t{r.ArtefactId}\n");
        // And the producer's prose (G84): two catalogs of the same bundles that tell a reader different notes are
        // different catalogs. Not the bundle id again -- that is above.
        if (AnnotationsActive)
            foreach (var a in annotations.OrderBy(a => a.DatasetId, SourcesPy.CodePointOrder))
                text.Append($"annotation/{a.DatasetId}\t{a.Canonical()}\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary><c>{sha256: [dataset ids]}</c> for every target database the bundles searched, in first-seen order.</summary>
    private static List<KeyValuePair<string, List<string>>> TargetDatabases(IReadOnlyList<BundleRef> bundles)
    {
        var found = new List<KeyValuePair<string, List<string>>>();
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in bundles)
            foreach (var db in DatabasesRead(r))
            {
                if (ProteinDb.IsContaminantDatabase(SourcesPy.Str(db["path"]))) continue;
                var sha = SourcesPy.Str(db["sha256"]);
                if (!index.TryGetValue(sha, out var ids))
                {
                    index[sha] = ids = [];
                    found.Add(new(sha, ids));
                }
                ids.Add(r.DatasetId);
            }
        return found;
    }

    /// <summary>The engine artefacts that belong in a catalog of these bundles, and a coverage check each.</summary>
    /// <remarks>
    /// An artefact belongs when it was run on a database one of the bundles searched (design/RUNNER.md,
    /// "Catalog"). Artefacts load without being asked for, unlike study bundles, because their relevance is
    /// decided by their inputs rather than by a producer's delivery -- and the catalog id hashes every artefact
    /// loaded, so what was served is always named.
    /// <para>Refused rather than guessed: two artefacts of one engine for the same database (a re-run on a newer
    /// Ensembl release, say). Which resolution a catalog serves is the operator's decision; move the other out
    /// of <c>&lt;store&gt;/_engine/</c> to make it.</para>
    /// <para>A database with NO artefact is not a failure: the catalog builds without it, as an empty study
    /// layer does, and the coverage check -- which always passes -- says which databases lack one. Artefacts
    /// written against another schema version are skipped the same way and named.</para>
    /// </remarks>
    public static (List<ArtefactRef> Artefacts, List<CatalogCheck> Checks) SelectArtefacts(string store, IReadOnlyList<BundleRef> bundles)
    {
        var databases = TargetDatabases(bundles);
        var dbIndex = databases.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var chosen = new List<ArtefactRef>();
        var checks = new List<CatalogCheck>();
        foreach (var engine in Runner.EngineTables.Values.Distinct().Order(SourcesPy.CodePointOrder))
        {
            var byDb = new Dictionary<string, List<ArtefactRef>>(StringComparer.Ordinal);
            var stale = new List<string>();
            foreach (var r in Runner.DiscoverArtefacts(store, engine))
            {
                var sha = r.Inputs.GetValueOrDefault("search_database");
                if (sha is null || !dbIndex.ContainsKey(sha)) continue;
                if (r.SchemaVersion != SchemaContract.Version)
                {
                    stale.Add($"{r.ArtefactId} (schema {r.SchemaVersion})");
                    continue;
                }
                if (!byDb.TryGetValue(sha, out var list)) byDb[sha] = list = [];
                list.Add(r);
            }
            foreach (var (sha, refs) in byDb.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                if (refs.Count > 1)
                {
                    var listing = string.Join(", ", refs.Select(r => r.ArtefactId));
                    throw new CatalogException(
                        $"{engine}: {refs.Count} artefacts ({listing}) resolve the database {sha[..Math.Min(12, sha.Length)]}... "
                        + $"searched by {string.Join(", ", dbIndex[sha].Distinct().Order(SourcesPy.CodePointOrder))}. A catalog serves one. "
                        + $"Move the ones it should not serve out of {SourcesPy.PathStr(System.IO.Path.Combine(store, Runner.EngineDir, engine))}.");
                }
                chosen.Add(refs[0]);
            }
            var uncovered = databases.Select(kv => kv.Key).Where(sha => !byDb.ContainsKey(sha)).Order(SourcesPy.CodePointOrder).ToList();
            string? detail = null;
            if (uncovered.Count > 0)
                detail = "no artefact for " + string.Join("; ", uncovered.Select(sha =>
                    $"{sha[..Math.Min(12, sha.Length)]} ({string.Join(", ", dbIndex[sha].Distinct().Order(SourcesPy.CodePointOrder))})"));
            if (stale.Count > 0)
                detail = (detail is not null ? detail + "; " : "") + "skipped, other schema: " + string.Join(", ", stale);
            checks.Add(new CatalogCheck(
                $"{engine} coverage (databases with an artefact)", "engine-coverage", true,
                databases.Count - uncovered.Count, databases.Count, detail));
        }
        if (GoActive) SelectGoArtefacts(store, bundles, chosen, checks);
        return (chosen, checks);
    }

    /// <summary>What a go artefact for this bundle must have been run on, from the bundle's own <c>sources</c>:
    /// its protein-group file's sha256, and the sha256 of every TARGET database it searched.</summary>
    /// <remarks>The contaminant panel is not an annotation input (GoEngine's remarks say why), so it is not part
    /// of the match either.</remarks>
    public static (string? ProteinGroups, SortedSet<string> Databases) GoInputsOf(BundleRef bundle)
    {
        string? proteinGroups = null;
        var databases = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in SourcesPy.Get(bundle.Manifest, "sources") as IEnumerable<object?> ?? [])
        {
            if (item is not IReadOnlyDictionary<string, object?> source) continue;
            var role = SourcesPy.Str(SourcesPy.Get(source, "role", ""));
            var sha = SourcesPy.Get(source, "sha256") is string s ? s : null;
            if (role == "protein_group_quant") proteinGroups = sha;
            else if (role.StartsWith("protein_database:", StringComparison.Ordinal) && sha is not null
                     && !ProteinDb.IsContaminantDatabase(SourcesPy.Str(SourcesPy.Get(source, "path", ""))))
                databases.Add(sha);
        }
        return (proteinGroups, databases);
    }

    /// <summary>A go artefact belongs to a bundle when it annotated that bundle's protein-group file against that
    /// bundle's target databases, by sha256. Keyed on inputs, as logs' are: a re-ingest that reads the same files
    /// is served by the same artefact.</summary>
    public static bool GoArtefactMatches(ArtefactRef artefact, BundleRef bundle)
    {
        if (artefact.Engine != Runner.GoEngine) return false;
        var (proteinGroups, databases) = GoInputsOf(bundle);
        var inputs = artefact.Inputs;
        if (proteinGroups is null || inputs.GetValueOrDefault(Runner.GoProteinGroupsRole) != proteinGroups) return false;
        var annotated = inputs.Where(kv => kv.Key.StartsWith(Runner.GoDatabaseRolePrefix, StringComparison.Ordinal))
            .Select(kv => kv.Value).ToHashSet(StringComparer.Ordinal);
        return databases.Count > 0 && annotated.SetEquals(databases);
    }

    /// <summary>go's artefacts for these bundles (one per bundle at most) and the coverage check (G86).</summary>
    /// <remarks>As for logs: two artefacts for one bundle (another go.obo release, another category map) are
    /// refused rather than one picked, and a bundle with none builds without go rows and is named in the check,
    /// which always passes.</remarks>
    private static void SelectGoArtefacts(string store, IReadOnlyList<BundleRef> bundles, List<ArtefactRef> chosen, List<CatalogCheck> checks)
    {
        var byBundle = new Dictionary<string, List<ArtefactRef>>(StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var r in Runner.DiscoverArtefacts(store, Runner.GoEngine))
        {
            var matched = bundles.Where(b => GoArtefactMatches(r, b)).ToList();
            if (matched.Count == 0) continue;
            if (r.SchemaVersion != SchemaContract.Version)
            {
                stale.Add($"{r.ArtefactId} (schema {r.SchemaVersion})");
                continue;
            }
            foreach (var b in matched)
            {
                if (!byBundle.TryGetValue(b.BundleId, out var list)) byBundle[b.BundleId] = list = [];
                list.Add(r);
            }
        }
        var uncovered = new List<BundleRef>();
        foreach (var b in bundles.OrderBy(b => b.DatasetId, SourcesPy.CodePointOrder))
        {
            if (!byBundle.TryGetValue(b.BundleId, out var refs))
            {
                uncovered.Add(b);
                continue;
            }
            if (refs.Count > 1)
                throw new CatalogException(
                    $"{Runner.GoEngine}: {refs.Count} artefacts ({string.Join(", ", refs.Select(r => r.ArtefactId))}) annotate "
                    + $"{b.DatasetId} bundle {b.BundleId} (another go.obo release or category map). A catalog serves one. "
                    + $"Move the ones it should not serve out of {SourcesPy.PathStr(System.IO.Path.Combine(store, Runner.EngineDir, Runner.GoEngine))}.");
            if (!chosen.Any(c => c.Engine == refs[0].Engine && c.ArtefactId == refs[0].ArtefactId)) chosen.Add(refs[0]);
        }
        string? detail = null;
        if (uncovered.Count > 0)
            detail = "no artefact for " + string.Join("; ", uncovered.Select(b => $"{b.DatasetId} ({b.BundleId})"));
        if (stale.Count > 0)
            detail = (detail is not null ? detail + "; " : "") + "skipped, other schema: " + string.Join(", ", stale);
        checks.Add(new CatalogCheck(
            $"{Runner.GoEngine} coverage (bundles with an artefact)", "engine-coverage", true,
            bundles.Count - uncovered.Count, bundles.Count, detail));
    }

    /// <summary>Each bundle and the go artefact that annotated it, in dataset order.</summary>
    private static List<(BundleRef Bundle, ArtefactRef Artefact)> GoPairs(IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts) =>
        bundles.OrderBy(b => b.DatasetId, SourcesPy.CodePointOrder)
            .SelectMany(b => artefacts.Where(a => GoArtefactMatches(a, b)).Select(a => (b, a)))
            .ToList();

    /// <summary>go's rows into its three core tables, with the matched bundle's provenance columns.</summary>
    /// <returns><c>{table: total rows}</c> for the tables that received any.</returns>
    private static Dictionary<string, long> LoadGoRows(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!GoActive) return counts;
        foreach (var (bundle, artefact) in GoPairs(bundles, artefacts))
            foreach (var table in Runner.GoTables)
            {
                var path = artefact.TablePath(table);
                if (path is null) continue;
                Exec(con,
                    $"INSERT INTO \"{table}\" BY NAME SELECT {Quote(bundle.DatasetId)} AS dataset_id, "
                    + $"{Quote(bundle.BundleId)} AS bundle_id, * FROM read_parquet({Quote(AsPosix(path))})");
                counts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
            }
        return counts;
    }

    /// <summary>Every go artefact's rows are all there, under the bundle it was matched to.</summary>
    private static List<CatalogCheck> CheckGo(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var checks = new List<CatalogCheck>();
        if (!GoActive) return checks;
        foreach (var (bundle, artefact) in GoPairs(bundles, artefacts))
            foreach (var (table, expected) in artefact.RowCounts.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                var (observed, _) = CountAndExample(con, $"SELECT count(*), NULL FROM \"{table}\" WHERE bundle_id = ?", bundle.BundleId);
                checks.Add(new CatalogCheck($"{artefact.Engine}/{artefact.ArtefactId}/{table}", "row_count", observed == expected,
                    observed, expected, $"engine artefact {artefact.ArtefactId} for {bundle.DatasetId} bundle {bundle.BundleId}"));
            }
        return checks;
    }

    // --- the build -------------------------------------------------------------------------------------

    /// <summary>Load bundles into one DuckDB catalog at <paramref name="out"/>.</summary>
    /// <param name="bundles">Exactly one bundle per dataset, as <see cref="SelectBundles"/> returns.</param>
    /// <param name="out">The catalog file to write. It is replaced atomically: the build happens in a temporary
    /// file beside it, so a failed build leaves the previous catalog serving.</param>
    /// <param name="overwrite">Rebuild even when a catalog with this id is already there.</param>
    /// <param name="instance">The producing instance's name, recorded in <c>catalog_meta</c>.</param>
    /// <param name="notes">Anything else worth recording, e.g. the release this catalog was built for. Kept in
    /// insertion order, as Python's <c>json.dumps</c> of a dict.</param>
    /// <param name="studyBundles">At most one delivery per study layer, as <see cref="SelectStudyBundles"/>
    /// returns. Empty is the normal case and leaves the study tables empty, which is an answer rather than an
    /// omission.</param>
    /// <param name="artefacts">Engine artefacts to load, as <see cref="SelectArtefacts"/> returns.</param>
    /// <param name="engineChecks">The coverage checks <see cref="SelectArtefacts"/> returned, recorded with the rest.</param>
    /// <param name="manifest">The producing instance's manifest. Its non-content fields for each dataset go into
    /// <c>dataset_annotations</c> and the catalog id (G84); a dataset it does not list says so in
    /// <c>catalog_checks</c>.</param>
    /// <returns><see cref="CatalogResult.Skipped"/> is true when the catalog was already current.</returns>
    /// <exception cref="CatalogException">No bundles, two bundles for one dataset, a bundle written against a
    /// different schema version, or checks that fail. Nothing is moved into place in those cases.</exception>
    public static CatalogResult BuildCatalog(
        IReadOnlyList<BundleRef> bundles,
        string @out,
        bool overwrite = false,
        string? instance = null,
        IReadOnlyDictionary<string, object?>? notes = null,
        IReadOnlyList<StudyBundleRef>? studyBundles = null,
        IReadOnlyList<ArtefactRef>? artefacts = null,
        IReadOnlyList<CatalogCheck>? engineChecks = null,
        Manifest? manifest = null)
    {
        if (bundles.Count == 0) throw new CatalogException("no bundles to build a catalog from");

        var seen = new Dictionary<string, BundleRef>(StringComparer.Ordinal);
        foreach (var r in bundles)
        {
            if (seen.TryGetValue(r.DatasetId, out var prior))
                throw new CatalogException(
                    $"{r.DatasetId} appears twice ({prior.BundleId} and "
                    + $"{r.BundleId}). A catalog holds one bundle per dataset.");
            seen[r.DatasetId] = r;
            if (r.SchemaVersion != SchemaContract.Version)
                throw new CatalogException(
                    $"{r.DatasetId} bundle {r.BundleId} was written against schema "
                    + $"{r.SchemaVersion}, and this build writes schema {SchemaContract.Version}. Re-ingest "
                    + "it, or build with the ingester that wrote it.");
        }

        var study = (studyBundles ?? []).ToList();
        var seenLayers = new Dictionary<string, StudyBundleRef>(StringComparer.Ordinal);
        foreach (var r in study)
        {
            if (!Tables.Study.ContainsKey(r.Layer))
            {
                var layers = string.Join(", ", Tables.Study.Keys.Order(SourcesPy.CodePointOrder));
                throw new CatalogException(
                    $"study bundle {r.BundleId} delivers layer '{r.Layer}', which this build does "
                    + $"not carry. Layers available: {(layers.Length > 0 ? layers : "(none)")}.");
            }
            if (seenLayers.TryGetValue(r.Layer, out var prior))
                throw new CatalogException(
                    $"study layer '{r.Layer}' appears twice ({prior.BundleId} and "
                    + $"{r.BundleId}). A catalog holds one delivery per layer.");
            seenLayers[r.Layer] = r;
            if (r.LayerVersion != Tables.StudyVersions[r.Layer])
                throw new CatalogException(
                    $"study bundle {r.BundleId} was written against {r.Layer} "
                    + $"{r.LayerVersion}, and this build carries {Tables.StudyVersions[r.Layer]}. A "
                    + "column added between the two would land silently null, so re-run "
                    + "`datarepo study`, or build with the version that wrote it.");
        }

        var engines = (artefacts ?? []).ToList();
        foreach (var r in engines)
            if (r.SchemaVersion != SchemaContract.Version)
                throw new CatalogException(
                    $"engine artefact {r.ArtefactId} ({r.Engine}) was written against schema "
                    + $"{r.SchemaVersion}, and this build writes {SchemaContract.Version}. Re-run it.");

        var datasets = seen.Keys.Order(SourcesPy.CodePointOrder).ToList();
        var annotations = Annotations(bundles, manifest);
        var cid = CatalogIdOf(bundles, study, engines, null, annotations);
        if (!overwrite && ReadCatalogId(@out) == cid)
            return new CatalogResult
            {
                Path = @out, CatalogId = cid, Datasets = datasets, Bundles = bundles,
                StudyBundles = study, Artefacts = engines, Skipped = true,
            };

        var full = System.IO.Path.GetFullPath(@out);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var staging = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!, $".{System.IO.Path.GetFileName(full)}.{cid}.building");
        DeleteDatabase(staging);

        Dictionary<string, long> rowCounts;
        Dictionary<string, long> studyCounts;
        List<CatalogCheck> checks;
        int indexes;
        try
        {
            using (var con = Open(staging, readOnly: false))
            {
                rowCounts = LoadTables(con, bundles);
                BuildDerived(con);
                studyCounts = CreateStudyTables(con, study);
                foreach (var (k, v) in CreateEngineTables(con, engines)) rowCounts[k] = v;
                foreach (var (k, v) in LoadGoRows(con, bundles, engines)) rowCounts[k] = v;
                BuildEngineDerived(con, bundles);
                if (AnnotationsActive) CreateAnnotations(con, annotations);
                checks = [
                    .. CheckRowCounts(con, bundles),
                    .. CheckIntegrity(con),
                    .. CheckStudy(con, study),
                    .. CheckEngines(con, engines.Where(r => r.Engine != Runner.GoEngine).ToList()),
                    .. CheckGo(con, bundles, engines),
                    .. engineChecks ?? [],
                    .. AnnotationsActive ? [AnnotationCheck(annotations)] : Array.Empty<CatalogCheck>(),
                ];
                var failed = checks.Where(c => !c.Ok).ToList();
                if (failed.Count > 0)
                {
                    var detail = string.Join("\n  - ", failed.Select(c =>
                        $"{c.Name}: {PyOptional(c.Observed)} vs {PyOptional(c.Expected)}"
                        + (c.Detail is { Length: > 0 } ? $" ({c.Detail})" : "")));
                    throw new CatalogException(
                        "the bundles do not hold together as one catalog, so nothing was written:\n"
                        + $"  - {detail}");
                }
                indexes = BuildIndexes(con);
                WriteCatalogTables(con, bundles, rowCounts, checks, cid, instance, notes, study, studyCounts, engines);
            }
        }
        catch
        {
            DeleteDatabase(staging);
            throw;
        }

        File.Move(staging, full, overwrite: true);
        var counts = new Dictionary<string, long>(rowCounts, StringComparer.Ordinal);
        foreach (var (k, v) in studyCounts) if (v != 0) counts[k] = v;
        return new CatalogResult
        {
            Path = @out, CatalogId = cid, Datasets = datasets, Bundles = bundles, StudyBundles = study,
            Artefacts = engines, RowCounts = counts, Checks = checks, Indexes = indexes,
        };
    }

    /// <summary><c>dataset_annotations</c>: one row per dataset, from the build's manifest (G84).</summary>
    private static void CreateAnnotations(DuckDBConnection con, IReadOnlyList<DatasetAnnotation> annotations)
    {
        Exec(con, $@"
        CREATE TABLE ""{AnnotationsTable}"" (
            dataset_id VARCHAR, bundle_id VARCHAR, in_manifest BOOLEAN, status VARCHAR, reason VARCHAR,
            notes VARCHAR, flags VARCHAR[], provenance_schema VARCHAR, sdrf VARCHAR
        )
        ");
        foreach (var a in annotations)
            Exec(con, $"INSERT INTO \"{AnnotationsTable}\" VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                a.DatasetId, a.BundleId, a.InManifest, a.Status, a.Reason, a.Notes, a.Flags?.ToList(), a.ProvenanceSchema, a.Sdrf);
    }

    /// <summary>Which datasets the build's manifest annotated. It always passes: a dataset with no entry builds,
    /// and this row is where that is said.</summary>
    /// <remarks>There is no "manifest differs from the bundle" case to record: the non-content fields never reach a
    /// bundle's rows or its content hash, so the bundle holds no copy of them. The manifest is the only source.</remarks>
    private static CatalogCheck AnnotationCheck(IReadOnlyList<DatasetAnnotation> annotations)
    {
        var missing = annotations.Where(a => !a.InManifest).ToList();
        var detail = missing.Count == 0 ? null
            : "no entry in the build's manifest for "
              + string.Join(", ", missing.Select(a => $"{a.DatasetId} ({a.BundleId})"))
              + $"; its {AnnotationsTable} columns are NULL (a bundle holds no copy of the manifest's non-content fields)";
        return new CatalogCheck($"{AnnotationsTable} (datasets with a manifest entry)", "manifest", true,
            annotations.Count - missing.Count, annotations.Count, detail);
    }

    /// <summary>One bundle's contribution to a catalog table, with provenance columns prepended.</summary>
    /// <remarks><c>dataset_id</c> is dropped from the bundle's own columns and re-stated from the bundle
    /// manifest. They agree today; stating it once means they cannot disagree tomorrow, and it gives the tables
    /// that have no <c>dataset_id</c> of their own -- <c>proteins</c>, <c>definitions</c> -- the one thing a
    /// cross-dataset query needs from them.</remarks>
    private static string SelectFromParquet(BundleRef r, string table, string path)
    {
        var hasDatasetId = Tables.ByName[table].Columns.Any(c => c.Name == "dataset_id");
        var body = hasDatasetId ? "* EXCLUDE (dataset_id)" : "*";
        return $"SELECT {Quote(r.DatasetId)} AS dataset_id, {Quote(r.BundleId)} AS bundle_id, "
            + $"{body} FROM read_parquet({Quote(AsPosix(path))})";
    }

    /// <summary>Create a table the bundles do not fill, so every schema table exists to be queried.</summary>
    /// <remarks>A bundle omits a table it has no rows for, which is right for a file that is the product. A
    /// catalog that did the same would make "no such table" and "no such rows" indistinguishable to a caller
    /// that cannot see which bundles went in. The Python registers an empty Arrow table and copies it; the
    /// column types here are the ones DuckDB gives those Arrow types.</remarks>
    private static void CreateEmpty(DuckDBConnection con, string table, IEnumerable<(string Name, string Type)> prefix, IEnumerable<ColumnSpec> columns)
    {
        var all = prefix.Concat(columns.Select(c => (c.Name, SqlType(c))));
        Exec(con, $"CREATE TABLE \"{table}\" ({string.Join(", ", all.Select(c => $"\"{c.Item1}\" {c.Item2}"))})");
    }

    /// <summary>The DuckDB type of a column read from the Arrow type the schema gives it.</summary>
    private static string SqlType(ColumnSpec column)
    {
        var element = column.Type switch
        {
            ColumnType.String => "VARCHAR",
            ColumnType.Int64 => "BIGINT",
            ColumnType.Float64 => "DOUBLE",
            ColumnType.Boolean => "BOOLEAN",
            ColumnType.Date => "DATE",
            ColumnType.TimestampUtc => "TIMESTAMP WITH TIME ZONE",
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, null),
        };
        return column.IsList ? element + "[]" : element;
    }

    private static readonly (string, string)[] DatasetProvenance = [("dataset_id", "VARCHAR"), ("bundle_id", "VARCHAR")];
    private static readonly (string, string)[] StudyProvenance = [("study_layer", "VARCHAR"), ("study_bundle_id", "VARCHAR")];
    private static readonly (string, string)[] EngineProvenance = [("engine", "VARCHAR"), ("artefact_id", "VARCHAR")];

    /// <summary>Create every study layer's tables, filled from a delivery where there is one.</summary>
    /// <remarks>
    /// A study layer ADDS tables keyed on core identifiers and never alters a core table (U5), so it is created
    /// separately and a catalog is complete without one.
    /// <para><b>A table with no delivery is created empty, and that has always been the point.</b> aging's
    /// benchmark distinguishes NO_TABLE from EMPTY_TABLE, and the 46 questions that need an age effect scored the
    /// first until 0.6.0 created these. An empty table with the right columns says "this repository can hold
    /// that, and holds none"; a missing table says nothing at all. Study bundles are opt-in, so the empty case
    /// stays the default rather than the accident.</para>
    /// </remarks>
    /// <returns><c>{table: row count}</c> for every study table, including the empty ones.</returns>
    private static Dictionary<string, long> CreateStudyTables(DuckDBConnection con, IReadOnlyList<StudyBundleRef> studyBundles)
    {
        var byLayer = new Dictionary<string, StudyBundleRef>(StringComparer.Ordinal);
        foreach (var r in studyBundles) byLayer[r.Layer] = r;
        var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var taken = new HashSet<string>(Tables.ByName.Keys, StringComparer.Ordinal);
        taken.UnionWith(DerivedTables);
        taken.UnionWith(AcceptedViews.Select(kv => kv.Key));
        taken.UnionWith(GrainViews.Select(kv => kv.Key));
        foreach (var (layer, tables) in Tables.Study)
        {
            var r = byLayer.GetValueOrDefault(layer);
            foreach (var spec in tables)
            {
                var table = spec.Name;
                if (taken.Contains(table))
                    throw new CatalogException(
                        $"study layer '{layer}' declares a table named '{table}', which the core "
                        + "catalog already uses. A study layer adds tables; it never shadows one.");
                taken.Add(table);
                var path = r?.TablePath(table);
                if (path is null)
                {
                    CreateEmpty(con, table, StudyProvenance, spec.Columns);
                    rowCounts[table] = 0;
                    continue;
                }
                Exec(con,
                    $"CREATE TABLE \"{table}\" AS "
                    + $"SELECT {Quote(layer)} AS study_layer, "
                    + $"{Quote(r!.BundleId)} AS study_bundle_id, * "
                    + $"FROM read_parquet({Quote(AsPosix(path))})");
                rowCounts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
            }
        }
        return rowCounts;
    }

    /// <summary><c>dataset_databases</c> from the bundle manifests, then <c>protein_genes</c> over it (G64).</summary>
    private static void BuildEngineDerived(DuckDBConnection con, IReadOnlyList<BundleRef> bundles)
    {
        Exec(con,
            "CREATE TABLE dataset_databases (dataset_id VARCHAR, bundle_id VARCHAR, "
            + "database VARCHAR, role VARCHAR, sha256 VARCHAR, entries BIGINT)");
        foreach (var r in bundles)
            foreach (var db in DatabasesRead(r))
            {
                var path = SourcesPy.Str(db["path"]);
                Exec(con, "INSERT INTO dataset_databases VALUES (?, ?, ?, ?, ?, ?)",
                    r.DatasetId,
                    r.BundleId,
                    SourcesPy.PathName(path),
                    ProteinDb.IsContaminantDatabase(path) ? "contaminant" : "target",
                    SourcesPy.Str(db["sha256"]),
                    SourcesPy.Get(db, "entries"));
            }
        Exec(con, @"
        CREATE TABLE protein_genes AS
        SELECT p.dataset_id, p.bundle_id, p.protein_accession, dd.database,
               g.outcome, g.n_genes, g.gene_id, g.gene_symbol, g.gene_biotype, g.source,
               g.ensembl_xref_agrees, g.gene_set_release, g.search_database_sha256,
               g.definition_id, g.artefact_id
        FROM proteins p
        JOIN dataset_databases dd ON dd.dataset_id = p.dataset_id AND dd.role = 'target'
        JOIN gene_resolutions g
          ON g.accession = p.protein_accession AND g.search_database_sha256 = dd.sha256
        WHERE NOT p.is_contaminant AND p.protein_accession NOT LIKE 'DECOY\_%' ESCAPE '\'
        ");
    }

    /// <summary>Create every engine table, filled from the chosen artefacts or empty with the right columns.</summary>
    private static Dictionary<string, long> CreateEngineTables(DuckDBConnection con, IReadOnlyList<ArtefactRef> artefacts)
    {
        var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (table, engine) in Runner.EngineTables)
        {
            var parts = new List<string>();
            foreach (var r in artefacts)
            {
                if (r.Engine != engine) continue;
                var path = r.TablePath(table);
                if (path is null) continue;
                parts.Add($"SELECT {Quote(r.Engine)} AS engine, {Quote(r.ArtefactId)} AS artefact_id, * "
                    + $"FROM read_parquet({Quote(AsPosix(path))})");
            }
            if (parts.Count == 0)
            {
                CreateEmpty(con, table, EngineProvenance, Tables.ByName[table].Columns);
                rowCounts[table] = 0;
                continue;
            }
            var union = string.Join("\nUNION ALL BY NAME\n", parts);
            Exec(con, $"CREATE TABLE \"{table}\" AS\n{union}");
            rowCounts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
        }
        return rowCounts;
    }

    /// <summary>Each artefact's rows are all there, and an engine table's key is unique across artefacts.</summary>
    private static List<CatalogCheck> CheckEngines(DuckDBConnection con, IReadOnlyList<ArtefactRef> artefacts)
    {
        var checks = new List<CatalogCheck>();
        foreach (var r in artefacts.OrderBy(a => a.Engine, SourcesPy.CodePointOrder).ThenBy(a => a.ArtefactId, SourcesPy.CodePointOrder))
            foreach (var (table, expected) in r.RowCounts.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                var (observed, _) = CountAndExample(con, $"SELECT count(*), NULL FROM \"{table}\" WHERE artefact_id = ?", r.ArtefactId);
                checks.Add(new CatalogCheck($"{r.Engine}/{r.ArtefactId}/{table}", "row_count", observed == expected,
                    observed, expected, $"engine artefact {r.ArtefactId}"));
            }
        foreach (var (table, key) in EngineKeys.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
        {
            // A NULL gene_id is a real key value here (one outcome row per protein with no gene), so it is
            // compared as a value rather than skipped the way the core checks skip NULLs.
            var columns = string.Join(", ", key.Select(c => $"coalesce(CAST(\"{c}\" AS VARCHAR), '')"));
            var (duplicates, example) = CountAndExample(con,
                $"SELECT count(*), min(k) FROM (SELECT {columns}, min(accession) AS k "
                + $"FROM \"{table}\" GROUP BY {columns} HAVING count(*) > 1)");
            checks.Add(new CatalogCheck($"{table} ({string.Join(", ", key)})", "engine-unique", duplicates == 0,
                duplicates, 0, duplicates == 0 ? null : $"e.g. {PyStr(example)}"));
        }
        return checks;
    }

    /// <summary>Materialise every schema table as the union of the bundles that hold it.</summary>
    private static Dictionary<string, long> LoadTables(DuckDBConnection con, IReadOnlyList<BundleRef> bundles)
    {
        var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var spec in Tables.Core)
        {
            var table = spec.Name;
            if (Runner.EngineTables.ContainsKey(table)) continue;  // never in a bundle; filled from artefacts
            var parts = new List<string>();
            foreach (var r in bundles)
            {
                var path = r.TablePath(table);
                if (path is not null) parts.Add(SelectFromParquet(r, table, path));
            }
            if (parts.Count == 0)
            {
                CreateEmpty(con, table, DatasetProvenance, SchemaContract.Columns(spec).Where(c => c.Name != "dataset_id"));
                continue;
            }
            // BY NAME rather than positionally: the bundles all carry one schema version, and a union that lined
            // columns up by position would turn a violation of that into silent nonsense.
            var union = string.Join("\nUNION ALL BY NAME\n", parts);
            Exec(con, $"CREATE TABLE \"{table}\" AS\n{union}");
            rowCounts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
        }
        return rowCounts;
    }

    /// <summary>Give each term-only <c>samples</c> column a <c>&lt;column&gt;_name</c> beside it (G74).</summary>
    /// <remarks><c>samples.organism_part</c> holds an UBERON term and nothing else, so an SDRF that says
    /// <c>heart</c> with no accession -- 51 samples in 4 of aging's datasets -- read NULL there, and "which
    /// datasets are heart?" came back empty while the answer sat in <c>sample_characteristics</c>. The name is
    /// taken from that table, which is filled at ingest from every verbatim cell, and parsed with the
    /// ingester's own <c>Sdrf.Name</c>, so there is one reading of an SDRF cell, not two.</remarks>
    private static void AddSampleNames(DuckDBConnection con)
    {
        var headers = new Dictionary<string, (string Column, int Rank)>(StringComparer.Ordinal);
        foreach (var (column, hs) in SampleNameColumns)
            for (var rank = 0; rank < hs.Length; rank++) headers[hs[rank]] = (column, rank);
        var found = new Dictionary<(string, string), (int Rank, HashSet<string> Names)>();
        var order = new List<(string, string)>();
        var rows = Query(con,
            "SELECT sample_id, lower(trim(name)), value FROM sample_characteristics "
            + $"WHERE lower(trim(name)) IN ({string.Join(", ", headers.Keys.Select(_ => "?"))})",
            headers.Keys.Cast<object?>().ToArray());
        foreach (var row in rows)
        {
            var sampleId = (string)row[0]!;
            var (column, rank) = headers[(string)row[1]!];
            var name = Sdrf.Name((string?)row[2] ?? "");
            if (string.IsNullOrEmpty(name)) continue;
            var key = (sampleId, column);
            if (!found.TryGetValue(key, out var best))
            {
                found[key] = (rank, new HashSet<string>(StringComparer.Ordinal) { name });
                order.Add(key);
            }
            else if (rank < best.Rank)
                found[key] = (rank, new HashSet<string>(StringComparer.Ordinal) { name });
            else if (rank == best.Rank)
                best.Names.Add(name);
        }
        foreach (var (column, _) in SampleNameColumns)
            Exec(con, $"ALTER TABLE samples ADD COLUMN \"{column}\" VARCHAR");
        if (order.Count == 0) return;
        Exec(con, "CREATE TEMP TABLE _sample_names (sample_id VARCHAR, col VARCHAR, name VARCHAR)");
        foreach (var key in order)
            // Two different names under one header for one sample are both kept, never one picked.
            Exec(con, "INSERT INTO _sample_names VALUES (?, ?, ?)",
                key.Item1, key.Item2, string.Join("; ", found[key].Names.Order(SourcesPy.CodePointOrder)));
        foreach (var (column, _) in SampleNameColumns)
            Exec(con,
                $"UPDATE samples SET \"{column}\" = n.name FROM _sample_names n "
                + "WHERE n.sample_id = samples.sample_id AND n.col = ?",
                column);
        Exec(con, "DROP TABLE _sample_names");
    }

    /// <summary>The acceptance views and the cross-dataset tables.</summary>
    /// <remarks>The views come first: everything below counts through them, so the catalog's headline numbers
    /// are the same numbers the bundle reconciled against the producer's <c>results.txt</c>. A catalog whose
    /// front page disagreed with the bundle it was built from would be worse than no front page.</remarks>
    private static void BuildDerived(DuckDBConnection con)
    {
        AddSampleNames(con);
        var threshold = PyFormat.FloatRepr(Identifications.ProducerThreshold);
        foreach (var (name, body) in AcceptedViews)
            Exec(con, $"CREATE VIEW \"{name}\" AS {body.Replace("{t}", threshold)}");
        foreach (var (name, body) in GrainViews)
            Exec(con, $"CREATE VIEW \"{name}\" AS {body}");

        Exec(con, @"
        CREATE TABLE dataset_overview AS
        SELECT
            d.dataset_id,
            d.bundle_id,
            d.title,
            d.organisms,
            d.acquisition,
            d.quant_method,
            d.labelling,
            d.enrichment,
            d.enrichment_mixed,
            d.instrument_vendor,
            d.search_engine,
            d.search_engine_version,
            (SELECT count(*) FROM runs r WHERE r.dataset_id = d.dataset_id) AS n_runs,
            (SELECT count(*) FROM samples s WHERE s.dataset_id = d.dataset_id) AS n_samples,
            (SELECT count(*) FROM psms_1pct p WHERE p.dataset_id = d.dataset_id) AS n_psms_1pct,
            (SELECT count(*) FROM peptidoforms_1pct pf
              WHERE pf.dataset_id = d.dataset_id) AS n_peptidoforms_1pct,
            (SELECT count(*) FROM protein_groups_1pct pg
              WHERE pg.dataset_id = d.dataset_id) AS n_protein_groups_1pct,
            (SELECT count(*) FROM psms p WHERE p.dataset_id = d.dataset_id) AS n_psms_all,
            (SELECT count(*) FROM ptm_sites ps WHERE ps.dataset_id = d.dataset_id) AS n_ptm_sites,
            (SELECT count(*) FROM quant_values q
               JOIN assays a ON a.assay_id = q.assay_id
               JOIN runs r ON r.run_id = a.run_id
              WHERE r.dataset_id = d.dataset_id) AS n_quant_values,
            (SELECT count(*) FROM findings f
              WHERE f.dataset_id = d.dataset_id AND f.severity IN ('warning', 'error')) AS n_open_findings
        FROM datasets d
        ");

        Exec(con, @"
        CREATE TABLE protein_datasets AS
        WITH observed AS (
            SELECT DISTINCT dataset_id, protein_accession, is_contaminant FROM proteins
        ),
        in_groups AS (
            SELECT dataset_id, unnest(protein_accessions) AS protein_accession, protein_group_id,
                   q_value
            FROM protein_groups_1pct
        ),
        in_peptides AS (
            SELECT dataset_id, unnest(protein_accessions) AS protein_accession, peptidoform_id
            FROM peptidoforms_1pct
        )
        SELECT
            o.dataset_id,
            o.protein_accession,
            o.is_contaminant,
            count(DISTINCT g.protein_group_id) AS n_protein_groups,
            count(DISTINCT p.peptidoform_id)   AS n_peptidoforms,
            min(g.q_value)                     AS best_q_value
        FROM observed o
        LEFT JOIN in_groups g
               ON g.dataset_id = o.dataset_id AND g.protein_accession = o.protein_accession
        LEFT JOIN in_peptides p
               ON p.dataset_id = o.dataset_id AND p.protein_accession = o.protein_accession
        GROUP BY o.dataset_id, o.protein_accession, o.is_contaminant
        ");

        // Attributes are recorded per dataset in `proteins`, so the global row has to pick. max() is an
        // arbitrary choice made deterministic; `protein_datasets` keeps the per-dataset truth.
        //
        // `n_datasets` counts where the accession appears in the search output at all -- decoys and
        // sub-threshold matches included, because `proteins` is the search's protein list, not its answer.
        // `n_datasets_1pct` counts where it has evidence that passed. An agent asking "which datasets have
        // this protein" almost always means the second one, so both are here with names that say which is which.
        Exec(con, @"
        CREATE TABLE protein_index AS
        SELECT
            p.protein_accession,
            max(p.gene)                                AS gene,
            max(p.organism)                            AS organism,
            count(DISTINCT p.dataset_id) FILTER (WHERE p.is_contaminant) AS n_datasets_contaminant,
            count(DISTINCT p.dataset_id)               AS n_datasets,
            count(DISTINCT p.dataset_id) FILTER (
                WHERE d.n_protein_groups > 0 OR d.n_peptidoforms > 0)  AS n_datasets_1pct,
            list_sort(list(DISTINCT p.dataset_id))     AS dataset_ids,
            list_sort(list(DISTINCT p.dataset_id) FILTER (
                WHERE d.n_protein_groups > 0 OR d.n_peptidoforms > 0)) AS dataset_ids_1pct,
            min(d.best_q_value)                        AS best_q_value
        FROM proteins p
        LEFT JOIN protein_datasets d
               ON d.dataset_id = p.dataset_id AND d.protein_accession = p.protein_accession
        GROUP BY p.protein_accession
        ");

        // What the search actually PLACED, as against what it declared (aging 024 section 6).
        //
        // Derived from the peptidoforms, not from `ptm_sites`, and the choice is the whole point. `ptm_sites`
        // is per RESOLVED PROTEIN POSITION, so it drops every placement without one. A placed view built on it
        // would have reported that N-terminal acetylation was NEVER PLACED in any of the three datasets while
        // 3,085 peptidoforms carried it: a view trusted about ABSENCE must draw from the table that loses nothing.
        //
        // Grain: accession-or-mass, and it cannot name the chemistry. A ProForma tag carries a UNIMOD
        // accession or a mass, never an `IdWithMotif`, so `_declared` and `_placed` are NOT comparable row for
        // row. Do not resolve a position rule through an accession from here: one accession spans entries with
        // different rules (UNIMOD:34 covers Anywhere, N-terminal and C-terminal across ~20 entries).
        Exec(con, @"
        CREATE TABLE search_modifications_placed AS
        SELECT
            dataset_id,
            bundle_id,
            tag,
            CASE WHEN tag LIKE 'UNIMOD:%' THEN tag END                        AS modification,
            CASE WHEN regexp_matches(tag, '^[+-][0-9]')
                 THEN try_cast(tag AS DOUBLE) END                             AS mass_shift,
            CASE WHEN tag LIKE 'Info:%' THEN substr(tag, 6) END               AS unresolved_name,
            count(*)                                                          AS n_placements,
            count(DISTINCT peptidoform_id)                                    AS n_peptidoforms
        FROM (
            SELECT dataset_id, bundle_id, peptidoform_id,
                   unnest(regexp_extract_all(peptidoform, '\[([^\]]*)\]', 1)) AS tag
            FROM peptidoforms_1pct
        )
        GROUP BY dataset_id, bundle_id, tag
        ");

        Exec(con, @"
        CREATE TABLE peptide_index AS
        SELECT
            base_sequence,
            count(DISTINCT dataset_id)           AS n_datasets,
            list_sort(list(DISTINCT dataset_id)) AS dataset_ids,
            count(*)                             AS n_peptidoforms,
            min(best_q_value)                    AS best_q_value
        FROM peptidoforms_1pct
        GROUP BY base_sequence
        ");
    }

    private static string? ColumnDataType(DuckDBConnection con, string table, string column)
    {
        var rows = Query(con,
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = ? AND column_name = ?",
            table, column);
        return rows.Count > 0 ? (string?)rows[0][0] : null;
    }

    /// <summary>ART indexes on the point-lookup columns, skipping what a DuckDB index cannot hold.</summary>
    private static int BuildIndexes(DuckDBConnection con)
    {
        var built = 0;
        foreach (var (table, column) in IndexColumns)
        {
            var kind = ColumnDataType(con, table, column);
            if (kind is null || kind.EndsWith("[]", StringComparison.Ordinal) || kind.StartsWith("STRUCT", StringComparison.Ordinal))
                continue;
            Exec(con, $"CREATE INDEX \"ix_{table}_{column}\" ON \"{table}\" (\"{column}\")");
            built++;
        }
        return built;
    }

    /// <summary>Every table must hold exactly the rows its bundle manifest says it wrote.</summary>
    /// <remarks>The catalog's version of the ingester's reconciliation, and it catches the thing a content hash
    /// cannot: a bundle whose Parquet was truncated or edited after <c>bundle.json</c> was written.</remarks>
    private static List<CatalogCheck> CheckRowCounts(DuckDBConnection con, IReadOnlyList<BundleRef> bundles)
    {
        var checks = new List<CatalogCheck>();
        foreach (var r in bundles)
            foreach (var (table, expected) in r.RowCounts.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                if (!Tables.ByName.ContainsKey(table))
                {
                    checks.Add(new CatalogCheck($"{r.DatasetId}/{table}", "row_count", false, null, expected,
                        $"bundle {r.BundleId} claims a table no schema {SchemaContract.Version} knows"));
                    continue;
                }
                var (observed, _) = CountAndExample(con, $"SELECT count(*), NULL FROM \"{table}\" WHERE bundle_id = ?", r.BundleId);
                checks.Add(new CatalogCheck($"{r.DatasetId}/{table}", "row_count", observed == expected, observed, expected,
                    $"bundle {r.BundleId}"));
            }
        return checks;
    }

    /// <summary>The bundle's referential rules, re-run across the union.</summary>
    /// <remarks>Driven by the same <see cref="Integrity.References"/> / <see cref="Integrity.Identifiers"/> /
    /// <see cref="Integrity.FeatureTables"/> constants the ingester uses, so there is one list of what points
    /// at what. Every join is <i>within</i> a dataset: identifiers are namespaced per dataset by the ingester,
    /// and a bundle's rows may only resolve against their own.</remarks>
    private static List<CatalogCheck> CheckIntegrity(DuckDBConnection con)
    {
        var checks = new List<CatalogCheck>();
        foreach (var (table, column) in Integrity.Identifiers)
        {
            var (duplicates, example) = CountAndExample(con,
                $"SELECT count(*), min(k) FROM (SELECT \"{column}\" AS k FROM \"{table}\" "
                + $"WHERE \"{column}\" IS NOT NULL GROUP BY dataset_id, \"{column}\" HAVING count(*) > 1)");
            checks.Add(new CatalogCheck($"{table}.{column}", "unique", duplicates == 0, duplicates, 0,
                duplicates == 0 ? null : $"e.g. {PyStr(example)}"));
        }

        foreach (var (table, column, target, targetColumn) in Integrity.References)
        {
            var isList = column.EndsWith("[]", StringComparison.Ordinal);
            var name = isList ? column[..^2] : column;
            var source = isList
                ? $"SELECT DISTINCT dataset_id, unnest(\"{name}\") AS val FROM \"{table}\""
                : $"SELECT DISTINCT dataset_id, \"{name}\" AS val FROM \"{table}\" WHERE \"{name}\" IS NOT NULL";
            var (dangling, example) = CountAndExample(con,
                $"SELECT count(*), min(val) FROM ({source}) s WHERE val IS NOT NULL AND NOT EXISTS ("
                + $"SELECT 1 FROM \"{target}\" t WHERE t.dataset_id = s.dataset_id "
                + $"AND t.\"{targetColumn}\" = s.val)");
            checks.Add(new CatalogCheck($"{table}.{column} -> {target}.{targetColumn}", "reference", dangling == 0,
                dangling, 0, dangling == 0 ? null : $"e.g. {PyStr(example)}"));
        }

        var types = Query(con, "SELECT DISTINCT feature_type FROM quant_values WHERE feature_type IS NOT NULL")
            .Select(r => (string)r[0]!).ToList();
        foreach (var featureType in types.Order(SourcesPy.CodePointOrder))
        {
            if (!Integrity.FeatureTables.TryGetValue(featureType, out var target))
            {
                checks.Add(new CatalogCheck($"quant_values.feature_type '{featureType}'", "reference", false, null, 0,
                    "names no table"));
                continue;
            }
            var (dangling, example) = CountAndExample(con,
                "SELECT count(*), min(feature_id) FROM (SELECT DISTINCT dataset_id, feature_id "
                + "FROM quant_values WHERE feature_type = ?) s WHERE NOT EXISTS ("
                + $"SELECT 1 FROM \"{target.Table}\" t WHERE t.dataset_id = s.dataset_id "
                + $"AND t.\"{target.Column}\" = s.feature_id)",
                featureType);
            checks.Add(new CatalogCheck($"quant_values.feature_id ({featureType}) -> {target.Table}.{target.Column}",
                "reference", dangling == 0, dangling, 0, dangling == 0 ? null : $"e.g. {PyStr(example)}"));
        }
        return checks;
    }

    /// <summary>A loaded study layer's own rules: its keys are unique and its joins into the core resolve.</summary>
    /// <remarks>
    /// Only runs for layers a delivery was loaded for. An empty study table has nothing to check and the checks
    /// would pass vacuously, which would put a reassuring line in <c>catalog_checks</c> about something nobody
    /// delivered.
    /// <para>The reference checks are NOT scoped by <c>dataset_id</c> the way the core's are. A study layer's
    /// rows are not namespaced per dataset by us -- the producer chose their identifiers -- and
    /// <c>age_effect_meta</c> is pooled across datasets by construction, so a per-dataset join would be wrong for
    /// the table the layer exists to produce.</para>
    /// <para><c>build</c> refuses on a failure rather than dropping the rows. An age effect naming a dataset this
    /// catalog does not hold is not a row to quietly skip: section D's question would come back with a smaller
    /// answer than the delivery supports, and nothing in the catalog would say so.</para>
    /// </remarks>
    private static List<CatalogCheck> CheckStudy(DuckDBConnection con, IReadOnlyList<StudyBundleRef> studyBundles)
    {
        var checks = new List<CatalogCheck>();
        foreach (var r in studyBundles.OrderBy(s => s.Layer, SourcesPy.CodePointOrder))
        {
            var layer = r.Layer;
            var layerTables = Tables.Study[layer].Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var keys = Integrity.StudyCompositeIdentifiers.GetValueOrDefault(layer) ?? new Dictionary<string, string[]>();
            foreach (var (table, key) in keys.OrderBy(k => k.Key, SourcesPy.CodePointOrder))
            {
                if (!layerTables.Contains(table)) continue;
                var columns = string.Join(", ", key.Select(c => $"\"{c}\""));
                var notNull = string.Join(" AND ", key.Select(c => $"\"{c}\" IS NOT NULL"));
                var (duplicates, example) = CountAndExample(con,
                    $"SELECT count(*), min(k) FROM (SELECT {columns}, min("
                    + $"CAST(\"{key[0]}\" AS VARCHAR)) AS k FROM \"{table}\" WHERE {notNull} "
                    + $"GROUP BY {columns} HAVING count(*) > 1)");
                checks.Add(new CatalogCheck($"{layer}.{table} ({string.Join(", ", key)})", "study-unique", duplicates == 0,
                    duplicates, 0, duplicates == 0 ? null : $"e.g. {PyStr(example)}"));
            }
            foreach (var (table, column, target, targetColumn) in Integrity.StudyReferences.GetValueOrDefault(layer) ?? [])
            {
                if (!layerTables.Contains(table)) continue;
                var (dangling, example) = CountAndExample(con,
                    $"SELECT count(*), min(val) FROM (SELECT DISTINCT \"{column}\" AS val "
                    + $"FROM \"{table}\" WHERE \"{column}\" IS NOT NULL) s WHERE NOT EXISTS ("
                    + $"SELECT 1 FROM \"{target}\" t WHERE t.\"{targetColumn}\" = s.val)");
                checks.Add(new CatalogCheck($"{layer}.{table}.{column} -> {target}.{targetColumn}", "study-reference",
                    dangling == 0, dangling, 0, dangling == 0 ? null : $"e.g. {PyStr(example)}"));
            }
        }
        return checks;
    }

    /// <summary>The catalog's account of itself: what went in, what came out, and what was checked.</summary>
    private static void WriteCatalogTables(
        DuckDBConnection con,
        IReadOnlyList<BundleRef> bundles,
        IReadOnlyDictionary<string, long> rowCounts,
        IReadOnlyList<CatalogCheck> checks,
        string cid,
        string? instance,
        IReadOnlyDictionary<string, object?>? notes,
        IReadOnlyList<StudyBundleRef> studyBundles,
        IReadOnlyDictionary<string, long> studyCounts,
        IReadOnlyList<ArtefactRef> artefacts)
    {
        Exec(con, @"
        CREATE TABLE catalog_meta (
            catalog_id VARCHAR, catalog_version VARCHAR, schema_version VARCHAR,
            qpx_version VARCHAR, builder VARCHAR, builder_version VARCHAR,
            built_utc VARCHAR, instance VARCHAR, n_datasets BIGINT, notes JSON
        )
        ");
        Exec(con, "INSERT INTO catalog_meta VALUES (?, ?, ?, ?, 'datarepo', ?, ?, ?, ?, ?)",
            cid,
            CatalogVersion,
            SchemaContract.Version,
            BundleWriter.QpxVersion,
            PackageVersion,
            // A string, like the bundle's `written_utc`; Python's isoformat(timespec="seconds").
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
            instance,
            (long)bundles.Count,
            PyFormat.Json(notes ?? new Dictionary<string, object?>(), sortKeys: false, ensureAscii: true));

        Exec(con, @"
        CREATE TABLE catalog_bundles (
            dataset_id VARCHAR, bundle_id VARCHAR, path VARCHAR, written_utc VARCHAR,
            schema_version VARCHAR, ingester_version VARCHAR, reconciliation_ok BOOLEAN,
            reconciliation_failed VARCHAR[]
        )
        ");
        foreach (var r in bundles)
        {
            var failed = new List<string>();
            if (SourcesPy.Get(r.Manifest, "reconciliation") is List<object?> recon && recon.Count > 0)
                foreach (var c in recon)
                {
                    var check = c as IReadOnlyDictionary<string, object?> ?? SourcesPy.EmptyDict;
                    if (!SourcesPy.Truthy(SourcesPy.Get(check, "ok"))) failed.Add(SourcesPy.Str(SourcesPy.Get(check, "name")));
                }
            var ingester = SourcesPy.Get(r.Manifest, "ingester") as IReadOnlyDictionary<string, object?>;
            Exec(con, "INSERT INTO catalog_bundles VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                r.DatasetId,
                r.BundleId,
                AsPosix(r.Path),
                r.WrittenUtc,
                r.SchemaVersion,
                SourcesPy.Str(SourcesPy.Get(ingester is { Count: > 0 } ? ingester : SourcesPy.EmptyDict, "version", "?")),
                failed.Count == 0,
                failed);
        }

        // A separate table from `catalog_bundles`, because the two are separate objects: a search bundle is one
        // dataset's evidence, a study bundle is one layer's model results over however many datasets.
        Exec(con, @"
        CREATE TABLE catalog_study_bundles (
            layer VARCHAR, bundle_id VARCHAR, layer_version VARCHAR, path VARCHAR,
            written_utc VARCHAR, schema_version VARCHAR, instance VARCHAR, delivery VARCHAR
        )
        ");
        foreach (var r in studyBundles)
            Exec(con, "INSERT INTO catalog_study_bundles VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                r.Layer,
                r.BundleId,
                r.LayerVersion,
                AsPosix(r.Path),
                r.WrittenUtc,
                r.SchemaVersion,
                SourcesPy.Get(r.Manifest, "instance"),
                SourcesPy.Get(r.Manifest, "delivery"));

        // One row per engine artefact loaded, with the record a stranger needs to reproduce it: which release
        // ran, on which inputs, and from which datarepo install.
        Exec(con, @"
        CREATE TABLE catalog_engine_artefacts (
            engine VARCHAR, artefact_id VARCHAR, definition_id VARCHAR, path VARCHAR,
            written_utc VARCHAR, schema_version VARCHAR, runner_version VARCHAR,
            release JSON, inputs JSON, datarepo_install JSON, engine_summary JSON
        )
        ");
        foreach (var r in artefacts)
        {
            var rec = r.Record;
            Exec(con, "INSERT INTO catalog_engine_artefacts VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                r.Engine,
                r.ArtefactId,
                SourcesPy.Get(rec, "definition_id"),
                AsPosix(r.Path),
                SourcesPy.Get(rec, "written_utc"),
                r.SchemaVersion,
                SourcesPy.Get(rec, "runner_version"),
                JsonOrEmpty(SourcesPy.Get(rec, "release")),
                JsonOrEmpty(SourcesPy.Get(rec, "inputs")),
                JsonOrEmpty(SourcesPy.Get(rec, "datarepo_install")),
                JsonOrEmpty(SourcesPy.Get(rec, "engine_summary")));
        }

        Exec(con, "CREATE TABLE catalog_tables (table_name VARCHAR, rows BIGINT, kind VARCHAR)");
        foreach (var spec in Tables.Core)
        {
            var name = spec.Name;
            var kind = Runner.EngineTables.TryGetValue(name, out var engine) ? $"engine:{engine}"
                : GoActive && Runner.GoTables.Contains(name) ? $"engine:{Runner.GoEngine}"
                : "bundle";
            Exec(con, "INSERT INTO catalog_tables VALUES (?, ?, ?)", name, rowCounts.GetValueOrDefault(name, 0), kind);
        }
        var views = AcceptedViews.Select(kv => kv.Key).Concat(GrainViews.Select(kv => kv.Key)).ToHashSet(StringComparer.Ordinal);
        foreach (var name in DerivedTables.Concat(AcceptedViews.Select(kv => kv.Key)).Concat(GrainViews.Select(kv => kv.Key)))
        {
            if (name == AnnotationsTable && !AnnotationsActive) continue;
            var rows = Count(con, $"SELECT count(*) FROM \"{name}\"");
            Exec(con, "INSERT INTO catalog_tables VALUES (?, ?, ?)", name, rows, views.Contains(name) ? "view" : "derived");
        }
        foreach (var (layer, tables) in Tables.Study)
            foreach (var spec in tables)
                Exec(con, "INSERT INTO catalog_tables VALUES (?, ?, ?)", spec.Name, studyCounts.GetValueOrDefault(spec.Name, 0), $"study:{layer}");

        Exec(con,
            "CREATE TABLE catalog_checks (name VARCHAR, kind VARCHAR, ok BOOLEAN, "
            + "observed BIGINT, expected BIGINT, detail VARCHAR)");
        foreach (var check in checks)
            Exec(con, "INSERT INTO catalog_checks VALUES (?, ?, ?, ?, ?, ?)",
                check.Name, check.Kind, check.Ok, check.Observed, check.Expected, check.Detail);
    }

    // --- reading a built catalog -----------------------------------------------------------------------

    /// <summary>The id of an existing catalog, or null if there is no readable one at <paramref name="path"/>.</summary>
    public static string? ReadCatalogId(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var con = Open(path, readOnly: true);
            var rows = Query(con, "SELECT catalog_id FROM catalog_meta");
            return rows.Count == 0 ? null : PyStr(rows[0][0]);
        }
        catch (Exception)  // an unreadable or foreign file is simply not our catalog
        {
            return null;
        }
    }

    /// <summary>Read a built catalog's own account of itself, without loading any science.</summary>
    /// <returns>Keys <c>path</c>, <c>meta</c>, <c>bundles</c>, <c>study_bundles</c>, <c>tables</c> and
    /// <c>checks</c>, each row a dictionary of plain values, as the Python returns.</returns>
    /// <exception cref="CatalogException">There is no catalog at <paramref name="path"/>.</exception>
    public static Dictionary<string, object?> DescribeCatalog(string path)
    {
        if (!File.Exists(path)) throw new CatalogException($"no catalog at {SourcesPy.PathStr(path)}");
        using var con = Open(path, readOnly: true);
        List<Dictionary<string, object?>> meta;
        try
        {
            meta = Dicts(con, "SELECT * FROM catalog_meta");
        }
        catch (DuckDB.NET.Data.DuckDBException exc)
        {
            throw new CatalogException($"{SourcesPy.PathStr(path)} is not a datarepo catalog: {exc.Message}");
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = SourcesPy.PathStr(path),
            ["meta"] = meta.Count > 0 ? meta[0] : new Dictionary<string, object?>(StringComparer.Ordinal),
            ["bundles"] = Dicts(con, "SELECT * FROM catalog_bundles ORDER BY dataset_id"),
            // A catalog from before 0.7.0 has no study bundles table and is still a catalog, so an absent one
            // reads as "no delivery" rather than as a broken file.
            ["study_bundles"] = RowsIfPresent(con, "catalog_study_bundles", "layer"),
            ["tables"] = Dicts(con, "SELECT * FROM catalog_tables ORDER BY kind, table_name"),
            ["checks"] = Dicts(con, "SELECT * FROM catalog_checks ORDER BY ok, kind, name"),
        };
    }

    private static List<Dictionary<string, object?>> RowsIfPresent(DuckDBConnection con, string table, string order)
    {
        try
        {
            return Dicts(con, $"SELECT * FROM \"{table}\" ORDER BY \"{order}\"");
        }
        catch (DuckDB.NET.Data.DuckDBException)
        {
            return [];
        }
    }

    /// <summary>Run one read-only query against a catalog.</summary>
    /// <remarks>The connection is opened read-only, so a query that tries to write is refused by DuckDB rather
    /// than by a rule of ours.</remarks>
    /// <param name="path">The catalog file.</param>
    /// <param name="sql">One SQL statement.</param>
    /// <param name="limit">Wrap the statement in a <c>LIMIT</c> so an exploratory query cannot print a million rows.</param>
    /// <returns><c>(column names, rows)</c>, each cell a plain value as Python's DuckDB would give it.</returns>
    /// <exception cref="CatalogException">There is no catalog at <paramref name="path"/>, or the query is not
    /// valid against it.</exception>
    public static (List<string> Columns, List<object?[]> Rows) RunQuery(string path, string sql, int? limit = null)
    {
        if (!File.Exists(path)) throw new CatalogException($"no catalog at {SourcesPy.PathStr(path)}");
        var statement = sql.Trim().TrimEnd(';');
        if (limit is not null) statement = $"SELECT * FROM ({statement}) LIMIT {limit.Value}";
        try
        {
            using var con = Open(path, readOnly: true);
            using var cmd = con.CreateCommand();
            cmd.CommandText = statement;
            using var reader = cmd.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add(ReadRow(reader));
            return (columns, rows);
        }
        catch (DuckDB.NET.Data.DuckDBException exc)
        {
            throw new CatalogException(exc.Message);
        }
    }

    /// <summary>Render query results as an aligned table, TSV, or JSON lines.</summary>
    public static string FormatRows(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows, string fmt = "table")
    {
        var list = rows.Select(r => r.ToList()).ToList();
        if (fmt == "json")
            return string.Join("\n", list.Select(r =>
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < columns.Count && i < r.Count; i++) d[columns[i]] = JsonSafe(r[i]);
                return PyFormat.Json(d, sortKeys: false, ensureAscii: true);
            }));
        var cells = list.Select(r => r.Select(v => v is null ? "" : PyStr(v)).ToList()).ToList();
        if (fmt == "tsv")
            return string.Join("\n", new[] { string.Join("\t", columns) }.Concat(cells.Select(c => string.Join("\t", c))));
        var widths = columns.Select(PyLen).ToArray();
        foreach (var row in cells)
            for (var i = 0; i < row.Count && i < widths.Length; i++) widths[i] = Math.Max(widths[i], PyLen(row[i]));
        var lines = new List<string>
        {
            string.Join("  ", columns.Select((c, i) => LJust(c, widths[i]))),
            string.Join("  ", widths.Select(w => new string('-', w))),
        };
        lines.AddRange(cells.Select(row => string.Join("  ", row.Select((c, i) => LJust(c, widths[i])))));
        return string.Join("\n", lines);
    }

    // --- helpers ---------------------------------------------------------------------------------------

    /// <summary>A SQL string literal. Paths on Windows go in here, so the escaping is not decorative.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary><c>Path.as_posix()</c> on this platform.</summary>
    internal static string AsPosix(string path)
    {
        var text = SourcesPy.PathStr(path);
        return OperatingSystem.IsWindows() ? text.Replace('\\', '/') : text;
    }

    /// <summary><c>sorted(path.iterdir())</c>: Python orders Windows paths case-insensitively, POSIX ones by
    /// code point.</summary>
    internal static List<string> SortedChildren(string directory)
    {
        var children = Directory.EnumerateFileSystemEntries(directory).ToList();
        if (OperatingSystem.IsWindows())
            children.Sort((a, b) => SourcesPy.CodePointOrder.Compare(a.ToLowerInvariant(), b.ToLowerInvariant()));
        else
            children.Sort(SourcesPy.CodePointOrder);
        return children;
    }

    private static List<T> OldestFirst<T>(List<T> found, Func<T, string> written, Func<T, string> manifest, Func<T, string> id) =>
        found.Select(r => (Ref: r, Written: written(r), Mtime: File.GetLastWriteTimeUtc(manifest(r)).Ticks, Id: id(r)))
            .OrderBy(x => x.Written, SourcesPy.CodePointOrder)
            .ThenBy(x => x.Mtime)
            .ThenBy(x => x.Id, SourcesPy.CodePointOrder)
            .Select(x => x.Ref)
            .ToList();

    /// <summary><c>json.loads(path.read_text(encoding="utf-8"))</c> of a JSON object, as plain values.</summary>
    public static IReadOnlyDictionary<string, object?> ReadJsonObject(string path, Func<string, Exception>? error = null)
    {
        error ??= msg => new CatalogException(msg);
        object? value;
        try
        {
            value = SourcesPy.LoadJson(path);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or DecoderFallbackException)
        {
            throw error($"cannot read {SourcesPy.PathStr(path)}: {exc.Message}");
        }
        return value as IReadOnlyDictionary<string, object?>
            ?? throw error($"cannot read {SourcesPy.PathStr(path)}: not a JSON object");
    }

    /// <summary><c>{str(k): f(v) for k, v in (value or {}).items()}</c>.</summary>
    internal static IReadOnlyDictionary<string, T> StrMap<T>(object? value, Func<object?, T> convert)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        if (value is IReadOnlyDictionary<string, object?> d)
            foreach (var (k, v) in d) result[k] = convert(v);
        return result;
    }

    /// <summary>Python's <c>int(v)</c> for a JSON value.</summary>
    internal static long PyInt(object? value) => value switch
    {
        long l => l,
        int i => i,
        double d => (long)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s => long.Parse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"int() argument must be a string or a number, not '{SourcesPy.TypeName(value)}'"),
    };

    /// <summary><c>(ref.manifest.get("protein_databases") or {}).get("read") or []</c>.</summary>
    private static IEnumerable<IReadOnlyDictionary<string, object?>> DatabasesRead(BundleRef r)
    {
        var dbs = SourcesPy.Get(r.Manifest, "protein_databases") as IReadOnlyDictionary<string, object?>;
        if (dbs is null || SourcesPy.Get(dbs, "read") is not List<object?> read) yield break;
        foreach (var db in read) yield return (IReadOnlyDictionary<string, object?>)db!;
    }

    private static string JsonOrEmpty(object? value) =>
        PyFormat.Json(SourcesPy.Truthy(value) ? value : new Dictionary<string, object?>(), sortKeys: false, ensureAscii: true);

    private static string RemoveSuffix(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;

    private static string PyOptional(long? value) => value is null ? "None" : value.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Python's <c>str()</c> of a value DuckDB returned.</summary>
    internal static string PyStr(object? value) => value switch
    {
        null => "None",
        DateTime t => t.ToString(t.TimeOfDay.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString(t.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:sszzz" : "yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture),
        _ => SourcesPy.Str(value),
    };

    private static object? JsonSafe(object? value) => value switch
    {
        null or string or bool or long or double => value,
        List<object?> list => list.Select(JsonSafe).ToList(),
        IReadOnlyDictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => JsonSafe(kv.Value), StringComparer.Ordinal),
        BigInteger b => b,
        _ => PyStr(value),  // json.dumps(..., default=str)
    };

    private static int PyLen(string s) => s.EnumerateRunes().Count();

    private static string LJust(string s, int width) => s + new string(' ', Math.Max(0, width - PyLen(s)));

    private static string ReadPackageVersion()
    {
        var info = typeof(CatalogBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CatalogBuilder).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    // --- DuckDB ----------------------------------------------------------------------------------------

    internal static DuckDBConnection Open(string path, bool readOnly)
    {
        var builder = new DuckDBConnectionStringBuilder { DataSource = path };
        if (readOnly) builder["ACCESS_MODE"] = "READ_ONLY";
        var con = new DuckDBConnection(builder.ConnectionString);
        con.Open();
        return con;
    }

    /// <summary>Removes a DuckDB file and its write-ahead log, if present.</summary>
    private static void DeleteDatabase(string path)
    {
        File.Delete(path);
        File.Delete(path + ".wal");
    }

    private static DuckDBCommand Command(DuckDBConnection con, string sql, object?[] args)
    {
        var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var arg in args) cmd.Parameters.Add(new DuckDBParameter(arg ?? DBNull.Value));
        return cmd;
    }

    internal static void Exec(DuckDBConnection con, string sql, params object?[] args)
    {
        using var cmd = Command(con, sql, args);
        cmd.ExecuteNonQuery();
    }

    internal static List<object?[]> Query(DuckDBConnection con, string sql, params object?[] args)
    {
        using var cmd = Command(con, sql, args);
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    private static List<Dictionary<string, object?>> Dicts(DuckDBConnection con, string sql)
    {
        using var cmd = Command(con, sql, []);
        using var reader = cmd.ExecuteReader();
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var values = ReadRow(reader);
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++) d[names[i]] = values[i];
            rows.Add(d);
        }
        return rows;
    }

    private static long Count(DuckDBConnection con, string sql, params object?[] args) =>
        Convert.ToInt64(Query(con, sql, args)[0][0], CultureInfo.InvariantCulture);

    private static (long Count, object? Example) CountAndExample(DuckDBConnection con, string sql, params object?[] args)
    {
        var row = Query(con, sql, args)[0];
        return (Convert.ToInt64(row[0], CultureInfo.InvariantCulture), row.Length > 1 ? row[1] : null);
    }

    private static object?[] ReadRow(System.Data.Common.DbDataReader reader)
    {
        var values = new object?[reader.FieldCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = reader.IsDBNull(i) ? null : Plain(reader.GetValue(i));
        return values;
    }

    /// <summary>A DuckDB value as the plain value Python's DuckDB returns: integers as <c>long</c>, lists as
    /// <c>List&lt;object?&gt;</c>, structs and maps as dictionaries.</summary>
    private static object? Plain(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or long or double or decimal or BigInteger or DateTime or DateTimeOffset or DateOnly or TimeOnly or Guid => value,
        int or short or sbyte or byte or uint or ushort => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong u => u <= long.MaxValue ? (long)u : new BigInteger(u),
        float f => (double)f,
        IDictionary d => d.Keys.Cast<object>().ToDictionary(k => PyStr(k), k => Plain(d[k]), StringComparer.Ordinal),
        IEnumerable e => e.Cast<object?>().Select(Plain).ToList(),
        _ => value,
    };
}
