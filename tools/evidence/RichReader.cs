// The model reading with figures and documents (sdrf G38; the user, 2026-11-04: "include information in figures too").
//
// It wraps mzLib's ModelEvidenceReader rather than changing it: the same system prompt with one paragraph added, the
// same claim rules, and the same Interpret, so every text claim is checked exactly as before. What is new:
//   - the request carries the paper's figure images and supplementary PDFs as content blocks, each introduced by a label
//     (`[fig:F2]`, `[supp:mmc1.pdf]`), after the text;
//   - the text has more in it: abstract, figure legends, supplementary captions, supplement prose, the deposit's own files;
//   - G38's open design gap, "an image claim cannot pass the quote check": the model writes the words a figure or page
//     shows into `transcripts` (one per label), and a claim from it quotes its transcript. The transcripts join the text
//     Interpret checks quotes against, so the existing rules apply unchanged (the quote must state the value; a row quote
//     must be whole cells; a per-file claim must name its file). A transcript is the model's own reading of pixels and is
//     NOT checked against them: a claim resting on one is written with source `figure` so it can be filtered or audited,
//     and transcripts.tsv keeps every transcript for review;
//   - an overloaded or rate-limited API is retried (two of the pilot's 15 deposits were lost to `overloaded_error`).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Readers;
using SampleEvidenceModel;

internal sealed record RichResult(ModelEvidenceResult Result, IReadOnlyList<(string Label, string Text)> Transcripts, int FigureClaims, int Attempts,
    int Repaired = 0, string Raw = "");

internal static class RichReader
{
    internal const string FigurePrompt = """

        Figures and documents: the paper's figures and some supplementary documents are attached after the text as images
        and PDF pages, each introduced by its label, e.g. [fig:F2] or [supp:mmc1.pdf]. Figures often draw the design (the
        groups, ages, sex, numbers of animals, time points, the channel or plex layout) and supplementary files often hold
        the sample details: read them as carefully as the text. Their words are not in the text above, so to claim from
        one, first copy the words it shows that state the claim into transcripts (figure: its label; text: the words as
        shown, e.g. "Young (3 mo) n = 6 | Aged (24 mo) n = 6"), then put that label at the start of the quote and quote
        from your transcript, with source "figure". Transcribe only words about the samples and the design, never data
        values or axis ticks, and never a description: a colour, a position or an arrangement alone states nothing. A
        figure legend is text: quote it as text.
        """;

    /// <summary>Reads one deposit with its attachments; retries an overloaded or rate-limited API.</summary>
    internal static async Task<RichResult> ReadAsync(AnthropicClient client, string model, PublicationInput input, string prompt,
        IReadOnlyList<Attachment> attachments, Action<string> log, CancellationToken ct = default)
    {
        int[] waitSeconds = { 30, 90, 180, 300 };
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var r = await ReadOnceAsync(client, model, input, prompt, attachments, ct).ConfigureAwait(false);
                return r with { Attempts = attempt };
            }
            catch (Exception e) when (attempt <= waitSeconds.Length && Transient(e))
            {
                log($"{input.Accession}: {e.GetType().Name} ({Short(e.Message)}); retry {attempt} of {waitSeconds.Length} in {waitSeconds[attempt - 1]} s");
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds[attempt - 1]), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Overloaded (529, also sent as an SSE error event mid-stream), rate limited (429) or a 5xx.</summary>
    internal static bool Transient(Exception e) => e is Anthropic5xxException or AnthropicRateLimitException
        || (e is AnthropicSseException && (e.Message.Contains("overloaded_error", StringComparison.Ordinal) || e.Message.Contains("api_error", StringComparison.Ordinal)))
        || e is AnthropicIOException;

    private static string Short(string s) => s.Length <= 160 ? s : s[..160] + "...";

    private static async Task<RichResult> ReadOnceAsync(AnthropicClient client, string model, PublicationInput input, string prompt,
        IReadOnlyList<Attachment> attachments, CancellationToken ct)
    {
        var content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = prompt } };
        foreach (var a in attachments)
        {
            content.Add(new BetaTextBlockParam { Text = $"{a.Label} {a.Describe}" });
            if (a.Kind == "image")
                content.Add(new BetaImageBlockParam(new BetaBase64ImageSource
                {
                    Data = Convert.ToBase64String(a.Bytes),
                    MediaType = a.MediaType switch
                    {
                        "image/png" => MediaType.ImagePng,
                        "image/gif" => MediaType.ImageGif,
                        "image/webp" => MediaType.ImageWebP,
                        _ => MediaType.ImageJpeg,
                    },
                }));
            else
                content.Add(new BetaRequestDocumentBlock(new BetaBase64PdfSource(Convert.ToBase64String(a.Bytes))) { Title = a.Label });
        }
        if (attachments.Count > 0)
            content.Add(new BetaTextBlockParam { Text = "That is everything: the text above and the figures and documents just shown." });

        var response = await client.Beta.Messages.CreateStreaming(new MessageCreateParams
        {
            Model = model,
            MaxTokens = ModelEvidenceReader.MaxOutputTokens,
            System = new List<BetaTextBlockParam> { new() { Text = ModelEvidenceReader.SystemPrompt + FigurePrompt, CacheControl = new BetaCacheControlEphemeral() } },
            Messages = [new() { Role = Role.User, Content = content }],
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig { Effort = Effort.High, Format = new BetaJsonOutputFormat { Schema = Schema } },
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        }, ct).Aggregate().ConfigureAwait(false);

        string stop = response.StopReason?.Raw() ?? "";
        var text = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out var t)) text.Append(t.Text);
        RichResult result;
        if (stop == "max_tokens")
            result = new RichResult(new ModelEvidenceResult(Array.Empty<SdrfEvidence>(), new[] { $"no reading: the answer was cut off at the output limit ({ModelEvidenceReader.MaxOutputTokens} tokens)" }, null, stop, 0, 0, 0), Array.Empty<(string, string)>(), 0, 1);
        else if (stop == "refusal" || text.Length == 0)
            result = new RichResult(new ModelEvidenceResult(Array.Empty<SdrfEvidence>(), new[] { $"no reading: stop reason '{stop}'" }, null, stop, 0, 0, 0), Array.Empty<(string, string)>(), 0, 1);
        else
            result = Interpret(text.ToString(), input, attachments, stop);
        return result with
        {
            Raw = text.ToString(),
            Result = result.Result with
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                CacheReadTokens = response.Usage.CacheReadInputTokens ?? 0,
            }
        };
    }

    /// <summary>
    /// The transcripts of known labels join the checked text; mzLib's Interpret then judges every claim; a kept claim the
    /// model gave as source "figure" is written with source <c>figure</c>. Pure: no I/O.
    /// </summary>
    internal static RichResult Interpret(string json, PublicationInput input, IReadOnlyList<Attachment> attachments, string stop)
    {
        var labels = attachments.Select(a => a.Label.Trim('[', ']')).ToHashSet(StringComparer.Ordinal);
        var transcripts = new List<(string, string)>();
        // A figure claim's key -> the label it cited (empty when it cited none).
        var figureKeys = new Dictionary<(string, string, string, string), string>();
        var extraRejected = new List<string>();
        int repaired = 0;
        try
        {
            if (JsonNode.Parse(json) is JsonObject root)
            {
                if (root["transcripts"] is JsonArray ts)
                    foreach (var t in ts.OfType<JsonObject>())
                    {
                        string label = (Str(t, "figure")).Trim().Trim('[', ']');
                        string words = Str(t, "text").Trim();
                        if (words.Length == 0) continue;
                        if (!labels.Contains(label)) { extraRejected.Add($"transcript of '{label}': not a label that was shown"); continue; }
                        transcripts.Add((label, words.Length <= MaxTranscriptChars ? words : words[..MaxTranscriptChars]));
                    }
                // The label a quote starts with says WHERE; the words after it are what is checked. Left on, "[fig:F1] male
                // C57BL/6J" is in no text: the transcript has other words between the label and these.
                if (root["claims"] is JsonArray cs)
                    foreach (var c in cs.OfType<JsonObject>())
                    {
                        string label = StripLabel(c, "quote", labels);
                        if (Str(c, "source") == "figure")
                            figureKeys[(Str(c, "data_file").Trim() + "|" + Str(c, "data_file_pattern").Trim(), Str(c, "label").Trim(),
                                Str(c, "column").Trim().ToLowerInvariant(), Str(c, "value").Trim().ToLowerInvariant())] = label;
                    }
                var designQuotes = new List<(JsonObject, string)>();
                if (root["design"] is JsonObject d)
                {
                    foreach (var q in new[] { "plexes_quote", "technical_replicates_quote", "fractions_quote", "runs_quote" }) { StripLabel(d, q, labels); designQuotes.Add((d, q)); }
                    if (d["groups"] is JsonArray gs) foreach (var g in gs.OfType<JsonObject>()) { StripLabel(g, "quote", labels); designQuotes.Add((g, "quote")); }
                }
                // Spacing and punctuation: a quote whose letters and digits are, in order, on one line of the text is that
                // line's own words (PXD047289's export header writes "Sample, N , 4"; the model wrote "Sample, N, 4").
                var checkedText = new QuoteRepair(CheckedText(input, transcripts));
                foreach (var (o, field) in (root["claims"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(c => (c, "quote")).Concat(designQuotes))
                    if (checkedText.TryRepair(Str(o, field)) is { } exact) { o[field] = exact; repaired++; }
                json = root.ToJsonString();
            }
        }
        catch (JsonException) { /* Interpret below reports it */ }

        var checkedInput = transcripts.Count == 0 ? input : input with
        {
            PaperText = input.PaperText + TranscriptSection(transcripts),
        };
        var r = ModelEvidenceReader.Interpret(json, checkedInput, stop);
        int figureClaims = 0;
        var evidence = r.Evidence.Select(e =>
        {
            if (!figureKeys.TryGetValue((e.DataFile + "|" + e.DataFilePattern, e.Label, e.Column, e.Value.ToLowerInvariant()), out string? label)) return e;
            figureClaims++;
            // mzLib writes the locator as `figure: "<quote>"`; name the figure instead when the claim cited one.
            string locator = label.Length > 0 && e.Locator.StartsWith("figure: ", StringComparison.Ordinal) ? $"[{label}] {e.Locator["figure: ".Length..]}" : e.Locator;
            return e with { Source = "figure", Locator = locator };
        }).ToList();
        return new RichResult(r with { Evidence = evidence, Rejected = r.Rejected.Concat(extraRejected).ToList() }, transcripts, figureClaims, 1, repaired);
    }

    private static string TranscriptSection(List<(string, string)> transcripts) => transcripts.Count == 0 ? ""
        : "\n## Transcripts of figures and documents\n" + string.Join("\n", transcripts.Select(t => $"[{t.Item1}] {t.Item2}"));

    /// <summary>The text mzLib's Interpret checks quotes against (its corpus), before normalising.</summary>
    private static string CheckedText(PublicationInput input, List<(string, string)> transcripts) =>
        string.Join("\n", input.RecordText, input.PaperText + TranscriptSection(transcripts),
            PublicationText.Tables(input.Tables, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));

    /// <summary>
    /// Finds a quote that mzLib's exact check would miss only for spacing or punctuation: its letters and digits (at least
    /// <see cref="MinRepairChars"/>) occur in that order within ONE line of the checked text. The repaired quote is that
    /// stretch of the text itself, so the check that follows is still exact. A row-cited quote is left alone (its own rule
    /// wants whole cells), and so is a quote already found.
    /// </summary>
    internal sealed class QuoteRepair
    {
        private readonly string _norm;
        private readonly string _squeezed;
        private readonly int[] _at;

        internal QuoteRepair(string text)
        {
            _norm = Norm(text);
            var sb = new StringBuilder(_norm.Length);
            var at = new List<int>(_norm.Length);
            for (int i = 0; i < _norm.Length; i++)
                if (char.IsLetterOrDigit(_norm[i])) { sb.Append(_norm[i]); at.Add(i); }
            _squeezed = sb.ToString();
            _at = at.ToArray();
        }

        internal string? TryRepair(string quote)
        {
            if (quote.Length == 0 || RowRef.IsMatch(quote)) return null;
            string q = Norm(quote);
            if (_norm.Contains(q, StringComparison.Ordinal)) return null;
            string sq = string.Concat(q.Where(char.IsLetterOrDigit));
            if (sq.Length < MinRepairChars) return null;
            for (int k = _squeezed.IndexOf(sq, StringComparison.Ordinal); k >= 0; k = _squeezed.IndexOf(sq, k + 1, StringComparison.Ordinal))
            {
                int from = _at[k], to = _at[k + sq.Length - 1];
                // One line only: a stretch across a newline joins two rows or paragraphs into words no line says.
                if (_norm.IndexOf('\n', from, to - from + 1) < 0) return _norm[from..(to + 1)];
            }
            return null;
        }

        // mzLib's Norm (ModelEvidenceReader, private): the same normalisation, so a repaired quote is found exactly.
        // Unlike mzLib's, newlines are kept (as '\n') so the one-line rule can see them.
        private static string Norm(string s) => Regex.Replace(Regex.Replace(s.Normalize(System.Text.NormalizationForm.FormKC), "[­​‌‍﻿]", "")
                .Replace('‐', '-').Replace('‑', '-').Replace('–', '-').Replace('—', '-')
                .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"'),
                @"[^\S\n]+", " ").Replace(" \n", "\n").Replace("\n ", "\n").Trim().ToLowerInvariant();

        private static readonly Regex RowRef = new(@"\[([^\[\]]+![^\[\]]*R\d+)\]", RegexOptions.Compiled);
    }

    /// <summary>The fewest letters and digits a repaired quote may have: shorter strings recur by chance.</summary>
    internal const int MinRepairChars = 12;

    private static readonly Regex LeadingLabel = new(@"^\s*\[((?:fig|supp|deposit):[^\]]+)\]\s*:?\s*", RegexOptions.Compiled);

    private static string Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    /// <summary>Removes a shown label from the start of <paramref name="o"/>[<paramref name="field"/>]; returns the label or "".</summary>
    private static string StripLabel(JsonObject o, string field, HashSet<string> labels)
    {
        string q = Str(o, field);
        var m = LeadingLabel.Match(q);
        if (!m.Success || !labels.Contains(m.Groups[1].Value)) return "";
        o[field] = q[m.Length..];
        return m.Groups[1].Value;
    }

    internal const int MaxTranscriptChars = 4_000;

    private static readonly Dictionary<string, JsonElement> Schema = BuildSchema();

    /// <summary>mzLib's schema (ModelEvidenceReader.BuildSchema) with source "figure" and the transcripts added.</summary>
    private static Dictionary<string, JsonElement> BuildSchema()
    {
        var str = new { type = "string" };
        var integer = new { type = "integer" };
        var claim = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "data_file", "data_file_pattern", "label", "column", "value", "source", "quote", "confidence" },
            properties = new
            {
                data_file = str, data_file_pattern = str, label = str, column = str, value = str,
                source = new { type = "string", @enum = new[] { "paper", "supplement", "pride record", "figure" } },
                quote = str,
                confidence = new { type = "string", @enum = new[] { "likely", "guess" } }
            }
        };
        var group = new { type = "object", additionalProperties = false, required = new[] { "name", "samples", "quote" }, properties = new { name = str, samples = integer, quote = str } };
        var design = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "groups", "plexes", "plexes_quote", "technical_replicates", "technical_replicates_quote",
                "fractions_per_sample", "fractions_quote", "runs_stated", "runs_quote" },
            properties = new
            {
                groups = new { type = "array", items = group },
                plexes = integer, plexes_quote = str,
                technical_replicates = integer, technical_replicates_quote = str,
                fractions_per_sample = integer, fractions_quote = str,
                runs_stated = integer, runs_quote = str
            }
        };
        var transcript = new { type = "object", additionalProperties = false, required = new[] { "figure", "text" }, properties = new { figure = str, text = str } };
        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "transcripts", "claims", "design" }),
            ["properties"] = JsonSerializer.SerializeToElement(new { transcripts = new { type = "array", items = transcript }, claims = new { type = "array", items = claim }, design }),
        };
    }

    /// <summary>The user text: mzLib's sections, with the paper's wider text, the supplement prose and the attachment list.</summary>
    internal static string Prompt(PublicationInput input, string paperText, string prose, IReadOnlyList<Attachment> attachments)
    {
        int max = ModelEvidenceReader.MaxPaperChars;
        var sb = new StringBuilder();
        sb.Append("# Deposit ").Append(input.Accession).Append("\n\n## PRIDE record\n").Append(input.RecordText).Append("\n\n");
        sb.Append("## Paper: methods, tables, abstract, figure legends, supplementary captions\n").Append(paperText.Length == 0 ? "(no full text available)"
            : paperText.Length <= max ? paperText : paperText[..max] + $"\n[paper cut at {max:N0} of {paperText.Length:N0} characters]").Append("\n\n");
        sb.Append("## Supplementary files and the deposit's own files: their text\n").Append(prose.Length == 0 ? "(none)"
            : prose.Length <= MaxProseChars ? prose : prose[..MaxProseChars] + $"\n[cut at {MaxProseChars:N0} of {prose.Length:N0} characters]").Append("\n\n");
        sb.Append("## Supplementary tables (the paper's and the deposit's own files)\n").Append(input.Tables.Count > 0 ? PublicationText.Tables(input.Tables) : "(none)").Append("\n\n");
        sb.Append($"## Raw files ({input.RawFiles.Count})\n").Append(PublicationText.Files(input.RawFiles));
        sb.Append("\n## Already found by the rule-based reader\n");
        if (input.RuleEvidence.Count == 0) sb.Append("(nothing)\n");
        foreach (var e in input.RuleEvidence.Take(300))
            sb.Append($"{(e.DataFile.Length > 0 ? e.DataFile : "(all files)")}\t{e.Label}\t{e.Column}\t{e.Value}\n");
        sb.Append("\n## Figures and documents attached after this text\n");
        if (attachments.Count == 0) sb.Append("(none)\n");
        foreach (var a in attachments) sb.Append($"{a.Label} {a.Describe}\n");
        return sb.ToString();
    }

    internal const int MaxProseChars = 120_000;
}
