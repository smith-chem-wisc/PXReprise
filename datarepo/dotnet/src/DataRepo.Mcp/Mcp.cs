using System.Text;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest.Sources;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DataRepo.Mcp;

/// <summary>One tool as an agent sees it.</summary>
public sealed record ToolSpec(string Name, string Method, string Title, string Description, string InputSchema, string OutputSchema);

/// <summary>The local MCP server: three tools over one catalog (FRAMEWORK step 3, D12-D15). C# port of the wiring
/// half of <c>mcp.py</c>: the tool specs, the error payload, the stdio server, and <c>--install</c>.</summary>
/// <remarks>
/// <para>Run as <c>datarepo mcp --catalog &lt;path&gt;</c>, over stdio, against <b>one</b> catalog named by explicit
/// path. Three tools ship, and a fourth is added only where aging's benchmark shows a specific wrong answer (D12):
/// <c>datarepo_describe</c>, <c>datarepo_search</c> and <c>datarepo_sql</c>.</para>
/// <para>The stdio server is the official C# MCP SDK (<c>ModelContextProtocol</c>), at its lowest level: the tools
/// are listed with the Python server's own input and output schemas, and each call's answer is the text an agent
/// reads -- <see cref="PydanticJson"/> of the method's result, byte for byte what the Python SDK sends -- plus the
/// same value as structured content.</para>
/// </remarks>
public static class Mcp
{
    /// <summary>Tools are named with this prefix because an agent sees them alongside every other server's.</summary>
    public const string ToolPrefix = "datarepo_";

    /// <summary>The tools as an agent sees them: names, titles and descriptions word for word, and the argument
    /// schemas the Python SDK derives from the methods' signatures.</summary>
    public static readonly IReadOnlyList<ToolSpec> ToolSpecs =
    [
        new(
            $"{ToolPrefix}describe",
            "describe",
            "Describe the catalog, a table, an enum or a definition",
            "What this dataRepo catalog holds and what its columns mean. Call it with no target "
            + "first: it returns the datasets, every table with its row count, and the open "
            + "findings. Then call it with a table name for that table's columns and what each one "
            + "means, an enum name for its permissible values, or a definition id (e.g. "
            + "'pep:DEF-PEP') for the published text behind a stored number. A table listed "
            + "with 0 rows exists and is empty: the data has not been delivered, which is not the "
            + "same as the answer being no.",
            """{"properties":{"target":{"anyOf":[{"type":"string"},{"type":"null"}],"default":null,"title":"Target"},"detail":{"default":"concise","enum":["concise","detailed"],"title":"Detail","type":"string"}},"type":"object","title":"datarepo_describeArguments"}""",
            """{"type":"object","additionalProperties":true,"title":"datarepo_describeDictOutput"}"""),
        new(
            $"{ToolPrefix}search",
            "search",
            "Find the ids behind a name",
            "Look up a dataset accession, UniProt accession, gene symbol, peptide sequence, "
            + "modification, tissue, cell type, raw file or definition and get back the ids needed "
            + "to query it. Returns the hits AND the list of sources searched with the rows each "
            + "one holds, so that 'no hits' can be told apart from 'that table is empty in this "
            + "catalog'. Use it before writing SQL against an identifier you have not confirmed.",
            """{"properties":{"query":{"title":"Query","type":"string"},"kind":{"anyOf":[{"enum":["dataset","protein","peptide","modification","sample","run","definition","localization"],"type":"string"},{"type":"null"}],"default":null,"title":"Kind"},"limit":{"default":25,"title":"Limit","type":"integer"}},"required":["query"],"type":"object","title":"datarepo_searchArguments"}""",
            """{"type":"object","additionalProperties":true,"title":"datarepo_searchDictOutput"}"""),
        new(
            $"{ToolPrefix}sql",
            "sql",
            "Run one read-only SQL query",
            "Run one read-only DuckDB statement against the catalog. The catalog is derived and "
            + $"cannot be written to. Results are capped at {Sandbox.RowCap.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows or {Sandbox.CharCap.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} "
            + "characters with a 'truncated' flag, and a query still running after "
            + $"{PyFormat.FormatG(Sandbox.TimeoutSecondsDefault)} s is stopped -- so aggregate in "
            + "SQL rather than fetching rows to count them. Prefer the *_1pct views (psms_1pct, "
            + "peptidoforms_1pct, protein_groups_1pct): they apply the producing search engine's "
            + "acceptance rule, so you do not have to restate it. Call describe('<table>') first if "
            + "you are unsure of a column.",
            """{"properties":{"query":{"title":"Query","type":"string"},"max_rows":{"anyOf":[{"type":"integer"},{"type":"null"}],"default":null,"title":"Max Rows"}},"required":["query"],"type":"object","title":"datarepo_sqlArguments"}""",
            """{"type":"object","additionalProperties":true,"title":"datarepo_sqlDictOutput"}"""),
    ];

    /// <summary>The Python name of an error type, as <c>type(exc).__name__</c> reports it.</summary>
    public static string ErrorName(Exception exc) => exc switch
    {
        QueryRefusedException => "QueryRefused",
        QueryTimeoutException => "QueryTimeout",
        ToolException => "ToolError",
        CatalogException => "CatalogError",
        ManifestException => "ManifestError",
        DatasetExcludedException => "DatasetExcluded",
        UnsupportedProvenanceException => "UnsupportedProvenance",
        ReaderUnavailableException => "ReaderUnavailable",
        IngestException => "IngestError",
        DataRepoException => "DataRepoError",
        _ => exc.GetType().Name,
    };

    /// <summary>An error an agent can act on: what went wrong, and what to send instead.</summary>
    public static Dictionary<string, object?> ErrorPayload(Exception exc)
    {
        var hint = exc.GetType() switch
        {
            var t when t == typeof(QueryRefusedException) => "Send one statement, and only a question -- the catalog is read-only.",
            var t when t == typeof(QueryTimeoutException) => "Narrow it: filter on dataset_id, or aggregate instead of returning rows.",
            var t when t == typeof(ToolException) => "Check the argument named in the message.",
            var t when t == typeof(CatalogException) => "Call datarepo_describe('tables') to see what this catalog actually has.",
            _ => "Call datarepo_describe() for what this catalog holds.",
        };
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["error"] = ErrorName(exc),
            ["message"] = exc.Message,
            ["hint"] = hint,
        };
    }

    /// <summary>One tool call through the bound tool, as the Python <c>bound_tools</c> wrapper makes it: every
    /// <see cref="DataRepoException"/> becomes a RESULT, not an exception, because an agent that gets a structured
    /// <c>{error, message, hint}</c> can fix its call. Anything else propagates.</summary>
    public static Dictionary<string, object?> Call(CatalogServer server, string method, string? target = null,
        string detail = "concise", string? query = null, string? kind = null, long limit = CatalogServer.SearchLimit, long? maxRows = null)
    {
        try
        {
            return method switch
            {
                "describe" => server.Describe(target, detail),
                "search" => server.Search(query, kind, limit),
                "sql" => server.Sql(query ?? throw new ArgumentNullException(nameof(query)), maxRows),
                _ => throw new ArgumentOutOfRangeException(nameof(method), method, "no such tool"),
            };
        }
        catch (DataRepoException exc)
        {
            return ErrorPayload(exc);
        }
    }

    // --- the stdio server -------------------------------------------------------------------------------

    /// <summary>Run the stdio MCP server over one catalog until the client disconnects.</summary>
    /// <exception cref="CatalogException">There is no catalog at <paramref name="catalog"/>.</exception>
    public static async Task ServeAsync(string catalog, string name = "datarepo", TextWriter? stderr = null, CancellationToken cancellation = default)
    {
        using var server = new CatalogServer(catalog);
        var options = ServerOptions(server, name);
        // stderr, never stdout: stdout IS the protocol channel, and a banner printed there would be read as a
        // malformed JSON-RPC message and close the connection.
        (stderr ?? Console.Error).WriteLine(
            $"datarepo {CatalogBuilder.PackageVersion} mcp: serving catalog {PyValues.Str(server.Identity.CatalogId)} ({server.Box.Path}) over stdio");
        await using var transport = new StdioServerTransport(options, null);
        await using var mcp = McpServer.Create(transport, options);
        await mcp.RunAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>The SDK server's options for one open catalog: its name, the three tools with the Python server's
    /// schemas, and the handlers that answer them. Exposed so a test can serve over any transport.</summary>
    public static McpServerOptions ServerOptions(CatalogServer server, string name = "datarepo")
    {
        var gate = new SemaphoreSlim(1, 1);  // the catalog connection serves one call at a time
        var tools = ToolSpecs.Select(spec => new Tool
        {
            Name = spec.Name,
            Title = spec.Title,
            Description = spec.Description,
            InputSchema = JsonDocument.Parse(spec.InputSchema).RootElement.Clone(),
            OutputSchema = JsonDocument.Parse(spec.OutputSchema).RootElement.Clone(),
        }).ToList();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = name, Version = CatalogBuilder.PackageVersion },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }),
                CallToolHandler = async (request, ct) =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        return CallTool(server, request.Params?.Name ?? "", request.Params?.Arguments);
                    }
                    finally
                    {
                        gate.Release();
                    }
                },
            },
        };
        return options;
    }

    /// <summary>One <c>tools/call</c>: validate the arguments as the Python SDK's argument model does, call the tool,
    /// and answer with the text an agent reads and the same value as structured content.</summary>
    public static CallToolResult CallTool(CatalogServer server, string toolName, IDictionary<string, JsonElement>? arguments)
    {
        var spec = ToolSpecs.FirstOrDefault(s => s.Name == toolName);
        if (spec is null) return ErrorResult($"Unknown tool: {toolName}");
        arguments ??= new Dictionary<string, JsonElement>();
        Dictionary<string, object?> result;
        try
        {
            var errors = new List<string>();
            switch (spec.Method)
            {
                case "describe":
                {
                    var target = OptionalString(arguments, "target", errors);
                    var detail = Literal(arguments, "detail", ["concise", "detailed"], "concise", nullable: false, errors);
                    if (errors.Count > 0) return ValidationError(spec, errors);
                    result = Call(server, "describe", target: target, detail: detail!);
                    break;
                }
                case "search":
                {
                    var query = RequiredString(arguments, "query", errors);
                    var kind = Literal(arguments, "kind", CatalogServer.SearchKinds, null, nullable: true, errors);
                    var limit = Integer(arguments, "limit", CatalogServer.SearchLimit, nullable: false, errors);
                    if (errors.Count > 0) return ValidationError(spec, errors);
                    result = Call(server, "search", query: query, kind: kind, limit: limit!.Value);
                    break;
                }
                default:
                {
                    var query = RequiredString(arguments, "query", errors);
                    var maxRows = Integer(arguments, "max_rows", null, nullable: true, errors);
                    if (errors.Count > 0) return ValidationError(spec, errors);
                    result = Call(server, "sql", query: query, maxRows: maxRows);
                    break;
                }
            }
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            return ErrorResult($"Error executing tool {toolName}: {exc.Message}");
        }
        var text = PydanticJson.Serialize(result);
        using var structured = JsonDocument.Parse(PydanticJson.Serialize(result, indent: null, nanAsNull: true));
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = structured.RootElement.Clone(),
            IsError = false,
        };
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };

    /// <summary>pydantic's report on arguments that do not fit the signature, as the Python SDK words it:
    /// "Error executing tool datarepo_sql: 1 validation error for datarepo_sqlArguments", then each error.</summary>
    private static CallToolResult ValidationError(ToolSpec spec, List<string> errors) => ErrorResult(
        $"Error executing tool {spec.Name}: {errors.Count} validation error{(errors.Count == 1 ? "" : "s")} for {spec.Name}Arguments\n"
        + string.Join("\n", errors));

    /// <summary>One pydantic error: the field, the message, its type and the input, and the docs link pydantic 2.12
    /// appends.</summary>
    private static string PydanticError(string field, string message, string type, string inputValue, string inputType) =>
        $"{field}\n  {message} [type={type}, input_value={inputValue}, input_type={inputType}]\n"
        + $"    For further information visit https://errors.pydantic.dev/2.12/v/{type}";

    private static object? Parsed(JsonElement value) => SourcesPy.FromJson(value);

    private static string InputRepr(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => "None",
        _ => PyValues.Repr(Parsed(value)),
    };

    private static string InputType(JsonElement value) => SourcesPy.TypeName(Parsed(value));

    private static string? RequiredString(IDictionary<string, JsonElement> arguments, string name, List<string> errors)
    {
        if (!arguments.TryGetValue(name, out var value))
        {
            var all = arguments.ToDictionary(kv => kv.Key, kv => Parsed(kv.Value), StringComparer.Ordinal);
            errors.Add(PydanticError(name, "Field required", "missing", PyValues.Repr(all), "dict"));
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(PydanticError(name, "Input should be a valid string", "string_type", InputRepr(value), InputType(value)));
            return null;
        }
        return value.GetString();
    }

    private static string? OptionalString(IDictionary<string, JsonElement> arguments, string name, List<string> errors)
    {
        if (!arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(PydanticError(name, "Input should be a valid string", "string_type", InputRepr(value), InputType(value)));
            return null;
        }
        return value.GetString();
    }

    private static string? Literal(IDictionary<string, JsonElement> arguments, string name, IReadOnlyList<string> allowed, string? fallback, bool nullable, List<string> errors)
    {
        if (!arguments.TryGetValue(name, out var value)) return fallback;
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString()!)) return value.GetString();
        var options = string.Join(", ", allowed.Take(allowed.Count - 1).Select(a => $"'{a}'")) + $" or '{allowed[^1]}'";
        errors.Add(PydanticError(name, $"Input should be {options}", "literal_error", InputRepr(value), InputType(value)));
        return null;
    }

    /// <summary>pydantic's lax integer: an integer, a bool, a float with no fraction, or a string of digits.</summary>
    private static long? Integer(IDictionary<string, JsonElement> arguments, string name, long? fallback, bool nullable, List<string> errors)
    {
        if (!arguments.TryGetValue(name, out var value)) return fallback;
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.Number when value.TryGetInt64(out var l):
                return l;
            case JsonValueKind.Number when value.TryGetDouble(out var d):
                if (d == Math.Floor(d) && Math.Abs(d) < 9.2e18) return (long)d;
                errors.Add(PydanticError(name, "Input should be a valid integer, got a number with a fractional part", "int_from_float", InputRepr(value), InputType(value)));
                return null;
            case JsonValueKind.String:
                if (long.TryParse(PyValues.Strip(value.GetString()!), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
                errors.Add(PydanticError(name, "Input should be a valid integer, unable to parse string as an integer", "int_parsing", InputRepr(value), InputType(value)));
                return null;
            default:
                errors.Add(PydanticError(name, "Input should be a valid integer", "int_type", InputRepr(value), InputType(value)));
                return null;
        }
    }

    // --- --install ----------------------------------------------------------------------------------------

    /// <summary>Where Claude Code keeps its user-level MCP server registrations: <c>CLAUDE_CONFIG_PATH</c> when set,
    /// else <c>~/.claude.json</c>.</summary>
    public static string ClaudeConfigPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_PATH");
        if (!string.IsNullOrEmpty(overridePath)) return SourcesPy.PathStr(overridePath);
        return SourcesPy.PathStr(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));
    }

    /// <summary>The running <c>datarepo</c> executable, as an MCP client must start it: the self-contained or
    /// apphost executable itself, or <c>dotnet &lt;datarepo.dll&gt;</c> when it runs under the shared host.</summary>
    public static (string Command, List<string> Prefix) RunningExecutable()
    {
        var process = Environment.ProcessPath ?? "datarepo";
        var file = System.IO.Path.GetFileNameWithoutExtension(process);
        if (string.Equals(file, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(dll)) return (process, [dll]);
        }
        return (process, []);
    }

    /// <summary>The <c>mcpServers</c> entry for this catalog. Absolute paths: the client sets its own cwd.</summary>
    /// <param name="catalog">The catalog; resolved to an absolute path.</param>
    /// <param name="executable">The command to register; default <see cref="RunningExecutable"/>.</param>
    public static Dictionary<string, object?> InstallEntry(string catalog, string? executable = null)
    {
        var (command, prefix) = executable is null ? RunningExecutable() : (executable, new List<string>());
        var args = prefix.Concat(["mcp", "--catalog", Resolve(catalog)]).Select(a => (object?)a).ToList();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command,
            ["args"] = args,
            ["env"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
    }

    /// <summary><c>str(Path(p).resolve())</c>: absolute, with a symbolic link at the end followed.</summary>
    private static string Resolve(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        try
        {
            var target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) full = target.FullName;
        }
        catch (IOException)
        {
            // an unreadable link resolves to itself, as Path.resolve(strict=False) does
        }
        return SourcesPy.PathStr(full);
    }

    /// <summary>Register this server with Claude Code, writing the config rather than asking for hand edits.</summary>
    /// <returns><c>{"config", "name", "entry", "action": "added"|"updated"|"unchanged"}</c>.</returns>
    /// <exception cref="ToolException">An entry of that name already points somewhere else and
    /// <paramref name="force"/> is not set, or the config is not JSON.</exception>
    /// <exception cref="CatalogException">There is no catalog at <paramref name="catalog"/>.</exception>
    public static Dictionary<string, object?> Install(string catalog, string name = "datarepo", string? config = null, bool force = false, string? executable = null)
    {
        var path = Resolve(catalog);
        if (!File.Exists(path)) throw new CatalogException($"no catalog at {path}; nothing to serve");
        var configPath = config is not null ? SourcesPy.PathStr(config) : ClaudeConfigPath();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (File.Exists(configPath))
        {
            var loaded = ReadConfig(configPath, invalid: message => new ToolException(
                $"{configPath} is not valid JSON ({message}); fix or move it rather than have this "
                + "overwrite a config you cannot read"));
            if (PyValues.Truthy(loaded))
                document = loaded as Dictionary<string, object?>
                    ?? throw new InvalidOperationException($"'{SourcesPy.TypeName(loaded)}' object has no attribute 'setdefault'");
        }
        if (!document.TryGetValue("mcpServers", out var serversValue))
            document["mcpServers"] = serversValue = new Dictionary<string, object?>(StringComparer.Ordinal);
        var servers = serversValue as Dictionary<string, object?>
            ?? throw new InvalidOperationException($"'{SourcesPy.TypeName(serversValue)}' object has no attribute 'get'");
        var entry = InstallEntry(path, executable);

        var existing = servers.GetValueOrDefault(name);
        string action;
        if (servers.ContainsKey(name) && PlainEquals(existing, entry))
            action = "unchanged";
        else if (existing is not null && !force)
        {
            var existingArgs = (existing as Dictionary<string, object?>)?.GetValueOrDefault("args", new List<object?>()) as IEnumerable<object?> ?? [];
            throw new ToolException(
                $"{configPath} already has an MCP server named {PyFormat.Repr(name)} pointing at "
                + $"{PyFormat.Repr(string.Join(" ", existingArgs.Select(a => a as string ?? throw new InvalidOperationException("sequence item: expected str instance"))))}. Pass --force to repoint it, or --name to "
                + "register this catalog alongside it.");
        }
        else
        {
            action = existing is not null ? "updated" : "added";
            servers[name] = entry;
        }

        if (action != "unchanged")
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(configPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // A config this rewrites is the user's whole Claude Code state, so it is written through a temporary file
            // in the same directory: a crash mid-write leaves the old one intact. Python's write_text writes the
            // platform's line ending, so this does too.
            var temp = configPath + ".datarepo-tmp";
            var text = (PyFormat.JsonIndented(document) + "\n").Replace("\n", Environment.NewLine);
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, configPath, overwrite: true);
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["config"] = configPath,
            ["name"] = name,
            ["entry"] = entry,
            ["action"] = action,
        };
    }

    /// <summary><c>json.loads(path.read_text(encoding="utf-8"))</c>: strict UTF-8, and a byte-order mark is an error
    /// as it is in Python.</summary>
    private static object? ReadConfig(string path, Func<string, Exception> invalid)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        if (text.Length > 0 && text[0] == '﻿') throw invalid("Unexpected UTF-8 BOM (decode using utf-8-sig): line 1 column 1 (char 0)");
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 1000 });
            return SourcesPy.FromJson(doc.RootElement);
        }
        catch (JsonException exc)
        {
            throw invalid(exc.Message);
        }
    }

    /// <summary>Python's <c>==</c> on plain JSON values: mappings compare without regard to order.</summary>
    private static bool PlainEquals(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (Dictionary<string, object?> x, Dictionary<string, object?> y) =>
            x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && PlainEquals(kv.Value, v)),
        (List<object?> x, List<object?> y) => x.Count == y.Count && x.Zip(y).All(p => PlainEquals(p.First, p.Second)),
        (string x, string y) => x == y,
        (bool x, bool y) => x == y,
        (long x, long y) => x == y,
        (double x, double y) => x == y,
        (long x, double y) => x == y,
        (double x, long y) => x == y,
        _ => Equals(a, b),
    };

    /// <summary>Every datarepo MCP server registered in the config, for <c>datarepo doctor</c> and <c>--list</c>.</summary>
    /// <remarks>An entry is ours when <c>datarepo</c> appears in its command or its arguments. The Python looked at
    /// the arguments only, which held <c>-m datarepo.cli</c>; the executable's arguments are just
    /// <c>mcp --catalog &lt;path&gt;</c>, so the name is in the command.</remarks>
    public static Dictionary<string, Dictionary<string, object?>> InstalledEntries(string? config = null)
    {
        var configPath = config is not null ? SourcesPy.PathStr(config) : ClaudeConfigPath();
        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        if (!File.Exists(configPath)) return result;
        object? document;
        try
        {
            document = ReadConfig(configPath, message => new ToolException(message));
        }
        catch (ToolException)
        {
            return result;
        }
        if (document is not Dictionary<string, object?> d) return result;
        if (d.GetValueOrDefault("mcpServers") is not Dictionary<string, object?> servers) return result;
        foreach (var (name, value) in servers)
        {
            if (value is not Dictionary<string, object?> entry) continue;
            var args = entry.GetValueOrDefault("args") as List<object?> ?? [];
            var words = string.Join(" ", args.Select(PyValues.Str)) + " " + (entry.GetValueOrDefault("command") is string c ? c : "");
            if (words.Contains("datarepo", StringComparison.Ordinal)) result[name] = entry;
        }
        return result;
    }
}
