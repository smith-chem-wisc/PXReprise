// pxr-evidence compare: our independent SDRF draft against every deposited or community SDRF of the deposit, the differences
// resolved where the evidence allows and the rest reported for a person (D65 (7), (8); the user, 2026-11-07: "we create an
// independant sdrf and compare it to the community sdrf and then make an effort to resolve differences. unresolved differences
// get reported so that a user can make a call. would be handy if the pride depositor's email addresses were handy").
//
//   pxr-evidence compare --out DIR [--list FILE] [--runs DIR[;DIR]] [--model ID | --no-model] [--max-usd N] [--refresh] PXD...
//       Per deposit, from DIR\<acc>\draft.independent.sdrf.tsv and DIR\<acc>\deposit\*sdrf*:
//         compare.independent.tsv   every difference (grouped over files): kind, both values, how it was resolved, evidence;
//         compare.independent.md    the report for a person, with the depositors' contacts from PRIDE's record;
//         adjudication.independent.json   the model's answers (paid once, reused unless --refresh or the questions change).
//       --runs: the engine's run roots, whose QC reports hold each raw file's instrument model (the raw files decide it).
//       Run-wide: compare.independent.summary.tsv, compare.independent.unresolved.tsv (no contacts in either).
// In order: what the raw files measure (the instrument) settles that column; wording, a more specific value and numbering are
// not disagreements; a conflict is put to the model with the two values unlabelled, and an answer counts only with a quote
// found in the text it was given; what stays is for a person.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Newtonsoft.Json.Linq;
using Readers;

internal enum DiffKind { Agree, Wording, MoreSpecificOurs, MoreSpecificTheirs, Conflict, OursMissing, TheirsMissing, Numbering, Grouping, Measured }

internal static class SdrfComparison
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One difference put to the model: the column, both values (ours first here; shown unlabelled), the files.</summary>
    internal sealed record Question(int Id, string Column, string Ours, string Theirs, IReadOnlyList<string> Files);

    /// <summary>The model's answer, accepted (<see cref="Resolved"/>) only with a quote found in the text.</summary>
    internal sealed record Verdict(int Id, bool Resolved, string Side, string Value, string Quote, string Note);

    /// <summary>A difference over one or more files of one SDRF: what each side says, what the raw files say, how it ended.</summary>
    internal sealed record Difference(string Sdrf, string Column, DiffKind Kind, string Ours, string Theirs, string Measured,
        IReadOnlyList<string> Files, bool Resolved, string Resolution, string Value, string Evidence);

    /// <summary>Columns that number or name things: compared as groupings of files, never as values.</summary>
    private static readonly HashSet<string> GroupingColumns = new(StringComparer.Ordinal)
        { "source name", "characteristics[biological replicate]", "comment[technical replicate]", "comment[fraction identifier]" };

    private static readonly HashSet<string> SkippedColumns = new(StringComparer.Ordinal)
        { "comment[data file]", "comment[file uri]", "comment[proteomexchange accession number]", "comment[associated file uri]", "assay name" };

    private const string Instrument = "comment[instrument]";

    /// <summary>
    /// A column of facts about the samples or the runs. Not compared: the data file (the key), and an SDRF's bookkeeping: the
    /// drafter's own <c>comment[... source]</c>, <c>... source method]</c> and <c>... source reference]</c> columns, and a
    /// community SDRF's <c>comment[sdrf template]</c>, <c>comment[sdrf annotation tool]</c> and the like.
    /// </summary>
    internal static bool Compared(string column) =>
        !SkippedColumns.Contains(column)
        && (column == "source name" || Regex.IsMatch(column, @"^(characteristics|factor value|comment)\["))
        && !Regex.IsMatch(column, @"^comment\[(.* source( method| reference)?|characteristics source|sdrf .*)\]$");

    // ---- sorting a difference -------------------------------------------------------------------------------------------

    /// <summary>Case- and space-blind; an ontology-tagged value (<c>NT=...;AC=...</c>) by its name; not available is empty.</summary>
    internal static string Norm(string v)
    {
        string s = (v ?? "").Trim();
        int nt = s.IndexOf("NT=", StringComparison.OrdinalIgnoreCase);
        if (nt >= 0)
        {
            int end = s.IndexOf(';', nt);
            s = end < 0 ? s[(nt + 3)..] : s[(nt + 3)..end];
        }
        s = string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s is "not available" or "not applicable" or "na" or "n/a" ? "" : s;
    }

    /// <summary>Wording-blind: possessives, punctuation, a final plural s, wild type and the label-free spelling.</summary>
    internal static string Loose(string v)
    {
        string s = Norm(v).Replace('’', '\'').Replace("'s ", " ").Replace("'s", "");
        s = Regex.Replace(s, "[^a-z0-9]+", " ").Trim();
        s = Regex.Replace(s, @"\bwild ?type\b|\bwt\b", "wild type");
        if (s == "label free sample") s = "label free";
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss", StringComparison.Ordinal) ? t[..^1] : t));
    }

    internal static DiffKind Classify(string ours, string theirs)
    {
        string a = Norm(ours), b = Norm(theirs);
        if (a.Length == 0 && b.Length == 0) return DiffKind.Agree;
        if (a.Length == 0) return DiffKind.OursMissing;
        if (b.Length == 0) return DiffKind.TheirsMissing;
        if (a == b) return DiffKind.Agree;
        string la = Loose(a), lb = Loose(b);
        if (la == lb) return DiffKind.Wording;
        var ta = la.Split(' ').ToHashSet(StringComparer.Ordinal);
        var tb = lb.Split(' ').ToHashSet(StringComparer.Ordinal);
        if (ta.IsProperSubsetOf(tb)) return DiffKind.MoreSpecificTheirs;
        if (tb.IsProperSubsetOf(ta)) return DiffKind.MoreSpecificOurs;
        return DiffKind.Conflict;
    }

    private static readonly HashSet<string> InstrumentNoise = new(StringComparer.Ordinal)
        { "orbitrap", "thermo", "scientific", "fisher", "thermofisher", "mass", "spectrometer", "instrument" };

    /// <summary>
    /// An SDRF instrument against the model a raw file's header names: every distinguishing word must agree, so "Q Exactive HF"
    /// is not "Q Exactive HF-X Orbitrap" and "LTQ Orbitrap" is not "LTQ Orbitrap XL". The word Orbitrap and the maker's name
    /// do not distinguish (the header says "Q Exactive Orbitrap" for PSI-MS's "Q Exactive").
    /// </summary>
    internal static bool SameInstrument(string sdrfValue, string rawModel)
    {
        HashSet<string> Words(string v) => Loose(v).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !InstrumentNoise.Contains(t)).ToHashSet(StringComparer.Ordinal);
        var a = Words(sdrfValue);
        return a.Count > 0 && a.SetEquals(Words(rawModel));
    }

    /// <summary>
    /// Two numberings of the same files: the same groups under other numbers, or different groups. A side that gives every file
    /// one number (a default "1") tells nothing about the groups: the other side's numbering is then an omission of this one.
    /// </summary>
    internal static DiffKind Grouping(IReadOnlyList<string> ours, IReadOnlyList<string> theirs)
    {
        var o = ours.Select(Norm).ToList();
        var t = theirs.Select(Norm).ToList();
        if (Grading.SamePartition(o, t)) return DiffKind.Numbering;
        if (o.Distinct().Count() == 1) return DiffKind.OursMissing;
        if (t.Distinct().Count() == 1) return DiffKind.TheirsMissing;
        return DiffKind.Grouping;
    }

    // ---- the model's answers ----------------------------------------------------------------------------------------------

    private static string QuoteNorm(string s) => Regex.Replace(s.Normalize(NormalizationForm.FormKC).Replace('’', '\'').Replace('‘', '\'')
        .Replace('“', '"').Replace('”', '"').Replace('–', '-').Replace('—', '-').Replace("­", ""), @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>
    /// The model's JSON answer, one verdict per question. "value 1" and "value 2" are mapped back through
    /// <paramref name="swapped"/> (false: value 1 was ours). A choice counts only with a quote of at least 8 characters found in
    /// <paramref name="text"/>; "both", "neither" and "cannot tell" stay for a person, with what the model said.
    /// </summary>
    internal static IReadOnlyList<Verdict> Verdicts(string json, IReadOnlyList<Question> questions, string text, IReadOnlyList<bool> swapped)
    {
        var byId = new Dictionary<int, JsonObject>();
        try
        {
            if (JsonNode.Parse(json)?["answers"] is JsonArray answers)
                foreach (var a in answers.OfType<JsonObject>())
                    if (a["id"]?.GetValue<int>() is int id) byId.TryAdd(id, a);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }   // a malformed answer: every question stays open
        string corpus = QuoteNorm(text);
        var verdicts = new List<Verdict>();
        foreach (var q in questions)
        {
            if (!byId.TryGetValue(q.Id, out var a)) { verdicts.Add(new Verdict(q.Id, false, "", "", "", "no answer")); continue; }
            string choice = (string?)a["choice"] ?? "", quote = (string?)a["quote"] ?? "", why = (string?)a["why"] ?? "", other = (string?)a["value"] ?? "";
            bool quoted = QuoteNorm(quote).Length >= 8 && corpus.Contains(QuoteNorm(quote), StringComparison.Ordinal);
            if (choice is "value 1" or "value 2")
            {
                bool first = choice == "value 1";
                bool ours = first != swapped[q.Id];
                string side = ours ? "ours" : "theirs";
                if (quoted) verdicts.Add(new Verdict(q.Id, true, side, ours ? q.Ours : q.Theirs, quote, why));
                else verdicts.Add(new Verdict(q.Id, false, side, ours ? q.Ours : q.Theirs, quote, $"the model chose {side}, but its quote is not in the text: '{quote}'"));
            }
            else
                verdicts.Add(new Verdict(q.Id, false, "", other, quoted ? quote : "",
                    $"the model: {choice}{(other.Length > 0 ? $" ('{other}')" : "")}{(why.Length > 0 ? $": {why}" : "")}{(quote.Length > 0 && !quoted ? " (its quote is not in the text)" : "")}"));
        }
        return verdicts;
    }

    /// <summary>The depositors PRIDE's record names, submitters then lab PIs, for a person who needs to write to them.</summary>
    internal static List<string> Contacts(JObject project)
    {
        var lines = new List<string>();
        foreach (var (key, role) in new[] { ("Submitters", "submitter"), ("LabPIs", "lab PI") })
        {
            if (project.GetValue(key, StringComparison.OrdinalIgnoreCase) is not JArray people) continue;
            foreach (var p in people.OfType<JObject>())
            {
                string Get(string k) => (string?)p.GetValue(k, StringComparison.OrdinalIgnoreCase) ?? "";
                string name = Get("Name").Length > 0 ? Get("Name") : $"{Get("FirstName")} {Get("LastName")}".Trim();
                string email = Get("Email"), aff = Get("Affiliation"), orcid = Get("Orcid");
                lines.Add($"{role}: {name}{(email.Length > 0 ? $" <{email}>" : "")}{(aff.Length > 0 ? $", {aff}" : "")}{(orcid.Length > 0 ? $", ORCID {orcid}" : "")}");
            }
        }
        return lines;
    }

    // ---- the comparison --------------------------------------------------------------------------------------------------

    /// <summary>Each raw file's instrument model from the engine's QC reports (<c>&lt;run&gt;\&lt;acc&gt;*\02b_qc*\qc_report.json</c>).</summary>
    internal static Dictionary<string, string> MeasuredInstruments(IEnumerable<string> runRoots, string acc)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string root in runRoots.Where(Directory.Exists))
            foreach (string run in Directory.GetDirectories(root, acc + "*").OrderBy(d => Path.GetFileName(d) == acc ? 0 : 1).ThenBy(d => d, StringComparer.Ordinal))
                foreach (string qc in new[] { "02b_qc", "02b_qc_probe" })
                {
                    string f = Path.Combine(run, qc, "qc_report.json");
                    if (!File.Exists(f)) continue;
                    if (JsonNode.Parse(File.ReadAllText(f)) is not JsonObject o) continue;
                    foreach (var (file, v) in o)
                        if (v is JsonObject r && (string?)r["instrument_model"] is { Length: > 0 } m) map.TryAdd(Norm(file), m);
                }
        return map;
    }

    private static string Get(Dictionary<string, string> row, string column) => row.GetValueOrDefault(column, "");

    /// <summary>
    /// Whether our cell came from curator evidence (pxr-evidence read --curator): the drafter's <c>comment[... source method]</c>
    /// for the column says <c>curator</c>. Such a cell is the user's decision already (D65), so a difference there is not
    /// put to the user again.
    /// </summary>
    internal static bool FromCurator(Dictionary<string, string> ours, string column)
    {
        var m = Regex.Match(column, @"^(characteristics|comment|factor value)\[(.+)\]$");
        string name = column == "source name" ? "source name"
            : !m.Success ? ""
            : m.Groups[1].Value == "factor value" ? "factor value " + m.Groups[2].Value
            : m.Groups[2].Value;
        return name.Length > 0 && Get(ours, $"comment[{name} source method]").Equals("curator", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every difference between our draft and one of their SDRFs, before the model is asked anything.</summary>
    internal static List<Difference> Compare(Grading.Table ours, Grading.Table theirs, string theirsName, IReadOnlyDictionary<string, string> measured,
        out int sharedRows, out int agreeCells)
    {
        var oursByFile = ours.Rows.GroupBy(r => Norm(Get(r, "comment[data file]"))).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.First());
        var pairs = theirs.Rows.Select(t => (T: t, File: Norm(Get(t, "comment[data file]")))).Where(p => oursByFile.ContainsKey(p.File))
            .Select(p => (O: oursByFile[p.File], p.T, p.File, Label: Get(p.T, "comment[label]"))).ToList();
        sharedRows = pairs.Count;
        agreeCells = 0;
        bool perChannel = pairs.GroupBy(p => p.File).Any(g => g.Count() > 1);
        string Place((Dictionary<string, string> O, Dictionary<string, string> T, string File, string Label) p) =>
            perChannel ? $"{Get(p.T, "comment[data file]")} {p.Label}" : Get(p.T, "comment[data file]");

        // The raw files' instrument: one model for every measured file means one for the deposit.
        var models = pairs.Select(p => p.File).Distinct().Select(f => measured.GetValueOrDefault(f)).Where(m => m is not null).Distinct().ToList();
        string? depositModel = models.Count == 1 ? models[0] : null;

        var diffs = new List<Difference>();
        var cells = new List<(string Column, DiffKind Kind, string Ours, string Theirs, string Measured, string Where, bool Curator)>();
        var columns = theirs.Columns.Concat(ours.Columns).Distinct(StringComparer.Ordinal).Where(Compared).ToList();
        foreach (string col in columns)
        {
            if (GroupingColumns.Contains(col))
            {
                var o = pairs.Select(p => Get(p.O, col)).ToList();
                var t = pairs.Select(p => Get(p.T, col)).ToList();
                if (o.All(v => Norm(v).Length == 0) || t.All(v => Norm(v).Length == 0)) continue;   // one side numbers nothing: an omission, not a grouping
                if (o.Zip(t).All(x => Norm(x.First) == Norm(x.Second))) { agreeCells += pairs.Count; continue; }
                var kind = Grouping(o, t);
                double ari = Grading.RandValue(o.Select(Norm).ToList(), t.Select(Norm).ToList());
                string what = $"{o.Select(Norm).Distinct().Count()} groups in ours, {t.Select(Norm).Distinct().Count()} in theirs, adjusted Rand {ari.ToString("0.00", Inv)}";
                // Ours grouped by the curator on every file: the user's decision. On some files only: still open, naming the rest.
                var notCurated = pairs.Where(p => !FromCurator(p.O, col)).Select(Place).ToList();
                string how = kind switch
                {
                    DiffKind.Numbering => "the same groups of files under other numbers",
                    DiffKind.OursMissing => "only theirs numbers them (ours gives every file one number)",
                    DiffKind.TheirsMissing => "only ours numbers them (theirs gives every file one number)",
                    DiffKind.Grouping when notCurated.Count == 0 => "the curator decided: ours",
                    DiffKind.Grouping when notCurated.Count < pairs.Count =>
                        $"unresolved: the files are grouped differently; the curator decided all but {notCurated.Count} file(s): {Files(notCurated)}",
                    _ => "unresolved: the files are grouped differently",
                };
                bool decided = kind != DiffKind.Grouping || notCurated.Count == 0;
                diffs.Add(new Difference(theirsName, col, kind, what, "", "", pairs.Select(Place).ToList(), decided, how, "", ""));
                continue;
            }
            foreach (var p in pairs)
            {
                string a = Get(p.O, col), b = Get(p.T, col);
                if (col == Instrument && (measured.GetValueOrDefault(p.File) ?? depositModel) is { } raw && (Norm(a).Length > 0 || Norm(b).Length > 0))
                {
                    // An empty side is an omission, not a wrong value: only a stated instrument can be wrong.
                    bool oursWrong = Norm(a).Length > 0 && !SameInstrument(a, raw), theirsWrong = Norm(b).Length > 0 && !SameInstrument(b, raw);
                    if (oursWrong || theirsWrong) { cells.Add((col, DiffKind.Measured, a, b, raw, Place(p), false)); continue; }
                    if (Norm(a).Length > 0 && Norm(b).Length > 0) { agreeCells++; continue; }
                }
                var k = Classify(a, b);
                if (k == DiffKind.Agree) { if (Norm(a).Length > 0) agreeCells++; continue; }
                cells.Add((col, k, a, b, "", Place(p), FromCurator(p.O, col)));
            }
        }
        foreach (var g in cells.GroupBy(c => (c.Column, c.Kind, O: Norm(c.Ours), T: Norm(c.Theirs), M: c.Measured, c.Curator)))
        {
            var first = g.First();
            var files = g.Select(c => c.Where).ToList();
            (bool resolved, string how, string value) = g.Key.Kind switch
            {
                DiffKind.Conflict when g.Key.Curator => (true, "the curator decided: ours", first.Ours),
                DiffKind.Measured => (true, (Norm(first.Ours).Length > 0 && !SameInstrument(first.Ours, first.Measured), Norm(first.Theirs).Length > 0 && !SameInstrument(first.Theirs, first.Measured)) switch
                {
                    (false, true) => "the raw files decide: theirs is wrong",
                    (true, false) => "the raw files decide: ours is wrong",
                    _ => "the raw files decide: both are wrong",
                }, first.Measured),
                DiffKind.Wording => (true, "wording only", first.Theirs.Contains("AC=", StringComparison.OrdinalIgnoreCase) ? first.Theirs : first.Ours),
                DiffKind.MoreSpecificTheirs => (true, "theirs is more specific", first.Theirs),
                DiffKind.MoreSpecificOurs => (true, "ours is more specific", first.Ours),
                DiffKind.OursMissing => (true, "only theirs states it (not checked)", first.Theirs),
                DiffKind.TheirsMissing => (true, "only ours states it", first.Ours),
                _ => (false, "unresolved", ""),
            };
            diffs.Add(new Difference(theirsName, g.Key.Column, g.Key.Kind, first.Ours, first.Theirs, first.Measured, files, resolved, how, value, ""));
        }
        return diffs;
    }

    // ---- asking the model ------------------------------------------------------------------------------------------------

    internal const string AdjudicationSystem = """
        You check the sample metadata of one proteomics deposit. Two descriptions of the same raw files disagree. For each numbered
        difference, decide from THE TEXT GIVEN ONLY which value the text supports for those files.

        Answer with one of:
        - "value 1" or "value 2": the text supports that value for those files;
        - "both": the text supports both (for example, for different files);
        - "neither": the text says something else; put what it says in "value";
        - "cannot tell": the text does not settle it.

        In "quote", copy the exact words from the text that decide it, character for character, at most 300 characters. A choice
        without such a quote is not accepted. Never decide from general knowledge, from what is usual, or from which value looks more
        complete. If the text does not say, answer "cannot tell". In "why", say in one sentence how the quote decides it.
        """;

    private static readonly Dictionary<string, JsonElement> AdjudicationSchema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "answers" }),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            answers = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "id", "choice", "value", "quote", "why" },
                    properties = new
                    {
                        id = new { type = "integer" },
                        choice = new { type = "string", @enum = new[] { "value 1", "value 2", "both", "neither", "cannot tell" } },
                        value = new { type = "string" }, quote = new { type = "string" }, why = new { type = "string" },
                    },
                },
            },
        }),
    };

    /// <summary>The question text: each difference with its two values unlabelled (order by <paramref name="swapped"/>), then the text.</summary>
    internal static string AdjudicationPrompt(string acc, IReadOnlyList<Question> qs, IReadOnlyList<bool> swapped, string text)
    {
        var sb = new StringBuilder($"# Deposit {acc}\n\n## Differences\n");
        foreach (var q in qs)
        {
            var (v1, v2) = swapped[q.Id] ? (q.Theirs, q.Ours) : (q.Ours, q.Theirs);
            string files = q.Files.Count <= 6 ? string.Join(", ", q.Files) : string.Join(", ", q.Files.Take(6)) + $" and {q.Files.Count - 6} more";
            sb.Append($"{q.Id}. {q.Column}, {q.Files.Count} file(s): {files}\n   value 1: {v1}\n   value 2: {v2}\n");
        }
        sb.Append("\n## The text\n").Append(text);
        return sb.ToString();
    }

    private static async Task<(string Json, long In, long Out, long Cache)> AskAsync(AnthropicClient client, string model, string prompt, Action<string> log, string acc)
    {
        int[] waitSeconds = { 30, 90, 180, 300 };
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var response = await client.Beta.Messages.CreateStreaming(new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = 16_000,
                    System = new List<BetaTextBlockParam> { new() { Text = AdjudicationSystem, CacheControl = new BetaCacheControlEphemeral() } },
                    Messages = [new() { Role = Role.User, Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = prompt } } }],
                    Thinking = new BetaThinkingConfigAdaptive(),
                    OutputConfig = new BetaOutputConfig { Effort = Effort.Medium, Format = new BetaJsonOutputFormat { Schema = AdjudicationSchema } },
                    Betas = ["server-side-fallback-2026-07-01"],
                    Fallbacks = new Default(),
                }).Aggregate().ConfigureAwait(false);
                var text = new StringBuilder();
                foreach (var block in response.Content)
                    if (block.TryPickText(out var t)) text.Append(t.Text);
                return (text.ToString(), response.Usage.InputTokens, response.Usage.OutputTokens, response.Usage.CacheReadInputTokens ?? 0);
            }
            catch (Exception e) when (attempt <= waitSeconds.Length && RichReader.Transient(e))
            {
                log($"{acc}: {e.GetType().Name}; retry {attempt} of {waitSeconds.Length} in {waitSeconds[attempt - 1]} s");
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds[attempt - 1])).ConfigureAwait(false);
            }
        }
    }

    // ---- the run ----------------------------------------------------------------------------------------------------------

    internal static readonly string[] SummaryColumns =
    {
        "accession", "their_sdrfs", "rows_shared", "agree_cells", "measured_fixed", "wording", "more_specific", "theirs_only", "ours_only",
        "numbering", "grouping_differs", "conflicts", "resolved_by_text", "unresolved", "est_cost_usd", "notes",
    };

    internal static async Task<int> RunAsync(string outRoot, IReadOnlyList<string> accs, IReadOnlyList<string> runRoots, string? model,
        AnthropicClient? claude, double maxUsd, bool refresh, (double In, double Out, double Cache) price)
    {
        var summary = new List<string> { string.Join('\t', SummaryColumns) };
        var unresolvedAll = new List<string> { "accession\tsdrf\tcolumn\tkind\tfiles\tfile_list\tours\ttheirs\tours_evidence\tnote" };
        double spent = 0;
        foreach (string acc in accs)
        {
            string dir = Path.Combine(outRoot, acc);
            var notes = new List<string>();
            string oursPath = Path.Combine(dir, "draft.independent.sdrf.tsv");
            var theirsFiles = Directory.Exists(Path.Combine(dir, "deposit"))
                ? Directory.GetFiles(Path.Combine(dir, "deposit"), "*sdrf*").Where(f => !f.EndsWith(".partial", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList()
                : new List<string>();
            if (!File.Exists(oursPath) || theirsFiles.Count == 0)
            {
                string why = !File.Exists(oursPath) ? "no draft.independent.sdrf.tsv (run read --independent first)" : "no deposited or community SDRF to compare with";
                Console.WriteLine($"{acc}: {why}");
                summary.Add(string.Join('\t', new[] { acc }.Concat(Enumerable.Repeat("", SummaryColumns.Length - 2)).Append(why)));
                continue;
            }
            var ours = Grading.Read(oursPath);
            var measured = MeasuredInstruments(runRoots, acc);
            if (measured.Count == 0) notes.Add("no QC report found: the instrument is compared as text");
            var diffs = new List<Difference>();
            int shared = 0, agree = 0;
            foreach (string tf in theirsFiles)
            {
                diffs.AddRange(Compare(ours, Grading.Read(tf), Path.GetFileName(tf), measured, out int s, out int a));
                shared += s; agree += a;
            }

            // Our own evidence for each conflicting value: the independent reading's claims, with where they were found.
            var claims = new[] { "evidence.model.independent.tsv", "evidence.rules.independent.tsv" }.Select(n => Path.Combine(dir, n)).Where(File.Exists)
                .SelectMany(SdrfEvidenceFile.Read).ToList();
            string OurEvidence(Difference d) => string.Join(" | ", claims.Where(c => c.Column.Equals(d.Column, StringComparison.OrdinalIgnoreCase) && Norm(c.Value) == Norm(d.Ours))
                .Select(c => $"{c.Source}: {c.Locator}").Distinct().Take(3));
            diffs = diffs.Select(d => d.Kind == DiffKind.Conflict ? d with { Evidence = OurEvidence(d) } : d).ToList();

            // The model, on the real conflicts only, with the text the independent reading was given (no SDRF in it).
            // One question per distinct conflict: a deposit with two SDRFs saying the same thing is asked once (PXD008934).
            var conflicts = diffs.Where(d => d.Kind == DiffKind.Conflict).ToList();
            int byCurator = diffs.Count(d => d.Resolution.StartsWith("the curator decided", StringComparison.Ordinal));
            if (byCurator > 0) notes.Add($"{byCurator} difference(s) decided by the curator (read --curator)");
            var asked = conflicts.Where(d => !d.Resolved).GroupBy(d => (d.Column, O: Norm(d.Ours), T: Norm(d.Theirs), F: string.Join("\n", d.Files))).ToList();
            var questions = asked.Select((g, i) => new Question(i, g.Key.Column, g.First().Ours, g.First().Theirs, g.First().Files)).ToList();
            var swapped = questions.Select(q => q.Id % 2 == 1).ToList();
            double cost = 0;
            int resolvedByText = 0;
            if (questions.Count > 0)
            {
                string promptFile = Path.Combine(dir, "prompt.independent.txt"), transcripts = Path.Combine(dir, "transcripts.independent.tsv");
                string text = (File.Exists(promptFile) ? File.ReadAllText(promptFile) : "")
                    + (File.Exists(transcripts) ? "\n## Transcripts of the figures and pages\n" + File.ReadAllText(transcripts) : "");
                string prompt = AdjudicationPrompt(acc, questions, swapped, text);
                string cacheFile = Path.Combine(dir, "adjudication.independent.json");
                string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))[..16];
                string? json = null;
                if (!refresh && File.Exists(cacheFile) && JsonNode.Parse(File.ReadAllText(cacheFile)) is JsonObject cached && (string?)cached["key"] == key)
                    json = (string?)cached["answer"];
                else if (claude is not null && model is not null && (maxUsd <= 0 || spent < maxUsd))
                {
                    Console.WriteLine($"{acc}: asking about {questions.Count} conflict(s)");
                    var (answer, tin, tout, tcache) = await AskAsync(claude, model, prompt, Console.WriteLine, acc).ConfigureAwait(false);
                    cost = tin / 1e6 * price.In + tout / 1e6 * price.Out + tcache / 1e6 * price.Cache;
                    spent += cost;
                    json = answer;
                    File.WriteAllText(cacheFile, new JsonObject
                    {
                        ["utc"] = DateTime.UtcNow.ToString("o", Inv), ["model"] = model, ["key"] = key, ["questions"] = questions.Count,
                        ["swapped"] = new JsonArray(swapped.Select(s => (JsonNode)s).ToArray()), ["input_tokens"] = tin, ["output_tokens"] = tout,
                        ["cache_read_tokens"] = tcache, ["est_cost_usd"] = Math.Round(cost, 4), ["answer"] = answer,
                    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                else notes.Add(claude is null ? "conflicts not put to the model (--no-model)" : $"conflicts not put to the model: the spend reached --max-usd {maxUsd}");
                if (json is not null)
                {
                    var verdicts = Verdicts(json, questions, text, swapped);
                    foreach (var v in verdicts)
                        foreach (var d in asked[v.Id])
                        {
                            int at = diffs.IndexOf(d);
                            diffs[at] = v.Resolved
                                ? d with { Resolved = true, Resolution = $"the text decides: {v.Side}", Value = v.Value, Evidence = (d.Evidence.Length > 0 ? d.Evidence + " | " : "") + $"quote: \"{v.Quote}\"" + (v.Note.Length > 0 ? $" ({v.Note})" : "") }
                                : d with { Resolution = $"unresolved: {v.Note}" };
                            if (v.Resolved) resolvedByText++;
                        }
                }
            }

            WriteDeposit(dir, acc, diffs, theirsFiles.Select(f => Path.GetFileName(f)!).ToList(), measured.Count, shared, agree);
            int Cells(DiffKind k) => diffs.Where(d => d.Kind == k).Sum(d => d.Files.Count);
            var unresolved = diffs.Where(d => !d.Resolved).ToList();
            foreach (var d in unresolved)
                unresolvedAll.Add(string.Join('\t', new[] { acc, d.Sdrf, d.Column, d.Kind.ToString(), d.Files.Count.ToString(Inv), string.Join(", ", d.Files.Take(5)), d.Ours, d.Theirs, d.Evidence, d.Resolution }.Select(Clean)));
            var row = new[]
            {
                acc, string.Join(", ", theirsFiles.Select(f => Path.GetFileName(f))), shared.ToString(Inv), agree.ToString(Inv), Cells(DiffKind.Measured).ToString(Inv),
                Cells(DiffKind.Wording).ToString(Inv), (Cells(DiffKind.MoreSpecificOurs) + Cells(DiffKind.MoreSpecificTheirs)).ToString(Inv),
                Cells(DiffKind.OursMissing).ToString(Inv), Cells(DiffKind.TheirsMissing).ToString(Inv),
                diffs.Count(d => d.Kind == DiffKind.Numbering).ToString(Inv), diffs.Count(d => d.Kind == DiffKind.Grouping).ToString(Inv),
                conflicts.Count.ToString(Inv), resolvedByText.ToString(Inv), unresolved.Count.ToString(Inv), cost.ToString("0.0000", Inv), string.Join("; ", notes),
            };
            summary.Add(string.Join('\t', row.Select(Clean)));
            Console.WriteLine($"{acc}: " + string.Join(" | ", SummaryColumns.Zip(row).Skip(2).Where(x => x.Second.Length > 0).Select(x => $"{x.First} {x.Second}")));
        }
        File.WriteAllLines(Path.Combine(outRoot, "compare.independent.summary.tsv"), summary);
        File.WriteAllLines(Path.Combine(outRoot, "compare.independent.unresolved.tsv"), unresolvedAll);
        Console.WriteLine($"estimated model cost this run: ${spent:0.00}");
        return 0;
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string Files(IReadOnlyList<string> f) => f.Count <= 4 ? string.Join(", ", f) : $"{string.Join(", ", f.Take(4))} and {f.Count - 4} more";

    /// <summary>The deposit's difference table and its report for a person (the report alone carries the contacts).</summary>
    private static void WriteDeposit(string dir, string acc, List<Difference> diffs, List<string> theirs, int measuredFiles, int shared, int agree)
    {
        File.WriteAllLines(Path.Combine(dir, "compare.independent.tsv"),
            new[] { "sdrf\tcolumn\tkind\tfiles\tfile_list\tours\ttheirs\tmeasured\tresolved\tresolution\tvalue\tevidence" }
            .Concat(diffs.Select(d => string.Join('\t', new[] { d.Sdrf, d.Column, d.Kind.ToString(), d.Files.Count.ToString(Inv), string.Join(", ", d.Files),
                d.Ours, d.Theirs, d.Measured, d.Resolved ? "yes" : "no", d.Resolution, d.Value, d.Evidence }.Select(Clean)))));

        var md = new StringBuilder();
        md.Append($"# {acc}: our independent SDRF against {string.Join(" and ", theirs)}\n\n");
        md.Append($"Rows compared: {shared}; cells that agree: {agree}. Raw files with a measured instrument: {measuredFiles}.\n\n");
        var unresolved = diffs.Where(d => !d.Resolved).ToList();
        md.Append($"## For you to decide ({unresolved.Count})\n\n");
        if (unresolved.Count == 0) md.Append("Nothing: every difference was resolved.\n\n");
        foreach (var d in unresolved)
        {
            md.Append($"- **{d.Column}**, {d.Files.Count} file(s) ({Files(d.Files)}), in {d.Sdrf}\n");
            if (d.Kind == DiffKind.Grouping) md.Append($"  - {d.Ours}\n");
            else md.Append($"  - ours: `{d.Ours}`{(d.Evidence.Length > 0 ? $" (evidence: {d.Evidence})" : " (no located evidence)")}\n  - theirs: `{d.Theirs}`\n");
            md.Append($"  - {d.Resolution}\n");
        }
        void Section(string title, IEnumerable<Difference> items, Func<Difference, string> line)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            md.Append($"\n## {title} ({list.Count})\n\n");
            foreach (var d in list) md.Append("- ").Append(line(d)).Append('\n');
        }
        Section("Settled by the raw files", diffs.Where(d => d.Kind == DiffKind.Measured), d =>
            $"{d.Column}, {d.Files.Count} file(s): ours `{d.Ours}`, theirs `{d.Theirs}`, the raw files `{d.Measured}`: {d.Resolution}");
        Section("Decided by the curator (your decisions, from read --curator)", diffs.Where(d => d.Resolution.StartsWith("the curator decided", StringComparison.Ordinal)), d =>
            d.Kind == DiffKind.Grouping ? $"{d.Column}: {d.Ours}" : $"{d.Column}, {d.Files.Count} file(s) ({Files(d.Files)}): ours `{d.Ours}`, theirs `{d.Theirs}`");
        Section("Settled by the text", diffs.Where(d => d.Kind == DiffKind.Conflict && d.Resolved && d.Resolution.StartsWith("the text", StringComparison.Ordinal)), d =>
            $"{d.Column}, {d.Files.Count} file(s) ({Files(d.Files)}): ours `{d.Ours}`, theirs `{d.Theirs}`: {d.Resolution}, `{d.Value}`. {d.Evidence}");
        Section("Not disagreements: wording, a more specific value, numbering", diffs.Where(d => d.Kind is DiffKind.Wording or DiffKind.MoreSpecificOurs or DiffKind.MoreSpecificTheirs or DiffKind.Numbering), d =>
            d.Kind == DiffKind.Numbering ? $"{d.Column}: {d.Resolution} ({d.Ours})" : $"{d.Column}, {d.Files.Count} file(s): ours `{d.Ours}`, theirs `{d.Theirs}`: {d.Resolution}");
        var omissions = diffs.Where(d => d.Kind is DiffKind.OursMissing or DiffKind.TheirsMissing).GroupBy(d => (d.Column, d.Kind))
            .Select(g => $"{g.Key.Column}: {(g.Key.Kind == DiffKind.OursMissing ? "only theirs" : "only ours")}, {g.Sum(d => d.Files.Count)} cell(s)").ToList();
        if (omissions.Count > 0) md.Append($"\n## Stated by one side only ({omissions.Count} columns)\n\n").Append(string.Join('\n', omissions.Select(o => "- " + o))).Append('\n');

        string projectFile = Path.Combine(dir, "project.json");
        var contacts = File.Exists(projectFile) ? Contacts(JObject.Parse(File.ReadAllText(projectFile))) : new List<string>();
        md.Append("\n## The depositors, from PRIDE's record (private: never published)\n\n");
        md.Append(contacts.Count == 0 ? "PRIDE's record names nobody.\n" : string.Join('\n', contacts.Select(c => "- " + c)) + "\n");
        File.WriteAllText(Path.Combine(dir, "compare.independent.md"), md.ToString());
    }
}
