using PXReprise.Cli;
using Tomlyn;
using Tomlyn.Model;

namespace PXReprise.Config;

/// <summary>A question or profile file that does not match its schema. A usage error: the file is the caller's input.</summary>
public sealed class ConfigException(string file, string message) : UsageException($"{Path.GetFileName(file)}: {message}")
{
    public string File { get; } = file;
}

/// <summary>
/// Typed, schema-checked access to one TOML table. Every key read is recorded, and <see cref="RefuseUnknownKeys"/>
/// rejects anything left unread, so a misspelt key is an error rather than a silently ignored setting.
/// </summary>
public sealed class TomlSection
{
    private readonly TomlTable _table;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    public string File { get; }
    public string Path { get; }

    public TomlSection(TomlTable table, string file, string path)
    {
        _table = table;
        File = file;
        Path = path;
    }

    public static TomlSection Load(string file)
    {
        if (!System.IO.File.Exists(file)) throw new UsageException($"no such file: {file}");
        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(System.IO.File.ReadAllText(file))
                    ?? throw new ConfigException(file, "empty document");
        }
        catch (Exception e) when (e is not UsageException)
        {
            throw new ConfigException(file, $"not valid TOML: {e.Message}");
        }
        return new TomlSection(table, file, "");
    }

    private string Where(string key) => Path.Length == 0 ? key : $"{Path}.{key}";

    private object? Raw(string key)
    {
        _read.Add(key);
        return _table.TryGetValue(key, out var v) ? v : null;
    }

    public bool Has(string key) => _table.ContainsKey(key);

    public string RequiredString(string key) =>
        OptionalString(key) is { Length: > 0 } s ? s : throw new ConfigException(File, $"'{Where(key)}' is required");

    public string? OptionalString(string key) => Raw(key) switch
    {
        null => null,
        string s => s,
        var o => throw new ConfigException(File, $"'{Where(key)}' must be a string, not {Describe(o)}"),
    };

    public long RequiredInteger(string key) =>
        OptionalInteger(key) ?? throw new ConfigException(File, $"'{Where(key)}' is required");

    public long? OptionalInteger(string key) => Raw(key) switch
    {
        null => null,
        long l => l,
        var o => throw new ConfigException(File, $"'{Where(key)}' must be an integer, not {Describe(o)}"),
    };

    public double? OptionalNumber(string key) => Raw(key) switch
    {
        null => null,
        long l => l,
        double d => d,
        var o => throw new ConfigException(File, $"'{Where(key)}' must be a number, not {Describe(o)}"),
    };

    public bool? OptionalBool(string key) => Raw(key) switch
    {
        null => null,
        bool b => b,
        var o => throw new ConfigException(File, $"'{Where(key)}' must be true or false, not {Describe(o)}"),
    };

    /// <summary>An array of strings. A single string is NOT accepted as a one-element list: say what you mean.</summary>
    public IReadOnlyList<string> StringList(string key, bool required = false)
    {
        switch (Raw(key))
        {
            case null when required: throw new ConfigException(File, $"'{Where(key)}' is required");
            case null: return Array.Empty<string>();
            case TomlArray arr:
                var list = new List<string>();
                foreach (var item in arr)
                    list.Add(item as string ?? throw new ConfigException(File, $"'{Where(key)}' must hold only strings"));
                if (required && list.Count == 0) throw new ConfigException(File, $"'{Where(key)}' must not be empty");
                return list;
            case var o: throw new ConfigException(File, $"'{Where(key)}' must be an array of strings, not {Describe(o)}");
        }
    }

    public TomlSection? OptionalTable(string key) => Raw(key) switch
    {
        null => null,
        TomlTable t => new TomlSection(t, File, Where(key)),
        var o => throw new ConfigException(File, $"'{Where(key)}' must be a table, not {Describe(o)}"),
    };

    public TomlSection RequiredTable(string key) =>
        OptionalTable(key) ?? throw new ConfigException(File, $"table [{Where(key)}] is required");

    /// <summary>Every key of this table, for tables whose keys are data (organisms, holds).</summary>
    public IEnumerable<string> Keys => _table.Keys;

    public void RefuseUnknownKeys()
    {
        var unknown = _table.Keys.Where(k => !_read.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new ConfigException(File, $"unknown key(s) {string.Join(", ", unknown.Select(k => $"'{Where(k)}'"))}");
    }

    private static string Describe(object o) => o switch
    {
        string => "a string",
        long or double => "a number",
        bool => "a boolean",
        TomlArray => "an array",
        TomlTable => "a table",
        _ => o.GetType().Name,
    };
}
