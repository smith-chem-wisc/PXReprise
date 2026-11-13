using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Bundle;
using PXReprise.Mcp;
using DuckDB.NET.Data;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PXReprise.Data.Tests;

/// <summary>The sandbox's bounds, the server's wiring and <c>datarepo mcp</c>'s command line (ports of
/// <c>tests/test_sandbox.py</c> and the install/CLI half of <c>tests/test_mcp.py</c>).</summary>
/// <remarks>Most of the sandbox tests assert something about <b>DuckDB</b>, not about our code, and that is the point:
/// D14's choices rest on measurements, and a measurement nobody re-runs becomes a belief. Answers themselves are
/// compared with the Python server's in <see cref="McpParityTests"/>.</remarks>
[NonParallelizable]
public class McpTests
{
    /// <summary>A cross join that DuckDB cannot answer from metadata. <c>SELECT count(*) FROM range(3e9)</c>, the
    /// probe D14 was written against, returns in half a second on 1.5.5.</summary>
    public const string SlowQuery =
        "SELECT count(*) FROM (SELECT a.range, b.range FROM range(200000) a, range(200000) b "
        + "WHERE (a.range * b.range) % 7 = 3)";

    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

    private string _scratch = "";
    private string _catalog = "";

    [OneTimeSetUp]
    public void Catalog()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "datarepo-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        _catalog = Path.Combine(_scratch, "catalog.duckdb");
        using var gz = new GZipStream(File.OpenRead(Path.Combine(Fixtures, "site", "catalog.duckdb.gz")), CompressionMode.Decompress);
        using var file = File.Create(_catalog);
        gz.CopyTo(file);
    }

    [OneTimeTearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
    }

    /// <summary>A copy of the catalog no <see cref="Sandbox"/> in this process has opened: the filesystem lock
    /// belongs to the DuckDB database instance, and DuckDB refuses a second connection with another config.</summary>
    private string Unopened()
    {
        var copy = Path.Combine(_scratch, $"unopened-{Guid.NewGuid():N}.duckdb");
        File.Copy(_catalog, copy);
        return copy;
    }

    private string OutsideFile()
    {
        var path = Path.Combine(_scratch, $"secret-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "a,b\n1,2\n3,4\n");
        return path;
    }

    private static object? Scalar(DuckDBConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    // --- what read-only does NOT do ---------------------------------------------------------------------

    [Test]
    public void ReadOnlyAloneIsNotASandbox()
    {
        using var con = new DuckDBConnection($"DataSource={Unopened()};ACCESS_MODE=READ_ONLY");
        con.Open();
        Assert.That(Scalar(con, $"SELECT count(*) FROM read_csv_auto('{OutsideFile().Replace('\\', '/')}')"), Is.EqualTo(2L));
    }

    [Test]
    public void TheSandboxCannotReadAFileOutsideTheCatalog()
    {
        using var box = new Sandbox(_catalog);
        var e = Assert.Throws<CatalogException>(() => box.Query($"SELECT * FROM read_csv_auto('{OutsideFile().Replace('\\', '/')}')"));
        Assert.That(e!.Message, Does.Contain("disabled"));
    }

    [Test]
    public void ExternalAccessCannotBeReEnabledFromInside()
    {
        using var con = new DuckDBConnection($"DataSource={Unopened()};ACCESS_MODE=READ_ONLY;enable_external_access=false");
        con.Open();
        foreach (var statement in new[] { "SET enable_external_access=true", "PRAGMA enable_external_access=true", "RESET enable_external_access" })
        {
            var e = Assert.Throws<DuckDBException>(() => Scalar(con, statement), statement);
            Assert.That(e!.Message, Does.Contain("while database is running"), statement);
        }
    }

    [Test]
    public void ExternalAccessOffStillLeavesAttachOpen()
    {
        var file = Unopened();
        using var con = new DuckDBConnection($"DataSource={file};ACCESS_MODE=READ_ONLY;enable_external_access=false");
        con.Open();
        Scalar(con, $"ATTACH '{file.Replace('\\', '/')}' AS other (READ_ONLY)");
        Assert.That(Scalar(con, "SELECT count(*) FROM other.datasets"), Is.GreaterThanOrEqualTo(1L));
    }

    [Test]
    public void TheSandboxRefusesToAttachAnotherDatabase()
    {
        using var box = new Sandbox(_catalog);
        var e = Assert.Throws<QueryRefusedException>(() => box.Query($"ATTACH '{Unopened().Replace('\\', '/')}' AS other (READ_ONLY)"));
        Assert.That(e!.Message, Does.Contain("ATTACH"));
    }

    [Test]
    public void TheOpenCatalogStillServesWithTheFilesystemDisabled()
    {
        using var box = new Sandbox(_catalog);
        Assert.That(box.Query("SELECT count(*) FROM psms").Rows[0][0], Is.GreaterThanOrEqualTo(2L));
        Assert.That(box.Query("SELECT dataset_id, count(*) FROM psms_1pct GROUP BY 1").RowCount, Is.EqualTo(1));
    }

    // --- what it refuses by name ------------------------------------------------------------------------

    [TestCase("DROP TABLE psms", "DROP")]
    [TestCase("CREATE TABLE x AS SELECT 1", "CREATE")]
    [TestCase("DELETE FROM psms", "DELETE")]
    [TestCase("UPDATE psms SET scan = 2", "UPDATE")]
    [TestCase("INSTALL httpfs", "LOAD")]
    [TestCase("LOAD httpfs", "LOAD")]
    [TestCase("COPY (SELECT 1) TO 'out.csv'", "COPY")]
    [TestCase("INSERT INTO nope VALUES (1)", "INSERT")]
    [TestCase("WITH t AS (SELECT 1) INSERT INTO psms SELECT * FROM t", "INSERT")]
    [TestCase("EXPORT DATABASE 'x'", "EXPORT")]
    [TestCase("SET threads=4", "SET")]
    [TestCase("PRAGMA threads=4", "SET")]
    public void OnlyQuestionsAreRun(string statement, string kind)
    {
        using var box = new Sandbox(_catalog);
        var e = Assert.Throws<QueryRefusedException>(() => box.Query(statement));
        Assert.That(e!.Message, Does.StartWith($"{kind} is not one of the statement kinds"));
        Assert.That(e.Message, Does.Contain("SELECT"), "a refusal says what to send instead");
    }

    [Test]
    public void SeveralStatementsAreRefusedRatherThanRun()
    {
        using var box = new Sandbox(_catalog);
        Assert.That(Assert.Throws<QueryRefusedException>(() => box.Query("SELECT 1; SELECT 2"))!.Message, Does.StartWith("2 statements in one query (SELECT, SELECT)"));
        Assert.That(Assert.Throws<QueryRefusedException>(() => box.Query("PIVOT psms ON target_decoy USING count(*)"))!.Message, Does.StartWith("2 statements in one query (CREATE, SELECT)"));
        Assert.That(Assert.Throws<QueryRefusedException>(() => box.Query("  -- nothing\n"))!.Message, Is.EqualTo("no statement to run"));
    }

    [Test]
    public void UnparseableSqlIsRefusedNotRaisedAsACatalogError()
    {
        using var box = new Sandbox(_catalog);
        var e = Assert.Throws<QueryRefusedException>(() => box.Query("this is not sql"));
        Assert.That(e!.Message, Does.StartWith("not valid SQL: Parser Error: syntax error at or near \"this\""));
    }

    [Test]
    public void ExplainAndShowAreAllowed()
    {
        using var box = new Sandbox(_catalog);
        Assert.That(box.Query("EXPLAIN SELECT * FROM psms").RowCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(box.Query("SHOW TABLES").RowCount, Is.GreaterThanOrEqualTo(1));
    }

    /// <summary>A question is still a question when it is explained, and when it is explained by running it.</summary>
    [TestCase("EXPLAIN SELECT * FROM psms")]
    [TestCase("EXPLAIN ANALYZE SELECT count(*) FROM psms")]
    [TestCase("explain analyse select 1")]
    [TestCase("EXPLAIN\n  ANALYZE\n  SELECT 1")]
    [TestCase("EXPLAIN (SELECT 1)")]
    [TestCase("EXPLAIN /* why */ SELECT 1")]
    [TestCase("EXPLAIN WITH t AS (SELECT 1 AS a) SELECT * FROM t")]
    [TestCase("EXPLAIN FROM psms")]
    [TestCase("EXPLAIN SELECT 1;")]
    public void AnExplainedSelectIsAnswered(string statement)
    {
        using var box = new Sandbox(_catalog);
        Assert.That(box.CheckStatement(statement), Is.EqualTo("EXPLAIN"));
        Assert.That(box.Query(statement).RowCount, Is.GreaterThanOrEqualTo(1));
    }

    /// <summary>The whole statement's kind is EXPLAIN whatever it wraps, and <c>EXPLAIN ANALYZE</c> runs what it wraps, so the
    /// wrapped statement is what has to be a question. Every spelling, and every kind, because the reproduction was one of
    /// them.</summary>
    [TestCase("EXPLAIN ANALYZE COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN ANALYSE COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("explain analyze copy (select 1) to 'out.csv'")]
    [TestCase("EXPLAIN\nANALYZE\nCOPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN (ANALYZE) COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN (ANALYZE, FORMAT JSON) COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN /* a */ ANALYZE /* b */ COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN/**/ANALYZE/**/COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN -- a\nANALYZE -- b\nCOPY (SELECT 1) TO 'out.csv'")]
    [TestCase("/* a */ EXPLAIN ANALYZE COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN ANALYZE VERBOSE COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN VERBOSE COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN COPY (SELECT 1) TO 'out.csv'")]
    [TestCase("EXPLAIN ANALYZE CREATE TEMP TABLE t AS SELECT 1")]
    [TestCase("EXPLAIN ANALYZE CREATE TEMP VIEW v AS SELECT 1")]
    [TestCase("EXPLAIN ANALYZE CREATE TEMP MACRO m() AS 1")]
    [TestCase("EXPLAIN ANALYZE INSERT INTO nope VALUES (1)")]
    [TestCase("EXPLAIN ANALYZE WITH t AS (SELECT 1) INSERT INTO psms SELECT * FROM t")]
    [TestCase("EXPLAIN ANALYZE DELETE FROM psms")]
    [TestCase("EXPLAIN ANALYZE UPDATE psms SET scan = 2")]
    [TestCase("EXPLAIN ANALYZE SET threads=4")]
    [TestCase("EXPLAIN ANALYZE CALL pragma_version()")]
    [TestCase("EXPLAIN ANALYZE EXPLAIN ANALYZE COPY (SELECT 1) TO 'out.csv'")]
    public void AnExplainWrapsASelectAndNothingElse(string statement)
    {
        using var box = new Sandbox(_catalog);
        Assert.That(() => box.CheckStatement(statement), Throws.InstanceOf<QueryRefusedException>(), statement);
        Assert.That(() => box.Query(statement), Throws.InstanceOf<QueryRefusedException>(), statement);
    }

    [Test]
    public void ARefusedExplainSaysWhatToSendInstead()
    {
        using var box = new Sandbox(_catalog);
        Assert.That(Assert.Throws<QueryRefusedException>(() => box.Query("EXPLAIN ANALYZE COPY (SELECT 1) TO 'out.csv'"))!.Message,
            Does.StartWith("EXPLAIN here takes one SELECT").And.Contain("EXPLAIN ANALYZE SELECT"));
    }

    /// <summary>A SELECT's kind does not say it only reads. Each of these runs on DuckDB 1.5.5: <c>enable_profiling</c>
    /// overwrote a bundle's <c>bundle.json</c> and <c>enable_logging</c> wrote files, through every one of these routes that
    /// reaches a table function.</summary>
    [TestCase("SELECT * FROM enable_logging(storage='file', storage_path='x')")]
    [TestCase("FROM enable_logging(storage='file', storage_path='x')")]
    [TestCase("select * from ENABLE_LOGGING(storage='file', storage_path='x')")]
    [TestCase("SELECT * FROM main.enable_logging(storage='file', storage_path='x')")]
    [TestCase("SELECT * FROM system.main.enable_logging(storage='file', storage_path='x')")]
    [TestCase("SELECT * FROM \"ENABLE_LOGGING\"(storage='file', storage_path='x')")]
    [TestCase("SELECT * FROM range(1), LATERAL (SELECT * FROM enable_logging(storage='file', storage_path='x'))")]
    [TestCase("SELECT * FROM range(1) JOIN enable_logging(storage='file', storage_path='x') ON true")]
    [TestCase("SELECT (SELECT count(*) FROM enable_logging(storage='file', storage_path='x'))")]
    [TestCase("WITH t AS (SELECT * FROM enable_logging(storage='file', storage_path='x')) SELECT * FROM t")]
    [TestCase("SELECT 1 WHERE EXISTS (FROM enable_logging(storage='file', storage_path='x'))")]
    [TestCase("SELECT 1 UNION ALL SELECT count(*) FROM enable_logging(storage='file', storage_path='x')")]
    [TestCase("SUMMARIZE SELECT * FROM enable_logging(storage='file', storage_path='x')")]
    [TestCase("DESCRIBE SELECT * FROM enable_logging(storage='file', storage_path='x')")]
    [TestCase("EXPLAIN SELECT * FROM enable_logging(storage='file', storage_path='x')")]
    [TestCase("EXPLAIN ANALYZE SELECT * FROM enable_logging(storage='file', storage_path='x')")]
    [TestCase("SELECT * FROM query('SELECT * FROM enable_logging(storage=''file'', storage_path=''x'')')")]
    [TestCase("SELECT * FROM query('SELECT * FROM query(''SELECT * FROM enable_logging(storage=''''file'''', storage_path=''''x'''')'')')")]
    [TestCase("SELECT * FROM query($$SELECT * FROM enable_profiling(format='json', save_location='x.json')$$)")]
    [TestCase("EXPLAIN ANALYZE SELECT * FROM query('SELECT * FROM enable_logging(storage=''file'', storage_path=''x'')')")]
    [TestCase("SELECT * FROM query('SEL' || 'ECT 1')")]
    [TestCase("SELECT * FROM query(?)")]
    [TestCase("SELECT * FROM query('COPY (SELECT 1) TO ''x''')")]
    [TestCase("SELECT * FROM enable_profiling(format='json', save_location='x.json')")]
    [TestCase("SELECT * FROM json_execute_serialized_sql(json_serialize_sql('SELECT 1'))")]
    [TestCase("SELECT write_log('x')")]
    [TestCase("SELECT WRITE_LOG('x')")]
    [TestCase("SELECT 1 WHERE write_log('x') IS NULL")]
    [TestCase("PRAGMA disable_optimizer")]
    [TestCase("pragma disable_optimizer")]
    [TestCase("PRAGMA /* a */ disable_optimizer")]
    [TestCase("-- a\nPRAGMA disable_optimizer")]
    [TestCase("/* a /* b */ */ PRAGMA disable_optimizer")]
    [TestCase("PRAGMA \"disable_optimizer\"")]
    [TestCase("PRAGMA enable_profiling")]
    [TestCase("PRAGMA add_parquet_key('k','0123456789012345')")]
    [TestCase("PRAGMA json_execute_serialized_sql('{}')")]
    public void OnlyWhatReadsIsCalled(string statement)
    {
        using var box = new Sandbox(_catalog);
        Assert.That(() => box.CheckStatement(statement), Throws.InstanceOf<QueryRefusedException>(), statement);
        Assert.That(() => box.Query(statement), Throws.InstanceOf<QueryRefusedException>(), statement);
    }

    /// <summary>Every way of asking that reads stays open: the allow-list is not meant to cost a question.</summary>
    [TestCase("SELECT count(*) FROM query('SELECT count(*) FROM psms')")]
    [TestCase("SELECT * FROM query($$SELECT 1 AS a$$)")]
    [TestCase("SELECT * FROM query('SELECT * FROM query(''SELECT 1 AS a'')')")]
    [TestCase("SELECT count(*) FROM query_table('psms')")]
    [TestCase("PRAGMA version")]
    [TestCase("pragma platform")]
    [TestCase("PRAGMA table_info('psms')")]
    [TestCase("PRAGMA show_tables")]
    [TestCase("PRAGMA database_size")]
    [TestCase("SHOW TABLES")]
    [TestCase("DESCRIBE psms")]
    [TestCase("SUMMARIZE SELECT 1 AS a")]
    [TestCase("SELECT * FROM duckdb_functions() LIMIT 1")]
    [TestCase("SELECT * FROM main.range(3)")]
    [TestCase("SELECT * FROM \"RANGE\"(3)")]
    [TestCase("SELECT * FROM unnest([1, 2])")]
    [TestCase("SELECT * FROM generate_series(1, 2)")]
    [TestCase("VALUES (1)")]
    [TestCase("TABLE psms")]
    [TestCase("FROM psms")]
    public void WhatReadsIsAnswered(string statement)
    {
        using var box = new Sandbox(_catalog);
        Assert.That(box.Query(statement).Columns, Is.Not.Empty, statement);
    }

    /// <summary>The table functions and macros kept off <see cref="Sandbox.ReadingTableFunctions"/>, each read by name.</summary>
    private static readonly string[] RefusedTableFunctions =
    [
        "arrow_scan", "arrow_scan_dumb", "check_peg_parser", "checkpoint", "disable_logging", "disable_peg_parser", "disable_profiling",
        "enable_logging", "enable_peg_parser", "enable_profiling", "force_checkpoint", "json_execute_serialized_sql", "seq_scan",
        "truncate_duckdb_logs",
    ];

    /// <summary>The PRAGMAs kept off <see cref="Sandbox.ReadingPragmas"/>, each read by name.</summary>
    private static readonly string[] RefusedPragmas =
    [
        "add_parquet_key", "all_profiling_output", "copy_database", "disable_checkpoint_on_shutdown", "disable_object_cache",
        "disable_optimizer", "disable_print_progress_bar", "disable_profile", "disable_profiling", "disable_progress_bar",
        "disable_verification", "disable_verify_external", "disable_verify_fetch_row", "disable_verify_parallelism",
        "disable_verify_serializer", "enable_checkpoint_on_shutdown", "enable_object_cache", "enable_optimizer",
        "enable_print_progress_bar", "enable_profile", "enable_profiling", "enable_progress_bar", "enable_verification",
        "force_checkpoint", "import_database", "json_execute_serialized_sql", "verify_external", "verify_fetch_row",
        "verify_parallelism", "verify_serializer",
    ];

    /// <summary>A function a new DuckDB adds is a decision, and this is what forces it: the same rule as a manifest field
    /// that must be classified content or not. A name here is one somebody read.</summary>
    [Test]
    public void EveryCallableDuckDbOffersIsADecision()
    {
        using var box = new Sandbox(_catalog);
        List<string> Offered(string types) => box.Query($"SELECT DISTINCT function_name FROM duckdb_functions() WHERE function_type IN ({types})")
            .Rows.Select(r => (string)r[0]!).ToList();
        var tables = Offered("'table', 'table_macro'");
        var pragmas = Offered("'pragma'");
        Assert.Multiple(() =>
        {
            Assert.That(tables.Except(Sandbox.ReadingTableFunctions).Except(RefusedTableFunctions), Is.Empty, "unclassified table functions");
            Assert.That(pragmas.Except(Sandbox.ReadingPragmas).Except(RefusedPragmas), Is.Empty, "unclassified pragmas");
            Assert.That(Sandbox.ReadingTableFunctions.Concat(RefusedTableFunctions).Except(tables), Is.Empty, "table functions DuckDB no longer offers");
            Assert.That(Sandbox.ReadingPragmas.Concat(RefusedPragmas).Except(pragmas), Is.Empty, "pragmas DuckDB no longer offers");
            Assert.That(Sandbox.ReadingTableFunctions.Intersect(RefusedTableFunctions), Is.Empty);
            Assert.That(Sandbox.ReadingPragmas.Intersect(RefusedPragmas), Is.Empty);
        });
        foreach (var name in RefusedTableFunctions)
            Assert.That(() => box.CheckStatement($"SELECT * FROM {name}()"), Throws.InstanceOf<QueryRefusedException>(), name);
        foreach (var name in RefusedPragmas)
            Assert.That(() => box.CheckStatement($"PRAGMA {name}"), Throws.InstanceOf<QueryRefusedException>(), name);
    }

    /// <summary>Checking the wrapped statement must not change how the whole one is answered.</summary>
    [Test]
    public void AnExplainedSelectKeepsItsOwnErrors()
    {
        using var box = new Sandbox(_catalog);
        Assert.That(() => box.Query("EXPLAIN SELECT * FROM no_such_table"), Throws.InstanceOf<CatalogException>().With.Message.Contain("no_such_table"));
        Assert.That(() => box.Query("EXPLAIN SELECT ?"), Throws.InstanceOf<CatalogException>().With.Message.Contain("Values were not provided"));
    }

    [Test]
    public void ABadColumnIsACatalogErrorWithDuckDbsOwnMessage()
    {
        using var box = new Sandbox(_catalog);
        Assert.That(Assert.Throws<CatalogException>(() => box.Query("SELECT nope FROM psms"))!.Message, Does.Contain("nope"));
        // DuckDB.NET would refuse these itself, in its own words; Python's execute() gets DuckDB's.
        Assert.That(Assert.Throws<CatalogException>(() => box.Query("SELECT ?, ?"))!.Message,
            Is.EqualTo("Invalid Input Error: Values were not provided for the following prepared statement parameters: 1, 2"));
        Assert.That(Assert.Throws<CatalogException>(() => box.Query("SELECT $b, $a"))!.Message,
            Is.EqualTo("Invalid Input Error: Values were not provided for the following prepared statement parameters: a, b"));
    }

    // --- the caps --------------------------------------------------------------------------------------

    [Test]
    public void TheRowCapIsObservedNotInferred()
    {
        using var box = new Sandbox(_catalog, rowCap: 1);
        var result = box.Query("SELECT 1 UNION ALL SELECT 2");
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Truncated && result.TruncatedBy == "rows");
        var exact = box.Query("SELECT 1");
        Assert.That(exact.Truncated, Is.False, "a result exactly at the cap is whole");
    }

    [Test]
    public void TheCharacterCapDropsWholeRows()
    {
        using var box = new Sandbox(_catalog, charCap: 60);
        var result = box.Query("SELECT psm_id, usi, peptidoform FROM psms");
        Assert.That(result.Truncated && result.TruncatedBy == "characters");
        Assert.That(result.Rows.All(r => r.Length == 3));
    }

    [Test]
    public void TheCapsInForceAreReportedAndMaxRowsOnlyTightens()
    {
        using var box = new Sandbox(_catalog, timeoutSeconds: 7, rowCap: 5, charCap: 500);
        var caps = box.Query("SELECT 1").Caps;
        Assert.That(PydanticJson.Serialize(caps, indent: null), Is.EqualTo("{\"max_rows\":5,\"max_characters\":500,\"timeout_seconds\":7.0}"));
        Assert.That(box.Query("SELECT 1", rowCap: 100).Caps["max_rows"], Is.EqualTo(5L));
        Assert.That(box.Query("SELECT 1", rowCap: 2).Caps["max_rows"], Is.EqualTo(2L));
    }

    // --- the watchdog ----------------------------------------------------------------------------------

    [Test]
    public void TheMetadataProbeNoLongerDemonstratesATimeout()
    {
        // The lesson in CLAUDE.md: DuckDB 1.5 answers this from metadata, so a test built on it tests nothing.
        using var box = new Sandbox(_catalog, timeoutSeconds: 30);
        var watch = Stopwatch.StartNew();
        Assert.That(box.Query("SELECT count(*) FROM range(3000000000)").Rows[0][0], Is.EqualTo(3_000_000_000L));
        Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(10));
    }

    [Test]
    public void ALongQueryIsStoppedAtTheDeadlineAndTheConnectionSurvives()
    {
        using var box = new Sandbox(_catalog, timeoutSeconds: 2);
        var watch = Stopwatch.StartNew();
        var e = Assert.Throws<QueryTimeoutException>(() => box.Query(SlowQuery));
        var elapsed = watch.Elapsed.TotalSeconds;
        TestContext.Out.WriteLine($"interrupted after {elapsed:F2} s of a 2 s deadline");
        Assert.That(e!.Message, Is.EqualTo("query ran longer than 2 s and was stopped. Narrow it -- add a WHERE on dataset_id, or aggregate instead of returning rows."));
        Assert.That(elapsed, Is.GreaterThanOrEqualTo(1.9).And.LessThan(20));
        Assert.That(box.Query("SELECT count(*) FROM datasets").Rows[0][0], Is.EqualTo(1L));
    }

    [Test]
    public void TheProbeReallyRunsLong()
    {
        // The probe must still be running at a deadline five times the one above, or the watchdog test proves nothing.
        using var box = new Sandbox(_catalog, timeoutSeconds: 10);
        var watch = Stopwatch.StartNew();
        Assert.Throws<QueryTimeoutException>(() => box.Query(SlowQuery));
        TestContext.Out.WriteLine($"still running at {watch.Elapsed.TotalSeconds:F2} s");
        Assert.That(watch.Elapsed.TotalSeconds, Is.GreaterThanOrEqualTo(9.9));
    }

    [Test]
    public void TheWatchdogDoesNotFireOnAFastQuery()
    {
        using var box = new Sandbox(_catalog, timeoutSeconds: 30);
        for (var i = 0; i < 3; i++) Assert.That(box.Query("SELECT count(*) FROM psms").RowCount, Is.EqualTo(1));
    }

    [Test]
    public void NoCatalogIsAnErrorAnOperatorCanActOn()
    {
        Assert.That(Assert.Throws<CatalogException>(() => _ = new Sandbox(Path.Combine(_scratch, "missing.duckdb")))!.Message, Does.StartWith("no catalog at"));
    }

    // --- what a statement reads -------------------------------------------------------------------------

    [Test]
    public void ACteNamedAfterARealTableCertifiesNothing()
    {
        using var box = new Sandbox(_catalog);
        Assert.That(box.ReferencedTables("WITH protein_groups_1pct AS (SELECT 99999 AS n) SELECT * FROM protein_groups_1pct"), Is.Empty);
        Assert.That(box.ReferencedTables("SELECT * FROM psms p JOIN (SELECT * FROM runs) r USING (run_id)"), Is.EqualTo(new[] { "psms", "runs" }));
        Assert.That(box.ReferencedTables("SELECT count(*) FROM query_table('psms')"), Is.Null, "unknown is not empty");
        Assert.That(box.ReferencedColumns("SELECT p.PEP AS x FROM psms p")!.Value.Names, Does.Contain("pep"));
        Assert.That(box.ReferencedColumns("SELECT * FROM psms")!.Value.Star, Is.True);
    }

    // --- the server ------------------------------------------------------------------------------------

    [Test]
    public void ThreeToolsShipAndTheSqlDescriptionStatesTheCapsItEnforces()
    {
        Assert.That(Mcp.Mcp.ToolSpecs.Select(s => s.Name), Is.EqualTo(new[] { "datarepo_describe", "datarepo_search", "datarepo_sql" }));
        Assert.That(Mcp.Mcp.ToolSpecs[2].Description, Does.Contain("1,000 rows or 50,000 characters").And.Contain("after 30 s is stopped"));
    }

    [Test]
    public void EveryErrorPayloadCarriesAHint()
    {
        foreach (var e in new DataRepoException[] { new QueryRefusedException("x"), new QueryTimeoutException("x"), new ToolException("x"), new CatalogException("x"), new IngestException("x") })
        {
            var payload = Mcp.Mcp.ErrorPayload(e);
            Assert.That(payload.Keys, Is.EqualTo(new[] { "error", "message", "hint" }));
            Assert.That((string)payload["hint"]!, Is.Not.Empty);
        }
        Assert.That(Mcp.Mcp.ErrorPayload(new QueryRefusedException("x"))["error"], Is.EqualTo("QueryRefused"));
        Assert.That(Mcp.Mcp.ErrorPayload(new IngestException("x"))["hint"], Is.EqualTo("Call datarepo_describe() for what this catalog holds."));
    }

    [Test]
    public void AReplacedCatalogFileIsAnnouncedNotSilentlyServed()
    {
        var file = Unopened();
        using var server = new CatalogServer(file);
        Assert.That(server.CatalogFileChanged, Is.Null);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        var answer = server.Describe("tables");
        var provenance = (Dictionary<string, object?>)answer["provenance"]!;
        Assert.That((string)provenance["catalog_file_changed"]!, Does.Contain("has changed since this server opened it"));
    }

    /// <summary>The whole MCP path in process: the SDK client lists the tools with the Python server's schemas and
    /// gets each tool's answer as the text <see cref="Mcp.Mcp.Call"/> renders.</summary>
    /// <summary>Python's tool text with the one sentence D48 step 2 changed: PSMs left the catalog, so the sql
    /// tool no longer names <c>psms_1pct</c> and says where the PSM counts are.</summary>
    private static string SinceStep2(string python) => python.Replace(
        "Prefer the *_1pct views (psms_1pct, peptidoforms_1pct, protein_groups_1pct): they apply the producing search "
        + "engine's acceptance rule, so you do not have to restate it. Call describe",
        "Prefer the *_1pct views (peptidoforms_1pct, protein_groups_1pct): they apply the producing search engine's "
        + "acceptance rule, so you do not have to restate it. PSMs are not in the catalog; dataset_overview has each "
        + "dataset's PSM counts. Call describe", StringComparison.Ordinal);

    [Test]
    public async Task TheSdkServesTheToolsAsThePythonServerDoes()
    {
        using var catalog = new CatalogServer(_catalog);
        var toServer = new Pipe();
        var toClient = new Pipe();
        var options = Mcp.Mcp.ServerOptions(catalog);
        await using var serverTransport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "datarepo", null);
        await using var server = McpServer.Create(serverTransport, options);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = server.RunAsync(stop.Token);
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), null), cancellationToken: stop.Token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: stop.Token);
            var python = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "mcp", "python_tools.json")))!.AsArray();
            Assert.That(tools.Select(t => t.Name), Is.EqualTo(python.Select(t => (string)t!["name"]!)));
            foreach (var (tool, expected) in tools.Zip(python))
            {
                Assert.That(tool.ProtocolTool.Title, Is.EqualTo((string)expected!["title"]!));
                Assert.That(tool.Description, Is.EqualTo(SinceStep2((string)expected["description"]!)));
                Assert.That(JsonNode.DeepEquals(JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()), expected["inputSchema"]), tool.Name);
                Assert.That(JsonNode.DeepEquals(JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText()), expected["outputSchema"]), tool.Name);
            }

            async Task<CallToolResult> Call(string name, Dictionary<string, object?> args) =>
                await client.CallToolAsync(name, args, cancellationToken: stop.Token);

            var described = await Call("datarepo_describe", new() { ["target"] = "psms" });
            Assert.That(described.IsError, Is.Not.True);
            Assert.That(((TextContentBlock)described.Content[0]).Text, Is.EqualTo(PydanticJson.Serialize(Mcp.Mcp.Call(catalog, "describe", target: "psms"))));
            Assert.That(described.StructuredContent!.Value.GetProperty("table").GetString(), Is.EqualTo("psms"));

            var searched = await Call("datarepo_search", new() { ["query"] = "P02768" });
            Assert.That(((TextContentBlock)searched.Content[0]).Text, Is.EqualTo(PydanticJson.Serialize(Mcp.Mcp.Call(catalog, "search", query: "P02768"))));

            var refused = await Call("datarepo_sql", new() { ["query"] = "DROP TABLE psms" });
            Assert.That(refused.IsError, Is.Not.True, "a refusal is an answer the agent can act on, not a transport error");
            Assert.That(refused.StructuredContent!.Value.GetProperty("error").GetString(), Is.EqualTo("QueryRefused"));

            var counted = await Call("datarepo_sql", new() { ["query"] = "SELECT count(*) AS n FROM psms", ["max_rows"] = 5 });
            Assert.That(counted.StructuredContent!.Value.GetProperty("rows")[0][0].GetInt64(), Is.EqualTo(60));

            var invalid = await Call("datarepo_search", new() { ["query"] = "x", ["kind"] = "nope" });
            Assert.That(invalid.IsError, Is.True);
            Assert.That(((TextContentBlock)invalid.Content[0]).Text, Is.EqualTo(
                "Error executing tool datarepo_search: 1 validation error for datarepo_searchArguments\n"
                + "kind\n  Input should be 'dataset', 'protein', 'peptide', 'modification', 'sample', 'run', 'definition' or 'localization' "
                + "[type=literal_error, input_value='nope', input_type=str]\n"
                + "    For further information visit https://errors.pydantic.dev/2.12/v/literal_error"));
        }
        await stop.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }
    }

    // --- datarepo mcp ----------------------------------------------------------------------------------

    private static (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = McpCommand.Run(args, stdout, stderr);
        return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
    }

    [Test]
    public void TheCatalogIsTheOneNamedNeverDiscovered()
    {
        var (code, _, stderr) = Run();
        Assert.That(code, Is.EqualTo(2));
        Assert.That(stderr, Is.EqualTo("datarepo mcp: --catalog is required (D13: one catalog, by explicit path)\n"));
        Assert.That(Run("--bogus").Code, Is.EqualTo(2));
        Assert.That(Run("--help").Stdout, Does.StartWith("usage: datarepo mcp [-h] [--catalog CATALOG]"));
        var missing = Run("--catalog", Path.Combine(_scratch, "nope.duckdb"), "--check");
        Assert.That(missing.Code, Is.EqualTo(1));
        Assert.That(missing.Stderr, Does.StartWith("datarepo: no catalog at"));
    }

    [Test]
    public void CheckOpensTheCatalogAndReportsWithoutServing()
    {
        var (code, stdout, _) = Run("--catalog", _catalog, "--check");
        Assert.That(code, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("  id       153cf55067d58441\n"));
        Assert.That(stdout, Does.Contain("  built    2026-10-04T16:03:54+00:00 by datarepo 0.32.0\n"));
        Assert.That(stdout, Does.Contain("  dataset  PXD999999           58 PSMs at 1%\n"));
        Assert.That(stdout, Does.Contain("  tools    datarepo_describe, datarepo_search, datarepo_sql\n"));
        Assert.That(stdout, Does.Contain("  empty    26 table(s) present with no rows\n"));
    }

    [Test]
    public void InstallWritesAConfigThatNamesTheCatalogAbsolutelyAndTheExecutable()
    {
        var config = Path.Combine(_scratch, $"claude-{Guid.NewGuid():N}.json");
        var result = Mcp.Mcp.Install(_catalog, config: config, executable: @"C:\tools\datarepo.exe");
        Assert.That(result["action"], Is.EqualTo("added"));
        var written = JsonNode.Parse(File.ReadAllText(config))!;
        var entry = written["mcpServers"]!["datarepo"]!;
        Assert.That((string)entry["command"]!, Is.EqualTo(@"C:\tools\datarepo.exe"));
        Assert.That(entry["args"]!.AsArray().Select(a => (string)a!), Is.EqualTo(new[] { "mcp", "--catalog", Path.GetFullPath(_catalog) }));
        Assert.That(entry["env"]!.AsObject(), Is.Empty);
        Assert.That(File.ReadAllText(config), Does.EndWith(Environment.NewLine));

        Assert.That(Mcp.Mcp.Install(_catalog, config: config, executable: @"C:\tools\datarepo.exe")["action"], Is.EqualTo("unchanged"), "installing twice is a no-op");
        var other = Unopened();
        var e = Assert.Throws<ToolException>(() => Mcp.Mcp.Install(other, config: config, executable: @"C:\tools\datarepo.exe"));
        Assert.That(e!.Message, Does.Contain("already has an MCP server named 'datarepo' pointing at 'mcp --catalog "));
        Assert.That(Mcp.Mcp.Install(other, config: config, force: true, executable: @"C:\tools\datarepo.exe")["action"], Is.EqualTo("updated"));
        Assert.That(Mcp.Mcp.Install(_catalog, name: "second", config: config, executable: @"C:\tools\datarepo.exe")["action"], Is.EqualTo("added"));
        Assert.That(Mcp.Mcp.InstalledEntries(config).Keys, Is.EquivalentTo(new[] { "datarepo", "second" }));
    }

    [Test]
    public void InstallKeepsTheRestOfTheConfigAndRefusesOneItCannotParse()
    {
        var config = Path.Combine(_scratch, $"claude-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, "{\"theme\": \"dark\", \"numStartups\": 12, \"ratio\": 0.5, \"mcpServers\": {\"other\": {\"command\": \"node\", \"args\": [\"x.js\"]}}}");
        Mcp.Mcp.Install(_catalog, config: config, executable: "datarepo");
        var written = JsonNode.Parse(File.ReadAllText(config))!;
        Assert.That((string)written["theme"]!, Is.EqualTo("dark"));
        Assert.That((double)written["ratio"]!, Is.EqualTo(0.5));
        Assert.That(written["mcpServers"]!.AsObject().Select(kv => kv.Key), Is.EqualTo(new[] { "other", "datarepo" }));
        Assert.That(Mcp.Mcp.InstalledEntries(config).Keys, Is.EqualTo(new[] { "datarepo" }), "a server that is not ours is not listed");

        var broken = Path.Combine(_scratch, $"broken-{Guid.NewGuid():N}.json");
        File.WriteAllText(broken, "{not json");
        var e = Assert.Throws<ToolException>(() => Mcp.Mcp.Install(_catalog, config: broken, executable: "datarepo"));
        Assert.That(e!.Message, Does.StartWith($"{broken} is not valid JSON ("));
        Assert.That(File.ReadAllText(broken), Is.EqualTo("{not json"), "a config it cannot read is left alone");
        Assert.That(Assert.Throws<CatalogException>(() => Mcp.Mcp.Install(Path.Combine(_scratch, "nope.duckdb"), config: broken))!.Message, Does.EndWith("; nothing to serve"));
    }

    [Test]
    public void TheCommandLineInstallsAndListsThroughAScratchConfig()
    {
        var config = Path.Combine(_scratch, $"claude-{Guid.NewGuid():N}.json");
        var empty = Run("--list", "--config", config);
        Assert.That(empty.Stdout, Is.EqualTo("no datarepo MCP server is registered\n"));
        var (code, stdout, _) = Run("--catalog", _catalog, "--install", "--config", config);
        Assert.That(code, Is.EqualTo(0));
        var (command, prefix) = Mcp.Mcp.RunningExecutable();
        var args = string.Join(" ", prefix.Concat(["mcp", "--catalog", Path.GetFullPath(_catalog)]));
        Assert.That(stdout, Is.EqualTo(
            $"added  datarepo in {config}\n  command  {command} {args}\n"
            + "  tools    datarepo_describe, datarepo_search, datarepo_sql\n  restart Claude Code to pick it up\n"));
        Assert.That(Run("--catalog", _catalog, "--install", "--config", config).Stdout, Does.StartWith("unchanged  datarepo in "));
        Assert.That(Run("--list", "--config", config).Stdout, Is.EqualTo($"datarepo  {command} {args}\n"));
    }
}
