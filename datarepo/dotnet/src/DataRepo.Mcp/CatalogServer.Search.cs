using System.Text.RegularExpressions;
using DataRepo.Bundle;

namespace DataRepo.Mcp;

public sealed partial class CatalogServer
{
    /// <summary>A query that looks like this is also tried as a peptide sequence. Deliberately strict: three letters
    /// would make "SAD" a peptide and every free-text search a peptide search.</summary>
    /// <remarks>There is no accession or gene regex here on purpose. Every kind is searched unless the caller names
    /// one, so a query does not have to be <i>classified</i> before it is answered -- and a classifier that decided
    /// "ELAVL1 is a gene, not a peptide" would be the first place a wrong answer could enter.</remarks>
    private static readonly Regex PeptideRe = new("^[ACDEFGHIKLMNPQRSTVWY]{6,}$", RegexOptions.CultureInvariant);

    private delegate (List<Dictionary<string, object?>> Rows, List<Dictionary<string, object?>> Sources) SearchSource(string query, int limit);

    /// <summary>Find the ids behind a name: a dataset, protein, gene, peptide, modification or tissue.</summary>
    /// <param name="query">What to look for. An accession, a gene symbol, a peptide sequence, a modification name, a
    /// tissue or free text.</param>
    /// <param name="kind">Restrict to one of <see cref="SearchKinds"/>. Omit to search them all.</param>
    /// <param name="limit">Hits per kind.</param>
    /// <returns>The hits, <b>and what was searched to get them</b> -- each source with the rows it holds, so "no
    /// hits" can be told apart from "that table is empty in this catalog".</returns>
    public Dictionary<string, object?> Search(string? query, string? kind = null, long limit = SearchLimit)
    {
        query = PyValues.Strip(query ?? "");
        if (query.Length == 0) throw new ToolException("search needs something to look for");
        var cap = (int)Math.Max(1, Math.Min(limit, SearchLimitMax));
        var kinds = SearchKindsFor(kind);

        var hits = new Dictionary<string, object?>(StringComparer.Ordinal);
        var searched = new List<Dictionary<string, object?>>();
        // One more than asked for, so truncation is OBSERVED rather than inferred from a full page. Without it
        // `search("KRT")` returned `total_hits: 25` beside `rows: 38002` when the real count is 232.
        var truncated = new List<string>();
        long total = 0;
        foreach (var name in kinds)
        {
            var (found, sources) = SourceFor(name)(query, cap + 1);
            if (found.Count > cap)
            {
                found = found.Take(cap).ToList();
                truncated.Add(name);
            }
            searched.AddRange(sources);
            if (found.Count > 0)
            {
                hits[name] = found;
                total += found.Count;
            }
        }

        var empty = searched.Where(s => s.GetValueOrDefault("rows") is long and 0).Select(s => (string)s["source"]!)
            .Distinct().Order(PyValues.CodePointOrder).ToList();
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["query"] = query,
            ["hits"] = hits,
            ["total_hits"] = total,
            ["searched"] = searched,
            ["provenance"] = Provenance(),
        };
        if (empty.Count > 0)
        {
            output["searched_but_empty"] = empty;
            output["searched_but_empty_means"] =
                "these tables exist in this catalog and hold no rows, so they could not match "
                + "anything. A question they would have answered has no answer here yet -- that is "
                + "not the same as the answer being no.";
        }
        if (truncated.Count > 0)
        {
            output["truncated_kinds"] = truncated;
            output["truncated_means"] =
                $"{string.Join(", ", truncated)} matched MORE than the {cap} hits shown and was cut off. "
                + "The list is not complete and its length is NOT a count -- raise `limit`, narrow "
                + "the query, or count with datarepo_sql.";
        }
        if (hits.Count == 0)
            output["no_hits_means"] =
                $"nothing in the sources listed under 'searched' matched {PyFormat.Repr(query)}. This catalog "
                + $"holds {Identity.Bundles.Count} dataset(s), not all of PRIDE, so an absence "
                + "here is an absence from these datasets only.";
        return output;
    }

    private static List<string> SearchKindsFor(string? kind)
    {
        if (kind is null) return SearchKinds.ToList();
        if (!SearchKinds.Contains(kind))
            throw new ToolException($"kind is one of {string.Join(", ", SearchKinds)}, not {PyFormat.Repr(kind)}");
        return [kind];
    }

    private SearchSource SourceFor(string kind) => kind switch
    {
        "dataset" => SearchDataset,
        "protein" => SearchProtein,
        "peptide" => SearchPeptide,
        "modification" => SearchModification,
        "sample" => SearchSample,
        "run" => SearchRun,
        "definition" => SearchDefinition,
        "localization" => SearchLocalization,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>One line of the <c>searched</c> block: what was looked in, and what was actually in it.</summary>
    /// <remarks><c>searchedColumns</c> is what makes an absence readable. <c>rows: 57, hits: 0</c> looks like a
    /// considered negative; it is not one when the columns that were matched against are NULL on all 57 rows. The
    /// columns are named whether or not they are empty, because an agent also needs to know that a protein NAME
    /// query never touched a name column -- there is no such column in the schema.</remarks>
    private Dictionary<string, object?> Source(string table, int hits, string? note = null, IReadOnlyList<string>? searchedColumns = null)
    {
        var info = TablesInCatalog().GetValueOrDefault(table);
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = table,
            ["present"] = info is not null,
            ["rows"] = info is null ? null : Rows(info),
            ["hits"] = (long)hits,
        };
        if (searchedColumns is { Count: > 0 } && PyValues.Truthy(Rows(info)))
        {
            entry["columns_searched"] = searchedColumns.ToList();
            var populated = Populated(table, searchedColumns);
            var empty = populated.Where(kv => kv.Value == 0).Select(kv => kv.Key).Order(PyValues.CodePointOrder).ToList();
            if (empty.Count > 0)
            {
                entry["columns_all_null"] = empty;
                entry["columns_all_null_mean"] =
                    $"{string.Join(", ", empty)} is NULL on all {PyValues.Thousands(Rows(info))} rows of {table}, so no "
                    + "query could have matched there. Zero hits from this source is missing data, "
                    + "not a negative answer.";
            }
        }
        if (!string.IsNullOrEmpty(note)) entry["note"] = note;
        return entry;
    }

    /// <summary>Run a search query only if its table is in this catalog, so an older one still works.</summary>
    private List<Dictionary<string, object?>> Maybe(string table, string sql, IReadOnlyList<object?> parameters, int limit) =>
        Box.HasTable(table) ? Box.Dicts(sql, parameters, limit) : [];

    private (List<Dictionary<string, object?>>, List<Dictionary<string, object?>>) SearchDataset(string query, int limit)
    {
        var like = $"%{PyValues.Lower(query)}%";
        var rows = Maybe("dataset_overview",
            "SELECT dataset_id, title, organisms, acquisition, quant_method, labelling, "
            + "enrichment, enrichment_mixed, instrument_vendor, n_runs, n_samples, n_psms_1pct, "
            + "n_protein_groups_1pct FROM dataset_overview "
            + "WHERE upper(dataset_id) = upper(?) OR lower(coalesce(title, '')) LIKE ? "
            + "OR lower(coalesce(instrument_vendor, '')) LIKE ? "
            + "OR lower(list_aggregate(organisms, 'string_agg', ' ')) LIKE ? "
            + "ORDER BY dataset_id",
            [query, like, like, like], limit);
        return (rows, [Source("dataset_overview", rows.Count, searchedColumns: ["dataset_id", "title", "instrument_vendor", "organisms"])]);
    }

    private (List<Dictionary<string, object?>>, List<Dictionary<string, object?>>) SearchProtein(string query, int limit)
    {
        var upper = PyValues.Upper(query);
        var like = $"{upper}%";
        // `proteins` is the search's protein LIST, so `protein_index` holds the decoys too. They are not hidden -- a
        // decoy hit is a real fact about the search -- but they are marked and sorted last.
        var rows = Maybe("protein_index",
            "SELECT protein_accession, gene, organism, n_datasets_contaminant, "
            + "starts_with(protein_accession, 'DECOY_') AS is_decoy, n_datasets, "
            + "n_datasets_1pct, dataset_ids, dataset_ids_1pct, best_q_value FROM protein_index "
            + "WHERE upper(protein_accession) = ? OR upper(coalesce(gene, '')) = ? "
            + "OR upper(coalesce(gene, '')) LIKE ? "
            + "ORDER BY is_decoy, "
            + "(upper(protein_accession) = ? OR upper(coalesce(gene, '')) = ?) DESC, "
            + "n_datasets_1pct DESC, protein_accession",
            [upper, upper, like, upper, upper], limit);
        var notes = new List<string>();
        if (rows.Count > 0)
            // ALWAYS, not only when every hit is zero. `EIF1AY` comes back `n_datasets_1pct: 2` and sits in ZERO
            // accepted protein groups.
            notes.Add(
                "n_datasets_1pct counts an accepted protein GROUP **or** an accepted PEPTIDOFORM, "
                + "so it is NOT 'identified at 1% protein FDR'. A NULL best_q_value beside a "
                + "non-zero n_datasets_1pct means NO accepted protein group contains this "
                + "accession -- for protein-level identification query protein_groups_1pct directly");
        if (rows.Any(r => PyValues.Truthy(r.GetValueOrDefault("is_decoy"))))
            notes.Add(
                "hits marked is_decoy are reversed-sequence entries the search used to estimate "
                + "FDR; they are not proteins and must never be counted as evidence");
        if (rows.Any(r => PyValues.Truthy(r.GetValueOrDefault("n_datasets_contaminant"))))
            // There used to be one corpus-wide `is_contaminant`, and P02768 read `true` while it is a target in all
            // 17 human datasets; an agent concluded albumin was excluded (aging 070).
            notes.Add(
                "the contaminant label is PER DATASET: n_datasets_contaminant counts the datasets "
                + "whose search labelled this accession a contaminant, out of n_datasets. Human "
                + "albumin is a target in a human search and a contaminant in a rodent one. Use "
                + "protein_datasets.is_contaminant for one dataset");
        notes.Add(
            "matched on ACCESSION (exact) and GENE SYMBOL (exact, then prefix) ONLY. There is no "
            + "protein name or description column anywhere in this schema, so a query like "
            + "'cytochrome c oxidase' cannot match however many rows this table holds -- zero hits "
            + "for a protein NAME is 'never searched', not 'not present'. Search the gene symbol "
            + "instead (COX4I1, NDUFA9)");
        return (rows, [Source("protein_index", rows.Count, string.Join("; ", notes), ["protein_accession", "gene"])]);
    }

    private (List<Dictionary<string, object?>>, List<Dictionary<string, object?>>) SearchPeptide(string query, int limit)
    {
        var upper = PyValues.Upper(query);
        if (!PeptideRe.IsMatch(upper))
            return ([], [Source("peptide_index", 0, "not searched: the query is not 6 or more amino-acid letters")]);
        var rows = Maybe("peptide_index",
            "SELECT base_sequence, n_datasets, dataset_ids, n_peptidoforms, best_q_value "
            + "FROM peptide_index WHERE base_sequence = ? OR base_sequence LIKE ? "
            + "ORDER BY base_sequence = ? DESC, n_datasets DESC",
            [upper, $"%{upper}%", upper], limit);
        return (rows, [Source("peptide_index", rows.Count, searchedColumns: ["base_sequence"])]);
    }

    private (List<Dictionary<string, object?>>, List<Dictionary<string, object?>>) SearchModification(string query, int limit)
    {
        var like = $"%{PyValues.Lower(query)}%";
        var rows = Maybe("ptm_sites",
            "SELECT modification, modification_name, count(*) AS n_sites, "
            + "count(DISTINCT dataset_id) AS n_datasets, "
            + "list_sort(list(DISTINCT dataset_id)) AS dataset_ids, sum(n_psms) AS n_psms "
            + "FROM ptm_sites "
            + "WHERE lower(coalesce(modification_name, '')) LIKE ? "
            + "OR lower(coalesce(modification, '')) LIKE ? "
            + "GROUP BY 1, 2 ORDER BY n_sites DESC",
            [like, like], limit);
        var sources = new List<Dictionary<string, object?>> { Source("ptm_sites", rows.Count, searchedColumns: ["modification_name", "modification"]) };
        var declaredTable = Box.HasTable("search_modifications_declared") ? "search_modifications_declared" : "search_modifications";
        var declared = Maybe(declaredTable,
            $"SELECT DISTINCT modification, name, residues, usage, dataset_id FROM \"{declaredTable}\" "
            + "WHERE lower(coalesce(name, '')) LIKE ? OR lower(coalesce(modification, '')) LIKE ? "
            + "ORDER BY name, dataset_id",
            [like, like], limit);
        sources.Add(Source(declaredTable, declared.Count,
            "what the search was TOLD to look for, which is not what it placed; "
            + "a declared modification with no ptm_sites row was searched and not found"));
        foreach (var row in declared) row["_source"] = declaredTable;
        return (rows.Concat(declared).ToList(), sources);
    }

    private (List<Dictionary<string, object?>>, List<Dictionary<string, object?>>) SearchSample(string query, int limit)
    {
        var like = $"%{PyValues.Lower(query)}%";
        // The `_name` columns exist in catalogs from format 8 on (G74): the term columns are NULL wherever the SDRF
        // names a value without an accession, which is most of the time.
        var have = Box.HasTable("samples") ? ColumnTypes("samples").Keys.ToHashSet(StringComparer.Ordinal) : [];
        (string Column, string Alias)[] all =
        [
            ("organism", "organisms"), ("organism_part", "organism_parts"),
            ("organism_part_name", "organism_part_names"), ("cell_type", "cell_types"),
            ("cell_type_name", "cell_type_names"), ("sex_name", "sex_names"),
            ("disease", "diseases"), ("disease_name", "disease_names"),
            ("condition", "conditions"),
        ];
        var listed = all.Where(p => have.Contains(p.Column)).ToList();
        var searched = listed.Select(p => p.Column).Concat(new[] { "cell_line", "source_name" }.Where(have.Contains)).ToList();
        var rows = searched.Count > 0
            ? Maybe("samples",
                // NULLs are filtered out rather than coalesced to a placeholder: an empty list reads as "not
                // recorded for these samples", where a list of '-' reads as a value.
                "SELECT dataset_id, count(*) AS n_samples, "
                + string.Join(", ", listed.Select(p => $"list_sort(list(DISTINCT \"{p.Column}\") FILTER (WHERE \"{p.Column}\" IS NOT NULL)) AS {p.Alias}"))
                + " FROM samples WHERE "
                + string.Join(" OR ", searched.Select(c => $"lower(coalesce(\"{c}\", '')) LIKE ?"))
                + " GROUP BY dataset_id ORDER BY dataset_id",
                Enumerable.Repeat((object?)like, searched.Count).ToList(), limit)
            : [];
        var sources = new List<Dictionary<string, object?>> { Source("samples", rows.Count, searchedColumns: searched) };
        var characteristics = Maybe("sample_characteristics",
            "SELECT name, value, count(*) AS n_samples FROM sample_characteristics "
            + "WHERE lower(value) LIKE ? OR lower(name) LIKE ? GROUP BY 1, 2 ORDER BY n_samples DESC",
            [like, like], limit);
        sources.Add(Source("sample_characteristics", characteristics.Count, searchedColumns: ["name", "value"]));
        return (rows.Concat(characteristics).ToList(), sources);
    }

    private (List<Dictionary<string, object?>>, List<Dictionary<string, object?>>) SearchRun(string query, int limit)
    {
        var like = $"%{PyValues.Lower(query)}%";
        var rows = Maybe("runs",
            "SELECT dataset_id, run_id, file_name, fraction, technical_replicate, "
            + "instrument_model, ms2_spectra, run_minutes, qc_pass FROM runs "
            + "WHERE lower(run_id) LIKE ? OR lower(coalesce(file_name, '')) LIKE ? "
            + "OR lower(coalesce(instrument_model, '')) LIKE ? ORDER BY dataset_id, run_id",
            [like, like, like], limit);
        return (rows, [Source("runs", rows.Count, searchedColumns: ["run_id", "file_name", "instrument_model"])]);
    }

    private (List<Dictionary<string, object?>>, List<Dictionary<string, object?>>) SearchDefinition(string query, int limit)
    {
        var like = $"%{PyValues.Lower(query)}%";
        var rows = Maybe("definitions",
            "SELECT DISTINCT definition_id, version, owner_project, text, url FROM definitions "
            + "WHERE lower(definition_id) LIKE ? OR lower(coalesce(text, '')) LIKE ? "
            + "ORDER BY definition_id, version DESC",
            [like, like], limit);
        var sources = new List<Dictionary<string, object?>> { Source("definitions", rows.Count, searchedColumns: ["definition_id", "text"]) };
        var metrics = Maybe("metrics",
            "SELECT dataset_id, scope, scope_id, name, value, definition_id, source FROM metrics "
            + "WHERE lower(name) LIKE ? OR lower(coalesce(definition_id, '')) LIKE ? "
            + "ORDER BY dataset_id, name",
            [like, like], limit);
        sources.Add(Source("metrics", metrics.Count, searchedColumns: ["name", "definition_id"]));
        return (rows.Concat(metrics).ToList(), sources);
    }

    /// <summary>Organelle and compartment. Usually empty, and saying so is the point: the organelle map belongs to
    /// <c>go</c> (D1), and a search for "mitochondria" against an empty <c>protein_localizations</c> must not come
    /// back looking like a considered no.</summary>
    private (List<Dictionary<string, object?>>, List<Dictionary<string, object?>>) SearchLocalization(string query, int limit)
    {
        var like = $"%{PyValues.Lower(query)}%";
        // The category is a property of the term (go 004, DATAREPO-29), so it is joined in rather than read off the
        // protein row. A catalog from before schema 0.0.8 has neither table shape; it gets no rows.
        var rows = new List<Dictionary<string, object?>>();
        if (Box.HasTable("organelle_term_categories"))
            rows = Maybe("protein_localizations",
                // An annotation row names no map (go D28, schema 0.0.9), so the join gives one category row per map.
                "SELECT l.dataset_id, l.compartment, c.category_map_name, c.organelle_map_version, "
                + "c.organelle_category, c.organelle_subcategory, "
                + "l.evidence, count(DISTINCT l.protein_accession) AS n_proteins, "
                + "list_sort(list(DISTINCT l.protein_accession))[1:10] AS examples "
                + "FROM protein_localizations l LEFT JOIN organelle_term_categories c "
                + "ON c.compartment = l.compartment AND c.go_release = l.go_release "
                + "WHERE lower(l.compartment) LIKE ? OR lower(coalesce(c.organelle_category, '')) LIKE ? "
                + "OR lower(coalesce(c.organelle_subcategory, '')) LIKE ? "
                + "GROUP BY 1, 2, 3, 4, 5, 6, 7 ORDER BY n_proteins DESC",
                [like, like, like], limit);
        var sources = new List<Dictionary<string, object?>>
        {
            Source("protein_localizations", rows.Count,
                "where compartment and organelle live. The map is `go`'s to produce (D1); dataRepo stores it"),
        };
        var annotations = Maybe("protein_annotations",
            "SELECT dataset_id, key, value, count(*) AS n_proteins FROM protein_annotations "
            + "WHERE lower(coalesce(value, '')) LIKE ? OR lower(key) LIKE ? "
            + "GROUP BY 1, 2, 3 ORDER BY n_proteins DESC",
            [like, like], limit);
        sources.Add(Source("protein_annotations", annotations.Count));
        return (rows.Concat(annotations).ToList(), sources);
    }

    // --- sql ------------------------------------------------------------------------------------------

    /// <summary>Run one read-only SQL statement against this catalog, bounded by D14's sandbox.</summary>
    /// <param name="query">One statement. <c>SELECT</c>, <c>EXPLAIN</c>, <c>PRAGMA</c> or <c>SHOW</c>; several at
    /// once are refused, and so is anything that writes.</param>
    /// <param name="maxRows">A tighter row cap than the server's, for a query you expect to be large.</param>
    /// <returns>Columns, rows as lists, whether the answer was cut short and by which cap, and the provenance.</returns>
    public Dictionary<string, object?> Sql(string query, long? maxRows = null)
    {
        var result = Box.Query(query, maxRows);
        var provenance = Provenance();
        var (touched, undetermined) = TablesTouched(query);

        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sql"] = query,
            ["columns"] = result.Columns,
            ["rows"] = result.Rows.Select(r => (object?)r.ToList()).ToList(),
            ["row_count"] = (long)result.RowCount,
            ["truncated"] = result.Truncated,
            ["elapsed_seconds"] = Math.Round(result.ElapsedSeconds, 3, MidpointRounding.ToEven),
            ["limits"] = result.Caps,
            ["tables_touched"] = touched,
            ["provenance"] = provenance,
        };
        if (undetermined is not null) output["tables_touched_undetermined"] = undetermined;
        // The reason this key exists. Every "this table is empty, do not answer from it" guard used to live in
        // describe() and search(), the two tools an agent may skip, and was absent from the one it always reaches.
        // An envelope field cannot be skipped (D19).
        var empty = (touched ?? []).Where(t => t.GetValueOrDefault("rows") is long and 0).Select(t => (string)t["table"]!).ToList();
        if (empty.Count > 0)
        {
            output["empty_tables"] = empty;
            // States the fact and stops. A warning that asserts a reason it has not checked is the failure it was
            // written to prevent, pointed the other way.
            output["empty_tables_mean"] =
                $"{string.Join(", ", empty)} exist in this catalog and hold NO ROWS, so they contributed "
                + "nothing here. Whether that is WHY this result looks as it does depends on the "
                + "query: check before reporting an absence as a finding. An undelivered table is "
                + "never evidence for a negative answer.";
        }
        var runRelative = RunRelativeRead(query, result.Columns, touched);
        if (runRelative.Count > 0)
        {
            output["run_relative_columns"] = runRelative.ToDictionary(c => c, c => (object?)RunRelativeColumns[c], StringComparer.Ordinal);
            // Not a refusal: a within-dataset ranking and a count at a threshold are correct uses.
            output["run_relative_means"] =
                $"This query reads {string.Join(", ", runRelative)}. MetaMorpheus trains its PEP model "
                + "afresh on every search, so these values are on a scale set by the search that "
                + "wrote them. Rank or threshold them WITHIN one dataset. Do not compare their values "
                + "across datasets or MetaMorpheus releases (no per-dataset medians, means or "
                + "distributions set side by side): compare counts at a threshold instead, and prefer "
                + "`q_value` across releases. Definition: describe('pep:DEF-PEP').";
        }
        var aboutSamples = StudyRowsOnSamples(touched);
        if (aboutSamples.Count > 0)
        {
            output["study_tables_on_these_samples"] = aboutSamples;
            // G69 again, one step later (aging 078): a curated tissue or cell line lives in a study table and never
            // in `samples`. Not a refusal, and it names no value: it says where else to look.
            output["study_tables_on_these_samples_mean"] =
                $"This query reads sample tables. {string.Join(", ", aboutSamples)} hold rows about "
                + "samples in this catalog, delivered by a study layer rather than read from an "
                + "SDRF. A NULL or missing tissue, cell type, cell line, disease or age in `samples` "
                + "is not evidence that nothing is known: read those tables before reporting one as "
                + "unknown, and say that such a value is the study owner's curation, not the "
                + "depositor's.";
        }
        if (result.Truncated)
        {
            output["truncated_by"] = result.TruncatedBy;
            output["truncated_means"] =
                $"you were given the first {result.RowCount} row(s) the query produced, cut off "
                + $"by the {result.TruncatedBy} cap. This is NOT the whole answer: aggregate in SQL "
                + "(count, group by) rather than counting the rows you can see.";
        }
        return output;
    }

    /// <summary>Populated study tables keyed on <c>samples</c>, when the statement reads a sample table.</summary>
    /// <remarks>The study tables come from <see cref="Integrity.StudyReferences"/>, so a layer that adds another
    /// sample-keyed table is covered without touching this. An undetermined read reports nothing: the note is a
    /// pointer, and a pointer on every unparsed query would train a reader to skip it.</remarks>
    private List<string> StudyRowsOnSamples(List<Dictionary<string, object?>>? touched)
    {
        var read = (touched ?? []).Select(t => (string)t["table"]!).ToHashSet(StringComparer.Ordinal);
        if (!read.Overlaps(SampleTables)) return [];
        var known = TablesInCatalog();
        var keyed = Integrity.StudyReferences.Values
            .SelectMany(references => references)
            .Where(r => r.Target == "samples")
            .Select(r => r.Table)
            .Order(PyValues.CodePointOrder)
            .ToList();
        return keyed.Where(t => RowsOrZero(known.GetValueOrDefault(t)) > 0).ToList();
    }

    /// <summary>The <see cref="RunRelativeColumns"/> a statement reads, as far as can be seen.</summary>
    /// <remarks>Three routes, any one enough: the parse names the column (through an alias too); the result has a
    /// column of that name; or the statement expands a <c>*</c> and reads a table that holds one. The last
    /// over-reports, and that side is chosen on purpose: an unneeded note costs a sentence, a missing one a wrong
    /// answer.</remarks>
    private List<string> RunRelativeRead(string query, IReadOnlyList<string> resultColumns, List<Dictionary<string, object?>>? touched)
    {
        var hits = resultColumns.Select(PyValues.Lower).Where(RunRelativeColumns.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var parsed = Box.ReferencedColumns(query);
        if (parsed is { } p)
        {
            hits.UnionWith(p.Names.Where(RunRelativeColumns.ContainsKey));
            if (p.Star)
                foreach (var table in touched ?? [])
                    hits.UnionWith(ColumnTypes((string)table["table"]!).Keys.Where(RunRelativeColumns.ContainsKey));
        }
        return hits.Order(PyValues.CodePointOrder).ToList();
    }

    /// <summary>The catalog tables a statement reads, or <c>(null, why)</c> when that cannot be determined.</summary>
    /// <remarks><b>This reads the query, not the engine.</b> It is a hint about where to look, never evidence of where
    /// an answer came from -- provenance is <c>catalog_id</c>. While this was presented as certification, a
    /// <c>WITH protein_groups_1pct AS (SELECT 99999)</c> came back with the real view's 8,055-row count attached to a
    /// fabricated number.</remarks>
    private (List<Dictionary<string, object?>>?, string?) TablesTouched(string query)
    {
        var names = Box.ReferencedTables(query);
        if (names is null)
            return (null,
                "could not be determined for this statement -- it names a table function that "
                + "takes its target as a string (query/query_table/read_*), or is a kind whose "
                + "reads cannot be parsed. Tables MAY have been read that are not listed. This is "
                + "not a claim that none were.");
        var known = TablesInCatalog();
        return (names.Where(known.ContainsKey).Select(name => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["table"] = name,
            ["rows"] = Rows(known[name]),
            ["kind"] = known[name].GetValueOrDefault("kind"),
        }).ToList(), null);
    }
}
