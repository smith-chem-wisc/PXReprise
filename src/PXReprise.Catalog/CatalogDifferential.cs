using PXReprise.Bundle;
using PXReprise.Ingest.Sources;
using DuckDB.NET.Data;

namespace PXReprise.Catalog;

/// <summary>The differential layer (our 145, PXR-R78; PXR-R85): PXReprise's <c>differential</c> stage, registered beside the
/// bundle it was computed from and served from catalog format 18.</summary>
/// <remarks>
/// <para><b>Beside the bundle, never in it.</b> The stage reads a delivered search's own files and writes
/// <c>06_differential/</c> (PXReprise D84; QuantProject's <c>DEF-DIFF-ABUNDANCE</c>, mzLib's differential code). It can be run
/// again over the same search with no new search, so its output is an engine artefact: a re-run costs one registration and a
/// catalog rebuild, never a re-ingest, and no bundle id moves.</para>
/// <para><b>Bound by the bundle's own record.</b> <c>differential.register</c> takes a stage only when every input the stage
/// recorded is a source of the bundle it is registered for, by sha256, and every input an analysis's metadata names by file is
/// that bundle's source of the same role (<c>PXReprise.Runner.DifferentialEngine</c>). An input that names no file
/// (<c>observation_table</c>, the table mzLib builds in memory from the peptide file) is recorded and not bound (PXR-R85,
/// option A): the peptide file's sha256 already pins what it was built from. The bundle id hashes its sources' sha256s, so at
/// build an artefact naming a served bundle's id is bound to exactly those files.</para>
/// <para><b>Which artefacts.</b> One per served bundle at most. Two for one bundle are refused, never one picked, as for go,
/// logs and ptmQtl: which run a catalog serves is the operator's decision. A stage that analysed nothing (one condition, for
/// example) is registered too, so "no analysis was possible" reads differently from "the stage never ran".</para>
/// <para><b>Rows.</b> Every row carries its bundle's <c>dataset_id</c> and <c>bundle_id</c>, and <c>artefact_id</c>, the
/// artefact it came from (<c>catalog_engine_artefacts</c>). The seven tables are in every catalog of the current rules, empty
/// when no stage is registered for a served bundle.</para>
/// </remarks>
public static partial class CatalogBuilder
{
    public const string DifferentialStagesTable = "differential_stages";
    public const string DifferentialAnalysesTable = "differential_analyses";
    public const string DifferentialResultsTable = "differential_results";
    public const string DifferentialDecisionsTable = "differential_decisions";
    public const string DifferentialExclusionsTable = "differential_exclusions";
    public const string DifferentialFindingsTable = "differential_findings";
    public const string DifferentialFilesTable = "differential_files";

    /// <summary>The differential tables, in the order a catalog lists them.</summary>
    public static readonly IReadOnlyList<string> DifferentialTables =
    [
        DifferentialStagesTable, DifferentialAnalysesTable, DifferentialResultsTable, DifferentialDecisionsTable,
        DifferentialExclusionsTable, DifferentialFindingsTable, DifferentialFilesTable,
    ];

    private static ColumnSpec Text(string name, bool nullable = true) => new(name, ColumnType.String, IsList: false, Nullable: nullable);
    private static ColumnSpec Texts(string name) => new(name, ColumnType.String, IsList: true, Nullable: true);
    private static ColumnSpec Long(string name, bool nullable = true) => new(name, ColumnType.Int64, IsList: false, Nullable: nullable);
    private static ColumnSpec Real(string name) => new(name, ColumnType.Float64, IsList: false, Nullable: true);
    private static ColumnSpec Flag(string name, bool nullable = true) => new(name, ColumnType.Boolean, IsList: false, Nullable: nullable);

    /// <summary><c>DifferentialResults.tsv</c>'s columns as mzLib's <c>diff-1.0</c> writes them (machine header), in its order,
    /// with the type each is stored as. A file with any other header is refused: a new column is a decision, not a guess.</summary>
    /// <remarks>Types are mzLib's <c>Quantification.Differential.DifferentialResult</c> properties (mzLib 1.0.601). Every
    /// column is nullable: a blank cell is NULL, never 0 or the empty string.</remarks>
    public static readonly IReadOnlyList<ColumnSpec> DifferentialResultColumns =
    [
        Text("definition_id"), Text("analysis_id"), Text("quant_style"), Text("reporter_acquisition"), Text("stratum"), Text("grain"),
        Text("feature_id"), Text("feature_accessions"), Text("genes"), Text("quantity"), Text("effect_type"), Text("quant_basis"),
        Text("contrast_id"), Text("contrast_label"), Text("numerator"), Text("denominator"), Text("covariate"), Text("covariate_unit"),
        Text("covariate_scale"), Real("log2_effect"), Real("natural_effect"), Text("natural_unit"), Real("delta_percentage_points"),
        Real("mean_numerator"), Real("mean_denominator"), Real("se"), Real("ci_low"), Real("ci_high"), Real("ci_level"),
        Real("statistic"), Real("df"), Text("df_method"), Real("p_value"), Real("p_adjusted"), Text("adjustment_method"),
        Long("family_size"), Real("pep"), Real("bayesian_fdr"), Real("bayes_factor"), Real("null_width"),
        Long("n_samples_numerator"), Long("n_samples_denominator"), Long("n_samples_with_value"), Long("n_samples_total"),
        Long("n_peptides"), Long("n_observations"), Long("n_mbr_values"), Text("status"), Text("status_detail"), Text("method"),
        Text("model_used"), Flag("robust"), Flag("robust_fell_back"), Text("normalization"), Text("covariates_fitted"),
        Text("design_sha256"), Text("method_version"),
    ];

    /// <summary><c>DifferentialDecisions.tsv</c>'s columns, all text, in mzLib's order.</summary>
    public static readonly IReadOnlyList<ColumnSpec> DifferentialDecisionColumns =
    [
        Text("analysis_id"), Text("scope"), Text("ruling"), Text("rule"), Text("stratum"), Text("quant_basis"), Text("feature_id"),
        Text("contrast_id"), Text("facts"), Text("effect"),
    ];

    /// <summary>Each differential table's columns as its artefact stores them. The catalog adds <c>dataset_id</c> and
    /// <c>bundle_id</c> before them and <c>artefact_id</c> after.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<ColumnSpec>> DifferentialColumns =
        new Dictionary<string, IReadOnlyList<ColumnSpec>>(StringComparer.Ordinal)
        {
            [DifferentialStagesTable] =
            [
                Text("stage_schema", false), Text("definition_id", false), Text("method_version"), Text("quant_style"),
                Flag("match_between_runs"), Flag("analysed", false), Long("n_analyses", false), Text("design_file"),
                Text("design_sha256"), Long("design_samples"), Texts("conditions"), Text("corrected_sdrf_sha256"),
                Text("individuals_source"), Text("individuals_reason"), Long("n_individuals"), Text("metamorpheus"),
                Text("identification_filter"), Flag("pep_trained"), Text("pxreprise"), Text("mzlib"),
                Long("n_samples_left_out", false), Long("n_lost_transfer_files", false), Texts("finding_kinds"),
                Text("started_utc"), Text("finished_utc"),
            ],
            [DifferentialAnalysesTable] =
            [
                Text("analysis_id", false), Text("name", false), Text("quant_basis"), Text("definition_version"),
                Text("method_version"), Long("samples"), Long("rows", false), Long("contrasts"), Long("decisions", false),
                Texts("design_warnings"), Text("settings_json"), Texts("bound_roles"), Text("observation_table_sha256"),
            ],
            [DifferentialResultsTable] = DifferentialResultColumns,
            [DifferentialDecisionsTable] = DifferentialDecisionColumns,
            [DifferentialExclusionsTable] =
            [
                Text("kind", false), Text("sample"), Text("condition"), Text("file"), Texts("files"), Long("mbr_peaks"),
                Real("min_pip_q_value"),
            ],
            [DifferentialFindingsTable] = [Text("kind", false), Text("detail")],
            [DifferentialFilesTable] =
            [
                Text("path", false), Text("analysis"), Text("role", false), Text("sha256", false), Long("size_bytes", false),
            ],
        };

    /// <summary>What each differential table means, for <see cref="DerivedDocs"/> (a method, so no field initializer of
    /// another part of this class can read it before it is set).</summary>
    public static DerivedDoc DifferentialDoc(string table)
    {
        const string Provenance = "The registered stage's bundle: the search the analysis read.";
        var common = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dataset_id"] = "The dataset. " + Provenance,
            ["bundle_id"] = "The bundle whose sources the stage's inputs are, by sha256. " + Provenance,
            ["artefact_id"] = "The `differential.register` artefact (`catalog_engine_artefacts`): one registered stage folder.",
        };
        Dictionary<string, string> With(Dictionary<string, string> own)
        {
            foreach (var (k, v) in common) own.TryAdd(k, v);
            return own;
        }
        const string Layer = " The differential tables are in every catalog: EMPTY means no stage is registered for a served bundle "
            + "(the `catalog_checks` row of kind `engine-coverage` names the datasets without one), never that nothing differs.";
        return table switch
        {
            DifferentialStagesTable => new(
                "One row per registered `differential` stage: PXReprise's differential abundance of one delivered search "
                + "(QuantProject's `DEF-DIFF-ABUNDANCE`, computed by mzLib), registered by `differential.register` beside the bundle "
                + "it read, never inside it. `analysed = false` is a stage that ran and found nothing to compare (its findings say "
                + "why, e.g. `one_condition`), which is not the same as a dataset with no row here (never run, or not registered)."
                + Layer,
                With(new(StringComparer.Ordinal)
                {
                    ["stage_schema"] = "The stage record's version, `pxreprise-differential/1`.",
                    ["definition_id"] = "The method the stage ran under, e.g. `QuantProject:DEF-DIFF-ABUNDANCE v1`.",
                    ["method_version"] = "mzLib's differential method version, e.g. `diff-1.0`.",
                    ["quant_style"] = "`lfq` or `tmt`.",
                    ["match_between_runs"] = "Whether the search transferred identifications between runs.",
                    ["analysed"] = "True when the stage wrote at least one analysis.",
                    ["n_analyses"] = "How many analyses it wrote (`differential_analyses`), e.g. `mbr_kept` and `msms_only`.",
                    ["design_file"] = "The search's design file the stage read (`ExperimentalDesign.tsv`, `TmtDesign.txt`).",
                    ["design_sha256"] = "That file's sha256, a source of the bundle.",
                    ["design_samples"] = "Samples in the design.",
                    ["conditions"] = "The design's conditions, as the stage read them.",
                    ["corrected_sdrf_sha256"] = "The sha256 of the corrected SDRF the stage used, or NULL when it used none.",
                    ["individuals_source"] = "Where the stage took each sample's individual from, or NULL when it had none.",
                    ["individuals_reason"] = "Why there is no individual, when there is none, verbatim from the stage.",
                    ["n_individuals"] = "Distinct individuals, when known.",
                    ["metamorpheus"] = "The MetaMorpheus release of the search.",
                    ["identification_filter"] = "The search's identification filter as results.txt names it (`pep q-value`, "
                        + "`q-value notch`, `q-value`), or NULL when the search record names none.",
                    ["pep_trained"] = "Whether the search trained PEP, as its record says.",
                    ["pxreprise"] = "The PXReprise build that ran the stage.",
                    ["mzlib"] = "The mzLib build that computed it.",
                    ["n_samples_left_out"] = "Samples the stage left out (`differential_exclusions`, kind `sample_left_out`).",
                    ["n_lost_transfer_files"] = "Files that lost every match-between-runs transfer (kind `lost_transfer_file`).",
                    ["finding_kinds"] = "The kinds of the stage's findings (`differential_findings`); NULL when it recorded none.",
                    ["started_utc"] = "When the stage started, as it recorded it.",
                    ["finished_utc"] = "When it finished.",
                })),
            DifferentialAnalysesTable => new(
                "One row per analysis of a registered stage: one row set of `differential_results`, e.g. `mbr_kept` (values "
                + "transferred between runs kept) and `msms_only` (only values from a run's own MS/MS). Join on `analysis_id`." + Layer,
                With(new(StringComparer.Ordinal)
                {
                    ["analysis_id"] = "mzLib's id of the analysis; every row of `differential_results` and `differential_decisions` names one.",
                    ["name"] = "The analysis's folder in the stage, e.g. `mbr_kept`.",
                    ["quant_basis"] = "Which values it analysed: `mbr_kept` or `msms_only`.",
                    ["definition_version"] = "The definition version the metadata names, e.g. `DEF-DIFF v1`.",
                    ["method_version"] = "mzLib's method version the metadata names, e.g. `diff-1.0`.",
                    ["samples"] = "Samples analysed.",
                    ["rows"] = "Rows in `differential_results` for this analysis: one per (feature, contrast), fitted or not.",
                    ["contrasts"] = "Contrasts compared.",
                    ["decisions"] = "Rows in `differential_decisions` for this analysis.",
                    ["design_warnings"] = "mzLib's warnings about the design, verbatim (e.g. no reference level, so every pair is "
                        + "compared); NULL when it gave none.",
                    ["settings_json"] = "The analysis's settings, the metadata's `settings` object verbatim (JSON text).",
                    ["bound_roles"] = "The inputs that bound to the bundle: each the bundle's source of that role, by file name and sha256.",
                    ["observation_table_sha256"] = "The sha256 mzLib recorded for its in-memory observation table. It names no file, "
                        + "so it is recorded and not bound; the peptide file it was built from is bound (`bound_roles`).",
                })),
            DifferentialResultsTable => new(
                "`DifferentialResults.tsv`, row for row: one row per (feature, contrast) of each analysis, with mzLib's own "
                + "columns and names (machine header, `diff-1.0`). A row whose `status` is not `fitted` or `fitted_single_peptide` "
                + "has no effect and no p-value: `status` and `status_detail` say why (e.g. `absent_in_denominator`), never a "
                + "number. `p_adjusted` adjusts within the family `family_size` counts (`adjustment_method`). A blank cell is NULL. "
                + "A `protein_group` row joins its search's group on `protein_groups.bundle_id = bundle_id AND "
                + "protein_groups.protein_group_id = dataset_id || ':' || feature_accessions` (every row of the first four deposits "
                + "does)." + Layer,
                With(new(StringComparer.Ordinal)
                {
                    ["definition_id"] = "The method, e.g. `QuantProject:DEF-DIFF-ABUNDANCE v1`.",
                    ["analysis_id"] = "The analysis (`differential_analyses`).",
                    ["quant_style"] = "`lfq` or `tmt`.",
                    ["reporter_acquisition"] = "For an isobaric style, `ms2` or `ms3_sps`; NULL otherwise.",
                    ["stratum"] = "The stratum the row was fitted in (`all` when the samples are not split).",
                    ["grain"] = "What a feature is, e.g. `protein_group`.",
                    ["feature_id"] = "The feature: for `protein_group`, its accessions `|`-joined, as mzLib wrote them.",
                    ["feature_accessions"] = "The feature's accessions, `;`-joined, as mzLib wrote them: `protein_groups.protein_group_id` "
                        + "without its `<dataset_id>:` prefix.",
                    ["genes"] = "The members' gene names, as mzLib wrote them; NULL where none.",
                    ["quantity"] = "What was compared, e.g. `abundance`.",
                    ["effect_type"] = "What the effect is, e.g. `abundance_log2_ratio`.",
                    ["quant_basis"] = "`mbr_kept` or `msms_only`.",
                    ["contrast_id"] = "mzLib's id of the contrast within the analysis, e.g. `c1`.",
                    ["contrast_label"] = "The contrast in words: numerator vs denominator.",
                    ["numerator"] = "The numerator level, e.g. `condition=progeria`.",
                    ["denominator"] = "The denominator level.",
                    ["covariate"] = "For a slope, the covariate; NULL for a two-level contrast.",
                    ["covariate_unit"] = "For a slope, the unit the effect is per.",
                    ["covariate_scale"] = "For a slope, how the stored covariate became that unit.",
                    ["log2_effect"] = "The effect on the log2 scale (log2 ratio numerator over denominator); NULL when not fitted.",
                    ["natural_effect"] = "The effect on its natural scale (`natural_unit`, e.g. a ratio).",
                    ["natural_unit"] = "The natural effect's unit, e.g. `ratio`.",
                    ["delta_percentage_points"] = "For an effect on a fraction, the difference in percentage points.",
                    ["mean_numerator"] = "The numerator's mean, on the analysed scale.",
                    ["mean_denominator"] = "The denominator's mean.",
                    ["se"] = "The effect's standard error.",
                    ["ci_low"] = "The interval's lower bound, at `ci_level`.",
                    ["ci_high"] = "The interval's upper bound.",
                    ["ci_level"] = "The interval's level, e.g. 0.95.",
                    ["statistic"] = "The test statistic.",
                    ["df"] = "Its degrees of freedom (`df_method` says how they were found).",
                    ["df_method"] = "How the degrees of freedom were found, e.g. `satterthwaite_moderated`.",
                    ["p_value"] = "The p-value.",
                    ["p_adjusted"] = "The adjusted p-value, within the row's family (`family_size`, `adjustment_method`).",
                    ["adjustment_method"] = "The adjustment, e.g. `benjamini_hochberg`.",
                    ["family_size"] = "How many tests the adjustment counted.",
                    ["pep"] = "A Bayesian posterior error probability, when the method gives one.",
                    ["bayesian_fdr"] = "A Bayesian FDR, when the method gives one.",
                    ["bayes_factor"] = "A Bayes factor, when the method gives one.",
                    ["null_width"] = "The width of the null region a Bayesian method used, when it used one.",
                    ["n_samples_numerator"] = "Numerator samples with a value.",
                    ["n_samples_denominator"] = "Denominator samples with a value.",
                    ["n_samples_with_value"] = "Samples with a value, both sides.",
                    ["n_samples_total"] = "Samples in the analysis.",
                    ["n_peptides"] = "Peptides that carried the feature's values.",
                    ["n_observations"] = "Values the fit used.",
                    ["n_mbr_values"] = "Of those, values transferred between runs.",
                    ["status"] = "What became of the row: `fitted`, `fitted_single_peptide`, or why there is no number, e.g. "
                        + "`absent_in_denominator`, `below_support:min_2_per_side`, `not_estimable:confounded`.",
                    ["status_detail"] = "The reason in words, for a row with no number.",
                    ["method"] = "The method, e.g. `moderated`.",
                    ["model_used"] = "The model fitted, e.g. `peptide_mixed_model`, `moderated_t`.",
                    ["robust"] = "Whether robust weighting was on for the fit.",
                    ["robust_fell_back"] = "Whether the robust fit fell back to the plain one.",
                    ["normalization"] = "The normalization, e.g. `shared_peptide_median`.",
                    ["covariates_fitted"] = "The covariates and random terms fitted, e.g. `sample(random)`.",
                    ["design_sha256"] = "mzLib's sha256 of the design the fit used.",
                    ["method_version"] = "mzLib's method version, e.g. `diff-1.0`.",
                })),
            DifferentialDecisionsTable => new(
                "`DifferentialDecisions.tsv`, row for row: each ruling mzLib applied, at the scope it applied to (`analysis`, "
                + "`stratum`, `feature`, `feature_contrast`), with the facts it rested on and its effect. It says why a feature or "
                + "a contrast has no number, or a reduced fit." + Layer,
                With(new(StringComparer.Ordinal)
                {
                    ["analysis_id"] = "The analysis (`differential_analyses`).",
                    ["scope"] = "What the ruling applied to: `analysis`, `stratum`, `feature` or `feature_contrast`.",
                    ["ruling"] = "The ruling's id in QuantProject's method, e.g. `M5b-12`.",
                    ["rule"] = "The rule, e.g. `row_set`, `absent_side`, `reduced_design`.",
                    ["stratum"] = "The stratum, where the scope has one.",
                    ["quant_basis"] = "`mbr_kept` or `msms_only`, where the scope has one.",
                    ["feature_id"] = "The feature, for a feature scope.",
                    ["contrast_id"] = "The contrast, for a `feature_contrast` scope.",
                    ["facts"] = "What the ruling rested on, in words.",
                    ["effect"] = "What it did, in words.",
                })),
            DifferentialExclusionsTable => new(
                "What the stage left out before analysing: one row per sample it left out (kind `sample_left_out`) and per file "
                + "that lost every match-between-runs transfer (kind `lost_transfer_file`), from its record." + Layer,
                With(new(StringComparer.Ordinal)
                {
                    ["kind"] = "`sample_left_out` or `lost_transfer_file`.",
                    ["sample"] = "The sample left out.",
                    ["condition"] = "Its condition.",
                    ["file"] = "The file that lost its transfers.",
                    ["files"] = "The left-out sample's files.",
                    ["mbr_peaks"] = "The file's match-between-runs peaks in the search.",
                    ["min_pip_q_value"] = "The smallest PIP q-value among them, or NULL when none had one.",
                })),
            DifferentialFindingsTable => new(
                "The stage's own findings, verbatim: why a stage analysed nothing (`one_condition`), or what it could not use."
                + Layer,
                With(new(StringComparer.Ordinal)
                {
                    ["kind"] = "The finding's kind, e.g. `one_condition`.",
                    ["detail"] = "The finding in words, verbatim.",
                })),
            DifferentialFilesTable => new(
                "Every file of a registered stage, kept verbatim in its artefact under `files/<path>`: the record, each "
                + "analysis's results, decisions, metadata, `StatisticalReport.md`, `DifferentialAssumptions.md` and its figures. "
                + "Read one at `catalog_engine_artefacts.path` + `/files/` + `path`." + Layer,
                With(new(StringComparer.Ordinal)
                {
                    ["path"] = "The file's path in the stage folder, with `/`, e.g. `mbr_kept/figures/figure_02_volcano.svg`.",
                    ["analysis"] = "The analysis it belongs to, or NULL for the stage's own record.",
                    ["role"] = "`provenance`, `results`, `decisions`, `metadata`, `report`, `assumptions`, `figure`, `figure_table` or `other`.",
                    ["sha256"] = "The file's sha256, part of the artefact's id.",
                    ["size_bytes"] = "Its size.",
                })),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };
    }

    /// <summary>The <c>catalog_tables.kind</c> of every differential table.</summary>
    public static string DifferentialTableKind => $"engine:{Runner.DifferentialEngine}";

    /// <summary>The differential artefacts for these bundles, and a coverage check that never fails.</summary>
    /// <exception cref="CatalogException">Two current artefacts for one bundle.</exception>
    private static void SelectDifferential(string store, IReadOnlyList<BundleRef> bundles, List<ArtefactRef> chosen, List<CatalogCheck> checks)
    {
        var byBundle = bundles.ToDictionary(b => b.BundleId, StringComparer.Ordinal);
        var all = Runner.DiscoverArtefacts(store, Runner.DifferentialEngine)
            .Where(a => byBundle.ContainsKey(a.Inputs.GetValueOrDefault("bundle") ?? "")).ToList();
        var stale = all.Where(a => a.SchemaVersion != SchemaContract.Version).Select(a => $"{a.ArtefactId} (schema {a.SchemaVersion})").ToList();
        var served = new List<ArtefactRef>();
        foreach (var group in all.Where(a => a.SchemaVersion == SchemaContract.Version).GroupBy(a => a.Inputs["bundle"])
                     .OrderBy(g => byBundle[g.Key].DatasetId, SourcesPy.CodePointOrder))
        {
            if (group.Count() > 1)
                throw new CatalogException(
                    $"{Runner.DifferentialEngine}: {group.Count()} artefacts ({string.Join(", ", group.Select(a => a.ArtefactId))}) for "
                    + $"{byBundle[group.Key].DatasetId}'s bundle {group.Key}: the stage was registered more than once. A catalog serves one; "
                    + $"move the others out of {EngineFolder(Runner.DifferentialEngine)}.");
            served.Add(group.Single());
        }
        chosen.AddRange(served);
        var uncovered = bundles.Where(b => served.All(a => a.Inputs["bundle"] != b.BundleId)).Select(b => b.DatasetId).Order(SourcesPy.CodePointOrder).ToList();
        string? detail = uncovered.Count > 0 ? "no stage registered for " + string.Join(", ", uncovered) : null;
        if (stale.Count > 0) detail = (detail is not null ? detail + "; " : "") + "skipped, other schema: " + string.Join(", ", stale);
        checks.Add(new CatalogCheck($"{Runner.DifferentialEngine} coverage (bundles with a registered stage)", "engine-coverage", true,
            bundles.Count - uncovered.Count, bundles.Count, detail));
    }

    /// <summary>The seven differential tables, from the chosen artefacts' Parquet, or typed and empty.</summary>
    /// <remarks>With <paramref name="views"/> each is a view over the artefacts' files, as ptmQtl's are
    /// (<see cref="LoadPtmQtlRows"/>); otherwise a table holding their rows.</remarks>
    private static Dictionary<string, long> LoadDifferentialRows(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts, bool views = false)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!GoActive) return counts;
        var byBundle = bundles.ToDictionary(b => b.BundleId, StringComparer.Ordinal);
        var registered = artefacts.Where(a => a.Engine == Runner.DifferentialEngine).ToList();
        foreach (var table in DifferentialTables)
        {
            var parts = registered.Select(a => (Artefact: a, Path: a.TablePath(table))).Where(p => p.Path is not null)
                .Select(p => $"SELECT {Quote(byBundle[p.Artefact.Inputs["bundle"]].DatasetId)} AS dataset_id, {Quote(p.Artefact.Inputs["bundle"])} AS bundle_id, "
                    + $"*, {Quote(p.Artefact.ArtefactId)} AS artefact_id FROM read_parquet({Quote(AsPosix(p.Path!))})")
                .ToList();
            if (parts.Count == 0)
            {
                var columns = DatasetProvenance.Concat(DifferentialColumns[table].Select(c => (c.Name, SqlType(c)))).Append(("artefact_id", "VARCHAR"));
                var typed = string.Join(", ", columns.Select(c => $"CAST(NULL AS {c.Item2}) AS \"{c.Item1}\""));
                Exec(con, $"CREATE {(views ? "VIEW" : "TABLE")} \"{table}\" AS SELECT {typed} WHERE false");
                counts[table] = 0;
                continue;
            }
            Exec(con, $"CREATE {(views ? "VIEW" : "TABLE")} \"{table}\" AS\n{string.Join("\nUNION ALL BY NAME\n", parts)}");
            counts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
        }
        return counts;
    }
}
