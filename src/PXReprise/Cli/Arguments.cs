namespace PXReprise.Cli;

/// <summary>
/// <c>&lt;verb words...&gt; [positional...] --name value --flag</c>, dependency-free, in the mzLib bridge's style.
/// The verb is the leading words that are not options; the caller decides how many words a verb has.
/// </summary>
public sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Words { get; }

    public Arguments(IReadOnlyList<string> argv, IReadOnlySet<string>? flags = null)
    {
        var words = new List<string>();
        for (int i = 0; i < argv.Count; i++)
        {
            string a = argv[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(a);
                continue;
            }
            string name = a[2..];
            if (name.Length == 0) throw new UsageException("an option name is missing after '--'");
            if (_options.ContainsKey(name)) throw new UsageException($"--{name} is given twice");
            if (flags is not null && flags.Contains(name))
            {
                _options[name] = null;
                continue;
            }
            if (i + 1 >= argv.Count || argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"--{name} needs a value");
            _options[name] = argv[++i];
        }
        Words = words;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Option(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public string Required(string name) =>
        Option(name) is { Length: > 0 } v ? v : throw new UsageException($"--{name} is required");

    /// <summary>Refuses options the command does not take, rather than silently ignoring a typo.</summary>
    public void AllowOnly(params string[] names)
    {
        var unknown = _options.Keys.Where(k => !names.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new UsageException($"unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
    }
}
