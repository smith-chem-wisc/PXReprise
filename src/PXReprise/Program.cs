using PXReprise.Census;
using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Provenance;
using UsefulProteomicsDatabases;

namespace PXReprise;

/// <summary>
/// <c>pxreprise &lt;verb&gt; [args]</c>. One JSON envelope on stdout, diagnostics on stderr, exit 0 ok / 1 handled
/// failure / 2 usage: the mzLib bridge's contract, so the engine is scriptable from any language.
/// </summary>
public static class Program
{
    /// <summary>The verbs, listed by the <c>version</c> verb. Keep in step with <see cref="Dispatch"/>.</summary>
    public static readonly string[] Verbs = { "version", "profiles", "validate", "census", "gate", "rank", "chemistry", "labels", "fetch", "qc", "search", "qc-payload", "differential", "batch", "library", "worker" };

    /// <summary>Where profiles are read from when --profiles is not given: beside the executable.</summary>
    public static string DefaultProfilesDir => Path.Combine(AppContext.BaseDirectory, "profiles");

    public static async Task<int> Main(string[] argv) => await RunAsync(argv, Console.Out, CancellationToken.None);

    public static async Task<int> RunAsync(string[] argv, TextWriter stdout, CancellationToken ct,
        Func<IProjectSearch>? searchFactory = null, Func<Census.IRankSource>? rankSource = null)
    {
        try
        {
            var args = new Arguments(argv, new HashSet<string>(StringComparer.Ordinal) { "queue", "write" });
            if (args.Words.Count == 0) throw new UsageException($"a verb is required: {string.Join(", ", Verbs)}");
            object? data = await Dispatch(args, ct, searchFactory, rankSource).ConfigureAwait(false);
            return Envelope.Ok(stdout, data);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return Envelope.Fail(stdout, e);
        }
    }

    private static async Task<object?> Dispatch(Arguments args, CancellationToken ct, Func<IProjectSearch>? searchFactory, Func<Census.IRankSource>? rankSource = null)
    {
        switch (args.Words[0])
        {
            case "version":
                Positional(args, 0);
                args.AllowOnly();
                return new { tools = RunRecord.Versions(), verbs = Verbs };

            case "profiles":
                Positional(args, 0);
                args.AllowOnly("profiles");
                return ProfileLoader.LoadDirectory(ProfilesDir(args)).Values.Select(p => new
                {
                    key = p.Key, status = p.Status, p.Description, metamorpheus = p.MetaMorpheus,
                    organisms = p.Databases.Keys,
                });

            case "validate":
            {
                string file = Positional(args, 1)[0];
                args.AllowOnly("profiles");
                var profiles = ProfileLoader.LoadDirectory(ProfilesDir(args));
                var q = QuestionLoader.Load(file);
                var missing = q.Profiles.Where(p => !profiles.ContainsKey(p)).ToList();
                if (missing.Count > 0)
                    throw new ConfigException(file, $"profile(s) not defined: {string.Join(", ", missing)}");
                QuestionLoader.CheckAgainstProfiles(q, profiles);
                // [designs]: the folder must exist, and the deposits it holds a design for are listed (aging 030 asked
                // whether validate reads it). Each design is checked against the deposit's files when it is searched.
                if (q.Designs is { } dg && !Directory.Exists(dg.Dir))
                    throw new ConfigException(file, $"[designs] dir {dg.Dir} does not exist");
                // A deprecated profile still loads, but takes no new search: say which ones the question still names.
                var deprecated = PXReprise.Discovery.Router.DeprecatedProfiles(q, profiles);
                foreach (string k in deprecated)
                    Console.Error.WriteLine($"note: {k} is deprecated: it takes no new searches; a deposit only it accepts waits");
                return new
                {
                    question = q.Name, q.Profiles, keywords = q.Keywords.Count, q.Organisms,
                    deprecated_profiles = deprecated.Count > 0 ? deprecated : null,
                    decisions = q.Relevance.Decisions.Count, holds = q.Holds.Count, overlays = q.Overlays.Keys,
                    // v0.4.43: each split deposit with the search keys it is searched and delivered under.
                    splits = q.Splits is { Count: > 0 } sp ? sp.ToDictionary(kv => kv.Key, kv => kv.Value) : null,
                    designs = q.Designs is not { } d ? null : new
                    {
                        dir = d.Dir,
                        deposits = Directory.EnumerateFiles(d.Dir, "*.sdrf.tsv").Select(f => Path.GetFileName(f)[..^".sdrf.tsv".Length])
                            .OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        condition_columns = d.ConditionColumns,
                    },
                };
            }

            case "chemistry":
            {
                // chemistry --accessions FILE [--designs DIR] [--out DIR]: each deposit's protease, alkylation and label
                // (G19, D22-D24) from PRIDE's record and the deposit's / question's SDRF; no spectra. One accession per line.
                args.AllowOnly("accessions", "designs", "out");
                var accs = File.ReadAllLines(args.Required("accessions")).Select(l => l.Split('\t')[0].Trim())
                    .Where(a => a.StartsWith("PXD", StringComparison.Ordinal)).Distinct().ToList();
                string outDir = args.Option("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "chemistry");
                Directory.CreateDirectory(outDir);
                var record = new RunRecord("chemistry");
                record.Input(args.Required("accessions"));
                using var client = new PrideArchiveClient();
                IProjectSearch search = searchFactory?.Invoke() ?? new PrideProjectSearch(client);
                var rows = await new Census.ChemistryRunner(search, rankSource?.Invoke() ?? new Census.PrideRankSource(client))
                    .RunAsync(accs, args.Option("designs"), outDir, record, ct).ConfigureAwait(false);
                record.Write(outDir);
                return new
                {
                    deposits = rows.Count, out_dir = Path.GetFullPath(outDir),
                    differ_from_v1 = rows.Count(r => r.DiffersFromV1().Count > 0),
                    parked = rows.Count(r => r.Chemistry?.Park is not null),
                    errors = rows.Count(r => r.Chemistry is null),
                };
            }

            case "labels":
            {
                // labels <deposit dir>... [--decisions FILE] [--out DIR]: each run's sample type and the search's cysteine
                // chemistry (D94), from what the deposit's folder records; no spectra. Written as <out>/labels.json, by
                // default the deposit's own 07_labels; with several deposits, --out holds one folder per dataset. A
                // deposit that cannot be labelled (no search yet) is listed under "failed" when several are given.
                args.AllowOnly("decisions", "out");
                var dirs = args.Words.Skip(1).ToList();
                if (dirs.Count == 0) throw new UsageException("labels needs a deposit folder");
                var decisions = args.Option("decisions") is { } df ? Labels.DepositLabelReader.LoadDecisions(df) : [];
                var written = new List<object>();
                var failed = new List<object>();
                foreach (var dir in dirs)
                {
                    Labels.DepositLabels labels;
                    try { labels = Labels.DepositLabelReader.Read(dir, decisions); }
                    catch (InvalidDataException e) when (dirs.Count > 1)
                    {
                        // One deposit that cannot be labelled (no search yet) does not stop the others; it is reported.
                        failed.Add(new { dataset = new DirectoryInfo(dir).Name, error = e.Message });
                        continue;
                    }
                    string outDir = args.Option("out") is { } o
                        ? (dirs.Count == 1 ? o : Path.Combine(o, labels.Dataset))
                        : Path.Combine(dir, Labels.DepositLabelReader.Folder);
                    var record = new RunRecord("labels");
                    foreach (var input in new[] { Path.Combine(dir, "04_search", "provenance.json"), Path.Combine(dir, Discovery.ExperimentRecord.Folder, Discovery.ExperimentRecord.JsonFile), args.Option("decisions") })
                        if (input is not null && File.Exists(input)) record.Input(input);
                    string file = Labels.DepositLabelReader.Write(labels, outDir);
                    record.Output(file);
                    record.Write(outDir);
                    written.Add(new
                    {
                        dataset = labels.Dataset, file = Path.GetFullPath(file),
                        sample_types = labels.Runs.GroupBy(r => r.Value).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                        cysteine = labels.Cysteine.Verdict,
                    });
                }
                return new { labelled = written, failed };
            }

            case "rank":
            {
                // rank <census dir> [--only file] [--out dir] [--large-gb 150] [--max-files 60]: orders a census's queueable
                // deposits biggest first within the limits (the maintainer, 2026-10-05), from PRIDE file listings; no spectra.
                // The design read from a deposited SDRF or guessed from file names is written alongside, for information.
                string censusDir = Positional(args, 1)[0];
                args.AllowOnly("only", "out", "large-gb", "max-files");
                IReadOnlyCollection<string>? only = args.Option("only") is { } of
                    ? File.ReadAllLines(of).Select(l => l.Split('\t')[0].Trim()).Where(a => a.StartsWith("PXD", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal)
                    : null;
                string outDir = args.Option("out") ?? Path.Combine(censusDir, "rank");
                double largeGb = args.Option("large-gb") is { } lg ? double.Parse(lg, System.Globalization.CultureInfo.InvariantCulture) : 150;
                int maxFiles = args.Option("max-files") is { } mf ? int.Parse(mf, System.Globalization.CultureInfo.InvariantCulture) : 60;   // label-free-dda@1 [deposit] max_files
                Directory.CreateDirectory(outDir);
                var record = new RunRecord("rank");
                record.Note("PRIDE is a live index: this ranking is a dated snapshot.");
                using var client = new PrideArchiveClient();
                var rows = await new Census.RankRunner(rankSource?.Invoke() ?? new Census.PrideRankSource(client))
                    .RunAsync(censusDir, only, outDir, largeGb, maxFiles, record, ct).ConfigureAwait(false);
                record.Write(outDir);
                return new
                {
                    ranked = rows.Count, out_dir = Path.GetFullPath(outDir),
                    by_tier = rows.GroupBy(r => r.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g => g.Count()),
                    by_design_source = rows.GroupBy(r => r.Design.Source).ToDictionary(g => g.Key, g => g.Count()),
                };
            }

            case "census":
            {
                string file = Positional(args, 1)[0];
                // --queue: also install the census's queue.json as the question's batch queue, the input of `batch run`.
                args.AllowOnly("profiles", "out", "queue");
                var profiles = ProfileLoader.LoadDirectory(ProfilesDir(args));
                var q = QuestionLoader.Load(file);
                string? installQueue = null;
                if (args.Has("queue"))
                {
                    installQueue = (q.Batch ?? throw new ConfigException(q.SourceFile, "--queue needs a [batch] table naming the queue file")).Queue;
                    // A batch's queue is its running order; replacing it under a batch is the operator's decision, not a census's.
                    if (File.Exists(installQueue))
                        throw new UsageException($"{installQueue} already exists; a batch may be using it. Delete or rename it first to replace it.");
                }
                string outDir = args.Option("out")
                                ?? Path.Combine(Path.GetDirectoryName(q.SourceFile)!, "census", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                var record = new RunRecord("census");
                record.Input(q.SourceFile);
                foreach (string key in q.Profiles)
                    if (profiles.TryGetValue(key, out _))
                        record.Note($"profile {key}");
                record.Note("PRIDE is a live index: this census is a dated snapshot, not reproducible by re-running.");

                IProjectSearch search;
                PrideArchiveClient? client = null;
                if (searchFactory is not null) search = searchFactory();
                else search = new PrideProjectSearch(client = new PrideArchiveClient());
                try
                {
                    var (summary, _) = await new CensusRunner(search).RunAsync(q, profiles, outDir, record, ct).ConfigureAwait(false);
                    string summaryFile = Path.Combine(outDir, "summary.json");
                    await File.WriteAllTextAsync(summaryFile,
                        System.Text.Json.JsonSerializer.Serialize(summary, Envelope.Json), ct).ConfigureAwait(false);
                    record.Output(summaryFile);
                    if (installQueue is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(installQueue)!);
                        File.Copy(Path.Combine(outDir, "queue.json"), installQueue);
                        record.Output(installQueue);
                    }
                    record.Write(outDir);
                    return summary with { QueueInstalled = installQueue };
                }
                finally
                {
                    client?.Dispose();
                }
            }

            case "gate":
            {
                // v0.4.46 (part A5): the question's gate over a list of accessions (one a line, or a queue.json),
                // writing the census's gate.tsv rows and changing nothing.
                string file = Positional(args, 1)[0];
                args.AllowOnly("list", "out");
                var q = QuestionLoader.Load(file);
                var terms = q.Gate ?? throw new UsageException($"{q.SourceFile} has no [gate] table");
                string listFile = args.Required("list");
                string listText = await File.ReadAllTextAsync(listFile, ct).ConfigureAwait(false);
                var accessions = listText.TrimStart().StartsWith('[')
                    ? System.Text.Json.Nodes.JsonNode.Parse(listText)!.AsArray().Select(e => e?["accession"]?.GetValue<string>()).OfType<string>().ToList()
                    : listText.Split('\n').Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToList();
                string outDir = args.Option("out") ?? Path.Combine(Path.GetDirectoryName(q.SourceFile)!, "gate", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(outDir);
                var record = new RunRecord("gate");
                record.Input(q.SourceFile);
                record.Input(listFile);
                record.Note("report mode: nothing queued, removed or changed; PRIDE and ProteomeXchange are live, so this is a dated reading");
                IProjectSearch search;
                PrideArchiveClient? client = null;
                if (searchFactory is not null) search = searchFactory();
                else search = new PrideProjectSearch(client = new PrideArchiveClient());
                try
                {
                    var rows = new List<(string, IReadOnlyCollection<string>, GateDecision)>();
                    foreach (string acc in accessions.Distinct(StringComparer.Ordinal))
                    {
                        if (Gate.Decided(q, acc) is { } decided) { rows.Add((acc, Array.Empty<string>(), decided)); continue; }
                        GateText text;
                        PrideProject? project = null;
                        try { project = await search.TryGetProjectAsync(acc, ct).ConfigureAwait(false); }
                        catch (Exception e) when (Envelope.IsUnavailable(e) && !ct.IsCancellationRequested) { record.Note($"{acc} PRIDE unavailable: {e.Message}"); }
                        if (project is not null) text = GateText.From(project);
                        else if (await search.TryGetProteomeXchangeXmlAsync(acc, ct).ConfigureAwait(false) is { } xml) text = GateText.FromProteomeXchange(xml);
                        else { text = new GateText(GateTextSource.None, Array.Empty<(string, string)>()); record.Note($"{acc}: no text from PRIDE or ProteomeXchange"); }
                        var kws = Gate.KeywordsIn(q, text);
                        rows.Add((acc, kws.ToList(), Gate.Evaluate(terms, text, Gate.NoReferenceGroup(q, kws, text))));
                    }
                    string gateFile = Path.Combine(outDir, "gate.tsv");
                    Gate.WriteTsv(gateFile, rows);
                    record.Output(gateFile);
                    record.Write(outDir);
                    return new
                    {
                        deposits = rows.Count, file = Path.GetFullPath(gateFile),
                        outcomes = rows.GroupBy(r => r.Item3.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                    };
                }
                finally
                {
                    client?.Dispose();
                }
            }

            case "fetch":
            {
                string accession = Positional(args, 1)[0];
                args.AllowOnly("out", "machine", "pick", "max-files", "max-file-mb", "parallel", "attempts", "include", "exclude");
                var select = FileSelection(args.Option("include"), args.Option("exclude"));
                var machine = Machine.Load(args.Required("machine"));
                var pick = ProfileLoader.ParseEnum<Fetch.Pick>("--pick", "pick", args.Option("pick") ?? "all");
                var request = new Fetch.FetchRequest(accession, args.Required("out"), pick,
                    int.Parse(args.Option("max-files") ?? "60"), int.Parse(args.Option("max-file-mb") ?? "5000"), ".raw",
                    int.Parse(args.Option("parallel") ?? "4"), int.Parse(args.Option("attempts") ?? "8"),
                    TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), machine.WorkRoot, TimeSpan.FromMinutes(machine.FetchStallMinutes),
                    Select: select,
                    Sha1Source: Fetch.Integrity.PrideChecksumsAsync,
                    Aspera: machine.AsperaAscp is { } ascp ? new Fetch.AsperaClient(ascp, machine.AsperaKey!, machine.AsperaAttempts, StartGap: TimeSpan.FromSeconds(10),
                        Stall: TimeSpan.FromMinutes(machine.FetchStallMinutes)) : null,
                    Https: Fetch.PrideHosts.Parse(machine.PrideHttps));
                using var client = new PrideArchiveClient();
                var manifest = await Fetch.FetchStage.RunAsync(request, new Fetch.PrideFiles(client), ct).ConfigureAwait(false);
                return new { accession, files = manifest["files"]!.AsArray().Count, manifest = Path.GetFullPath(Path.Combine(request.OutDir, "fetch_manifest.json")) };
            }

            case "qc":
            {
                Positional(args, 0);
                args.AllowOnly("spectra", "out", "profile", "profile-file", "profiles", "machine", "run-date");
                var profile = ResolveProfile(args);
                var machine = Machine.Load(args.Required("machine"));
                string outDir = args.Required("out");
                Directory.CreateDirectory(outDir);
                string paramsFile = Path.Combine(outDir, "params.json");
                await File.WriteAllTextAsync(paramsFile, System.Text.Json.JsonSerializer.Serialize(
                    new { profile = profile.Key, qc = profile.Qc, work_root = machine.WorkRoot }, Envelope.Json), ct).ConfigureAwait(false);
                var (report, allPass) = Qc.QcStage.Run(args.Required("spectra"), outDir, profile, machine, paramsFile, args.Option("run-date"));
                return new
                {
                    all_pass = allPass, files = report.Count,
                    failed = report.Where(kv => !kv.Value!["pass"]!.GetValue<bool>()).Select(kv => kv.Key).ToList(),
                    report = Path.GetFullPath(Path.Combine(outDir, "qc_report.json")),
                };
            }

            case "search":
            {
                Positional(args, 0);
                args.AllowOnly("spectra", "qc", "out", "organism", "profile", "profile-file", "profiles", "machine",
                    "extra-db", "exclude", "run-date", "accession", "design", "condition-columns", "enriched", "probe-offsets");
                var profile = ResolveProfile(args);
                if (args.Option("design") is { } given && !File.Exists(given)) throw new UsageException($"--design {given} does not exist");
                // D84: a probe's measured precursor offsets, as the batch passes them to a profile with
                // engine.precursor_window = "probe" (its state entry's probe_precursor_offsets_ppm), so a search re-run by hand
                // gets the window the batch would give it.
                var probeOffsets = args.Option("probe-offsets") is { } po ? ReadProbeOffsets(po) : null;
                // v0.4.40 (G66): the PTM classes the deposit enriches, as the batch reads them (library routing, and the variable
                // mods of a profile with engine.enriched_variable): --enriched phospho,GG.
                var enriched = List(args.Option("enriched"));
                if (enriched.FirstOrDefault(c => Search.LibraryRouting.Classes.All(k => k.Key != c)) is { } badClass)
                    throw new UsageException($"--enriched {badClass}: the PTM classes are {string.Join(", ", Search.LibraryRouting.Classes.Select(k => k.Key))}");
                var request = new Search.SearchRequest(profile, args.Required("organism"), Machine.Load(args.Required("machine")),
                    args.Required("spectra"), args.Required("out"), args.Required("qc"), List(args.Option("extra-db")),
                    List(args.Option("exclude")), args.Option("run-date"), args.Option("accession"),
                    CuratedDesign: args.Option("design"), DesignConditionColumns: args.Option("condition-columns") is null ? null : List(args.Option("condition-columns")),
                    // v0.4.45: a profile that reads each deposit's chemistry takes it from --design's SDRF here.
                    Chemistry: Search.SearchStage.ChemistryOutsideTheBatch(profile, args.Required("spectra"), args.Option("design")),
                    LibraryRouteClasses: enriched.Count > 0 ? enriched : null, ProbePrecursorOffsets: probeOffsets);
                return await Search.SearchStage.RunAsync(request, ct).ConfigureAwait(false);
            }

            case "qc-payload":
            {
                // pxreprise qc-payload <search_dir> <qc_dir> <out_dir> --accession PXD [--run-date D] [--machine m.toml]
                var pos = Positional(args, 3);
                args.AllowOnly("accession", "run-date", "machine", "metamorpheus");
                string accession = args.Required("accession");
                var machine = args.Option("machine") is { } mf ? Machine.Load(mf) : null;
                string outDir = pos[2];
                Directory.CreateDirectory(outDir);
                string paramsFile = Path.Combine(outDir, "params.json");
                await File.WriteAllTextAsync(paramsFile, System.Text.Json.JsonSerializer.Serialize(
                    new { stage = "qc_payload", accession, run_date = args.Option("run-date"), qc_python = machine?.QcPython }, Envelope.Json), ct).ConfigureAwait(false);
                string workRoot = machine?.WorkRoot ?? Path.GetDirectoryName(Path.GetFullPath(pos[0]))!;
                var o = Qc.QcPayloadStage.Run(pos[0], pos[1], outDir, accession, args.Option("run-date"), workRoot, paramsFile,
                    machine?.QcPython, args.Option("metamorpheus"));
                return new
                {
                    payload = o.PayloadFile, files = o.Files, protein_groups_quantified = o.ProteinGroupsQuantified,
                    bin_edges_source = o.BinSource, validate = o.Validate, render = o.Render, findings = o.FindingsFile, flags = o.Flags,
                    provenance = o.ProvenanceFile,
                };
            }

            case "differential":
            {
                // pxreprise differential --search <04_search> --out <06_differential> [--sdrf corrected.sdrf.tsv] [--threads N]
                // D84 (QuantProject 155): mzLib's differential analysis of one finished search, with QuantProject's defaults.
                Positional(args, 0);
                args.AllowOnly("search", "out", "sdrf", "threads");
                string searchDir = args.Required("search"), outDir = args.Required("out");
                if (!Directory.Exists(searchDir)) throw new UsageException($"--search {searchDir} does not exist");
                int threads = -1;
                if (args.Option("threads") is { } t && (!int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out threads) || threads < 1))
                    throw new UsageException($"--threads must be a whole number of 1 or more, not {t}");
                var record = Differential.DifferentialStage.Run(searchDir, outDir, args.Option("sdrf"), threads);
                return Differential.DifferentialStage.Summary(record, outDir);
            }

            case "batch":
            {
                // pxreprise batch run|status|stop <question.toml> [--machine m.toml] [--run-date D]
                // pxreprise batch retry <question.toml> <PXD> --reason "..."
                var rest = args.Words.Skip(1).ToList();
                // pxreprise batch research <question.toml> <PXD> --reason "..." [--profile key@n]
                // pxreprise batch restrictions <question.toml> [--manifest copy.yaml] [--write]
                if (!(rest.Count == 2 && rest[0] is ("run" or "status" or "stop" or "restrictions")) && !(rest.Count == 3 && rest[0] is ("retry" or "research")))
                    throw new UsageException("usage: pxreprise batch run|status|stop <question.toml> --machine m.toml, batch retry <question.toml> <PXD> --reason \"...\", batch research <question.toml> <PXD> --reason \"...\" [--profile key@n], or batch restrictions <question.toml> [--manifest file] [--write]");
                args.AllowOnly("machine", "profiles", "run-date", "reason", "profile", "manifest", "write");
                if ((args.Has("manifest") || args.Has("write")) && rest[0] != "restrictions") throw new UsageException("--manifest and --write are for batch restrictions only");
                if (args.Option("profile") is not null && rest[0] != "research") throw new UsageException("--profile is for batch research only");
                var q = QuestionLoader.Load(rest[1]);
                var b = q.Batch ?? throw new ConfigException(q.SourceFile, "a batch needs a [batch] table");
                // A probe worker (v0.4.26) runs the controller's copy of the question, whose [batch] is the controller's: its
                // state directory and run root are its machine file's.
                var given = args.Option("machine") is { } wm ? Machine.Load(wm) : null;
                var worker = given?.Worker;
                if (worker is not null) b = b with { StateDir = worker.StateDir, RunRoot = worker.RunRoot };
                Directory.CreateDirectory(b.StateDir);
                string stop = Path.Combine(b.StateDir, "STOP"), stateFile = Path.Combine(b.StateDir, "state.json");
                switch (rest[0])
                {
                    case "retry":
                        return Batch.BatchRunner.Requeue(b.StateDir, b.RunRoot, rest[2], args.Required("reason"), b.Queue);
                    case "research":
                    {
                        // The profile must be one the question names: a question is data, and the batch searches only its profiles.
                        string? key = args.Option("profile");
                        if (key is not null && !q.Profiles.Contains(key))
                            throw new UsageException($"--profile {key} is not among the question's profiles ({string.Join(", ", q.Profiles)}); add it to the question first");
                        return Batch.BatchRunner.Research(b.StateDir, b.RunRoot, rest[2], args.Required("reason"), key, q.Publish?.Manifest);
                    }
                    case "restrictions":
                    {
                        // v0.4.46: what the driver's start writes into the manifest's `restrictions`, shown
                        // without writing; --write writes it, refused on the live manifest while a driver runs (it is the
                        // manifest's writer, and syncs at its own start anyway). --manifest points at a copy, e.g. to check it
                        // with `datarepo manifest` first.
                        string mf = args.Option("manifest") ?? q.Publish?.Manifest
                                    ?? throw new ConfigException(q.SourceFile, "the question names no [publish] manifest; give --manifest");
                        if (!File.Exists(mf)) throw new UsageException($"no manifest at {mf}");
                        bool write = args.Has("write");
                        if (write && args.Option("manifest") is null && Batch.BatchRunner.LiveDriver(Path.Combine(b.StateDir, "driver.pid")) is { } live)
                            throw new UsageException($"driver {live} is running and writes this manifest; it syncs restrictions at its next start (or give --manifest for a copy)");
                        // v0.4.46 (G69): an entry's `run` is relative to the machine's work root; without --machine, the run
                        // root's parent, where the batch puts it.
                        string workRoot = args.Option("machine") is { } restrictionsMachine ? Machine.Load(restrictionsMachine).WorkRoot : Path.GetDirectoryName(Path.GetFullPath(b.RunRoot))!;
                        var noDesign = Batch.Restrictions.NoChannelDesignKeys(Batch.ManifestEntry.ReadLf(mf), workRoot, out var unread);
                        var r = Batch.Restrictions.Sync(mf, q.Restrictions, write, noDesign);
                        return new
                        {
                            manifest = mf,
                            written = write && r.Changed.Count > 0,
                            restricted = r.Restricted,
                            no_stated_isobaric_design = noDesign.Order(StringComparer.Ordinal).ToList(),
                            isobaric_provenance_unread = unread,
                            changed = r.Changed,
                            waiting_for_delivery = r.Waiting,
                        };
                    }
                    case "stop":
                        // Taken at the top of the loop: the search in flight, its cleanup and delivery finish first.
                        await File.WriteAllTextAsync(stop, $"Stop requested {DateTime.UtcNow:o}", ct).ConfigureAwait(false);
                        return new { stop_written = stop };
                    case "status":
                    {
                        // D73: with --machine, the check a start makes on its datarepo, so a new install is checked before
                        // the old driver is stopped.
                        string? datarepoVersion = null;
                        if (given?.DataRepo is { } dr)
                        {
                            var (started, rc, line) = await Batch.BatchRunner.DataRepoVersionAsync(dr, ct).ConfigureAwait(false);
                            if (started && Batch.BatchRunner.OtherRelease(dr, RunRecord.Versions()["pxreprise"], rc, line) is { } refusal)
                                throw new ConfigException(given.SourceFile, refusal);
                            datarepoVersion = line;
                        }
                        var ds = File.Exists(stateFile) ? System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(stateFile, ct).ConfigureAwait(false))!["datasets"]!.AsObject() : new System.Text.Json.Nodes.JsonObject();
                        string pidFile = Path.Combine(b.StateDir, "driver.pid");
                        return new
                        {
                            driver_pid = File.Exists(pidFile) ? (await File.ReadAllTextAsync(pidFile, ct).ConfigureAwait(false)).Trim() : null,
                            stop_pending = File.Exists(stop),
                            datarepo = datarepoVersion,
                            datasets = ds.Count,
                            by_status = ds.GroupBy(kv => kv.Value?["status"]?.GetValue<string>() ?? "?").OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
                            // The question's profiles that take no new searches (status "deprecated"), if any.
                            deprecated_profiles = PXReprise.Discovery.Router.DeprecatedProfiles(q, ProfileLoader.LoadDirectory(ProfilesDir(args))) is { Count: > 0 } dp ? dp : null,
                        };
                    }
                    default:
                    {
                        var machine = Machine.Load(args.Required("machine"));
                        var profiles = ProfileLoader.LoadDirectory(ProfilesDir(args));
                        // Never clear a running driver's STOP: a second start is refused by the driver's claim, but only after
                        // this line ran (G18; a start after a reboot may race a driver that survived it).
                        if (Batch.BatchRunner.LiveDriver(Path.Combine(b.StateDir, "driver.pid")) is null && File.Exists(stop)) File.Delete(stop);
                        using var client = new PrideArchiveClient();
                        var runner = new Batch.BatchRunner(q, profiles, machine, new Fetch.PrideFiles(client), new PrideProjectSearch(client),
                            args.Option("run-date") ?? DateTime.UtcNow.ToString("yyyy-MM-dd"))
                        { CommandLine = args.Raw };
                        // A probe worker (v0.4.26) takes its deposits from the controller's assignments, not from a queue file.
                        await runner.RunAsync(machine.Worker is null ? Batch.BatchRunner.LoadQueue(b.Queue) : Array.Empty<Batch.QueueEntry>(), ct).ConfigureAwait(false);
                        return new { finished = true };
                    }
                }
            }

            case "library":
            {
                // pxreprise library list --machine m.toml
                // pxreprise library repair <organism> --machine m.toml --reason "...": a new current version without the
                // spectra mzLib cannot read back (v0.4.20). Run it with no driver running.
                //   --databases a.xml,b.xml (v0.4.32): also clean it (Search/LibraryCleaning.cs) against the organism's
                //   target databases, with the modifications of every MetaMorpheus the machine names and the shipped lists.
                //   --remove-deposits PXD1,PXD2 (2026-11-24): also leave out what those deposits' searches put in the chain
                //   (SpectralLibrary.WithoutDeposits), for a deposit with a stated quality problem.
                // 2026-11-11, with the machine's spectral_library_remote (the chains shared through the cloud):
                //   pxreprise library pull <chain> --machine m.toml   fetch the cloud's newer versions and make the newest current
                //   pxreprise library seed <chain> --machine m.toml   start the cloud's copy of a chain from this machine
                var rest = args.Words.Skip(1).ToList();
                if (!(rest is ["list"] || rest is ["repair", _] || rest is ["pull", _] || rest is ["seed", _]))
                    throw new UsageException("usage: pxreprise library list --machine m.toml, library repair <organism> --machine m.toml --reason \"...\" [--databases a.xml,b.xml] [--remove-deposits PXD1,PXD2], library pull <chain> --machine m.toml, or library seed <chain> --machine m.toml");
                args.AllowOnly("machine", "reason", "databases", "profiles", "remove-deposits");
                var libMachine = Machine.Load(args.Required("machine"));
                string root = libMachine.SpectralLibraryRoot;
                if (rest[0] == "list") return Search.SpectralLibrary.List(root);
                using var libCloud = Search.LibraryCloud.Open(libMachine.SpectralLibraryRemote);
                if (rest[0] is "pull" or "seed")
                {
                    if (libCloud is null) throw new UsageException($"{libMachine.SourceFile} names no spectral_library_remote");
                    return rest[0] == "pull" ? Search.SpectralLibrary.Pull(root, rest[1], libCloud) : Search.SpectralLibrary.Seed(root, rest[1], libCloud);
                }
                Search.LibraryCleaning? cleaning = null;
                if (args.Option("databases") is { } dbList)
                {
                    var modFiles = libMachine.MetaMorpheus.Values
                        .Select(c => Path.Combine(Path.GetDirectoryName(c)!, "Mods")).Where(Directory.Exists)
                        .SelectMany(d => Directory.EnumerateFiles(d, "*.txt"))
                        .Concat(Directory.EnumerateFiles(Path.Combine(ProfilesDir(args), "lists"), "PXReprise_custom_modifications_*.txt"));
                    cleaning = Search.LibraryCleaning.For(modFiles,
                        dbList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(x => Path.IsPathRooted(x) ? x : Path.Combine(libMachine.DatabaseDir, x)));
                }
                var removeDeposits = args.Option("remove-deposits")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return Search.SpectralLibrary.Repair(root, rest[1], args.Required("reason"), cleaning, libCloud, removeDeposits);
            }

            case "worker":
            {
                // v0.4.26, a probe worker's share:
                //   pxreprise worker sums <dir>             write <dir>\SHA256SUMS (a software folder or database set)
                //   pxreprise worker check <dir> [--sums F] check <dir>'s files against SHA256SUMS (exit 1 on a mismatch)
                //   pxreprise worker release <share> --reason "..." [--metamorpheus NAME] [--dotnet NAME] [--database-set NAME]
                //     publish THIS build (the one running) to the share and raise current.toml's generation
                var rest = args.Words.Skip(1).ToList();
                if (!(rest is ["sums", _] || rest is ["check", _] || rest is ["release", _]))
                    throw new UsageException("usage: pxreprise worker sums <dir>, worker check <dir> [--sums FILE], or worker release <share> --reason \"...\" [--metamorpheus NAME] [--dotnet NAME] [--database-set NAME]");
                switch (rest[0])
                {
                    case "sums":
                        args.AllowOnly();
                        return new { dir = Path.GetFullPath(rest[1]), files = Batch.Sha256Sums.Write(rest[1]), sums = Path.GetFullPath(Path.Combine(rest[1], Batch.Sha256Sums.FileName)) };
                    case "check":
                    {
                        args.AllowOnly("sums");
                        var (n, problems) = Batch.Sha256Sums.Check(rest[1], args.Option("sums"));
                        if (problems.Count > 0)
                            throw new InvalidDataException($"{problems.Count} of {n} file(s) do not match: {string.Join("; ", problems.Take(20))}{(problems.Count > 20 ? "; ..." : "")}");
                        return new { dir = Path.GetFullPath(rest[1]), files_checked = n, ok = true };
                    }
                    default:
                    {
                        args.AllowOnly("reason", "metamorpheus", "dotnet", "database-set");
                        var c = new Batch.WorkerShare(rest[1]).Release(AppContext.BaseDirectory, RunRecord.Versions()["pxreprise"],
                            args.Option("metamorpheus"), args.Option("dotnet"), args.Option("database-set"), args.Required("reason"));
                        return new { generation = c.Generation, pxreprise = c.Pxreprise, software = c.Software, metamorpheus = c.MetaMorpheus, dotnet = c.Dotnet, database_set = c.DatabaseSet };
                    }
                }
            }

            default:
                throw new UsageException($"unknown verb '{args.Words[0]}'; verbs: {string.Join(", ", Verbs)}");
        }
    }

    private static string ProfilesDir(Arguments args) => args.Option("profiles") ?? DefaultProfilesDir;

    /// <summary>A shipped profile by key (<c>--profile label-free-dda@1</c>), or one file (<c>--profile-file</c>).</summary>
    private static Profile ResolveProfile(Arguments args)
    {
        string? key = args.Option("profile"), file = args.Option("profile-file");
        if ((key is null) == (file is null)) throw new UsageException("give exactly one of --profile or --profile-file");
        if (file is not null) return ProfileLoader.Load(file);
        var all = ProfileLoader.LoadDirectory(ProfilesDir(args));
        return all.TryGetValue(key!, out var p) ? p : throw new UsageException($"no profile {key} (known: {string.Join(", ", all.Keys)})");
    }

    private static IReadOnlyList<string> List(string? csv) =>
        csv is null ? Array.Empty<string>() : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// <c>search --probe-offsets</c>: a JSON object of each probe run's precursor offset in ppm (<c>{"run": 9.4}</c>), the
    /// shape of a batch state entry's <c>probe_precursor_offsets_ppm</c>.
    /// </summary>
    internal static IReadOnlyDictionary<string, double> ReadProbeOffsets(string path)
    {
        if (!File.Exists(path)) throw new UsageException($"--probe-offsets {path} does not exist");
        System.Text.Json.Nodes.JsonNode? node;
        try { node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)); }
        catch (System.Text.Json.JsonException e) { throw new UsageException($"--probe-offsets {path} is not JSON: {e.Message}", e); }
        if (node is not System.Text.Json.Nodes.JsonObject o || o.Count == 0)
            throw new UsageException($"--probe-offsets {path} must be a JSON object of run name to offset in ppm, such as {{\"run\": 9.4}}");
        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (run, value) in o)
        {
            if (value is not System.Text.Json.Nodes.JsonValue v || !v.TryGetValue(out double ppm))
                throw new UsageException($"--probe-offsets {path}: run '{run}' has no number");
            offsets[run] = ppm;
        }
        return offsets;
    }

    /// <summary>
    /// <c>fetch --include / --exclude</c>: the raw files to download, as a question's <c>include_files</c> and
    /// <c>exclude_files</c> choose them (case-insensitive regular expressions on the file name; a file is kept when it
    /// matches the include, if given, and not the exclude, if given). Null when neither is given. Files left out are never
    /// downloaded, so a search by hand of part of a deposit fetches only that part.
    /// </summary>
    internal static Fetch.FileSelection? FileSelection(string? include, string? exclude)
    {
        if (include is null && exclude is null) return null;
        static System.Text.RegularExpressions.Regex? Pattern(string option, string? p)
        {
            if (p is null) return null;
            if (p.Length == 0) throw new UsageException($"--{option} is empty");
            try { return new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant); }
            catch (ArgumentException e) { throw new UsageException($"--{option} is not a valid regular expression: '{p}' ({e.Message})", e); }
        }
        var (inc, exc) = (Pattern("include", include), Pattern("exclude", exclude));
        string reason = string.Join(", ", new[] { inc is null ? null : $"include_files /{inc}/", exc is null ? null : $"exclude_files /{exc}/" }
            .Where(s => s is not null)) + " (fetch by hand)";
        return new Fetch.FileSelection(n => (inc is null || inc.IsMatch(n)) && (exc is null || !exc.IsMatch(n)), reason);
    }

    /// <summary>Exactly <paramref name="n"/> positional arguments after the verb.</summary>
    private static IReadOnlyList<string> Positional(Arguments args, int n)
    {
        var rest = args.Words.Skip(1).ToList();
        if (rest.Count != n)
            throw new UsageException(n == 0
                ? $"'{args.Words[0]}' takes no positional arguments"
                : $"'{args.Words[0]}' takes {n} positional argument(s), got {rest.Count}");
        return rest;
    }
}
