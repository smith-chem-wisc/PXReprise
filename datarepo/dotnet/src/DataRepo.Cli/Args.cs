namespace DataRepo.Cli;

/// <summary>A command's arguments, parsed the way Python's argparse parsed them for the same command line.</summary>
/// <remarks>
/// Operators call <c>datarepo</c> from scripts (aging's batch, PXReprise), so a C# command must accept exactly
/// the arguments the Python one did and fail the same way: a usage error prints <c>usage: ...</c> and an
/// <c>error:</c> line on stderr and exits 2. Supported: positionals (one, <c>*</c> or <c>+</c>), flags,
/// single-value options (<c>--x V</c> and <c>--x=V</c>), and <c>append</c> options. Long options may be
/// abbreviated to an unambiguous prefix, as argparse allows.
/// </remarks>
public sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _lists = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _positional = new(StringComparer.Ordinal);

    public string? Value(string name) => _values.GetValueOrDefault(name);
    public IReadOnlyList<string> List(string name) => _lists.GetValueOrDefault(name) ?? [];
    public bool Flag(string name) => _flags.Contains(name);
    public string Positional(string name) => _positional[name][0];
    public IReadOnlyList<string> Positionals(string name) => _positional.GetValueOrDefault(name) ?? [];

    /// <summary>A usage error: argparse's message, exit code 2.</summary>
    public sealed class UsageException(string usage, string message) : Exception(message)
    {
        public string Usage { get; } = usage;
    }

    public enum Kind { Flag, Value, Append }

    /// <summary>One command's grammar.</summary>
    /// <param name="prog">The command as typed, e.g. <c>datarepo ingest</c>.</param>
    /// <param name="description">What the command does, one line; <c>-h</c> prints it under the usage line, and
    /// docs/cli.md is generated from that output (<c>tools/build_cli_docs.py</c>).</param>
    public sealed class Spec(string prog, string? description = null)
    {
        private readonly List<(string Name, string Arity, string Help)> _positionals = [];
        private readonly List<(string Long, string? Short, Kind Kind, string? Default, string? Metavar, bool Required, string[]? Choices, string Help)> _options = [];

        public string Prog { get; } = prog;
        public string? Description { get; } = description;

        public Spec Positional(string name, string help, string arity = "1")
        {
            _positionals.Add((name, arity, help));
            return this;
        }

        public Spec Option(string longName, Kind kind, string help, string? shortName = null, string? defaultValue = null,
            string? metavar = null, bool required = false, string[]? choices = null)
        {
            _options.Add((longName, shortName, kind, defaultValue, metavar, required, choices, help));
            return this;
        }

        /// <summary>The placeholder an option's value is shown as: its metavar, <c>{a,b}</c> for choices (as
        /// argparse shows them), else the option's name upper-cased.</summary>
        private static string Meta((string Long, string? Short, Kind Kind, string? Default, string? Metavar, bool Required, string[]? Choices, string Help) o) =>
            o.Metavar ?? (o.Choices is not null ? "{" + string.Join(",", o.Choices) + "}" : o.Long.TrimStart('-').Replace('-', '_').ToUpperInvariant());

        public string Usage()
        {
            var parts = new List<string> { $"usage: {Prog} [-h]" };
            foreach (var o in _options)
            {
                var name = o.Short ?? o.Long;
                var text = o.Kind == Kind.Flag ? name : $"{name} {Meta(o)}";
                parts.Add(o.Required ? text : $"[{text}]");
            }
            foreach (var p in _positionals)
                parts.Add(p.Arity switch { "*" => $"[{p.Name} ...]", "+" => $"{p.Name} [{p.Name} ...]", _ => p.Name });
            return string.Join(" ", parts);
        }

        /// <summary>What <c>-h</c> prints: usage, the description, then every argument with its help, a value
        /// option's default, and whether it is required or repeatable.</summary>
        public string Help()
        {
            var lines = new List<string> { Usage(), "" };
            if (!string.IsNullOrEmpty(Description))
            {
                lines.Add(Description);
                lines.Add("");
            }
            if (_positionals.Count > 0)
            {
                lines.Add("positional arguments:");
                foreach (var p in _positionals)
                {
                    var help = p.Help + p.Arity switch { "*" => " (zero or more)", "+" => " (one or more)", _ => "" };
                    lines.Add($"  {p.Name,-22} {help}");
                }
                lines.Add("");
            }
            lines.Add("options:");
            lines.Add($"  {"-h, --help",-22} show this help message and exit");
            foreach (var o in _options)
            {
                var meta = o.Kind == Kind.Flag ? "" : " " + Meta(o);
                var names = (o.Short is null ? "" : o.Short + ", ") + o.Long + meta;
                var help = o.Help;
                if (o.Required) help += " (required)";
                if (o.Kind == Kind.Append) help += help.Contains("repeatable", StringComparison.Ordinal) ? "" : " (repeatable)";
                if (o.Default is not null) help += $" (default: {o.Default})";
                lines.Add(names.Length > 22 ? $"  {names}\n  {"",-22} {help}" : $"  {names,-22} {help}");
            }
            return string.Join("\n", lines);
        }

        /// <exception cref="UsageException">The arguments do not fit this grammar.</exception>
        public Args Parse(IReadOnlyList<string> argv)
        {
            var args = new Args();
            var loose = new List<string>();
            for (var i = 0; i < argv.Count; i++)
            {
                var token = argv[i];
                if (token is "-h" or "--help")
                {
                    Console.WriteLine(Help());
                    Environment.Exit(0);
                }
                if (token == "--")
                {
                    loose.AddRange(argv.Skip(i + 1));
                    break;
                }
                if (token.StartsWith('-') && token.Length > 1 && !double.TryParse(token, out _))
                {
                    var name = token;
                    string? inline = null;
                    var eq = token.IndexOf('=');
                    if (token.StartsWith("--") && eq > 0) (name, inline) = (token[..eq], token[(eq + 1)..]);
                    var option = Find(name);
                    if (option.Kind == Kind.Flag)
                    {
                        if (inline is not null) throw new UsageException(Usage(), $"argument {option.Long}: ignored explicit argument '{inline}'");
                        args._flags.Add(option.Long);
                        continue;
                    }
                    var value = inline ?? (i + 1 < argv.Count ? argv[++i] : throw new UsageException(Usage(), $"argument {Display(option)}: expected one argument"));
                    if (option.Choices is not null && !option.Choices.Contains(value))
                        throw new UsageException(Usage(),
                            $"argument {Display(option)}: invalid choice: '{value}' (choose from {string.Join(", ", option.Choices.Select(c => $"'{c}'"))})");
                    if (option.Kind == Kind.Append)
                    {
                        if (!args._lists.TryGetValue(option.Long, out var list)) args._lists[option.Long] = list = [];
                        list.Add(value);
                    }
                    else args._values[option.Long] = value;
                    continue;
                }
                loose.Add(token);
            }

            foreach (var o in _options)
            {
                if (o.Required && !args._values.ContainsKey(o.Long) && !args._lists.ContainsKey(o.Long))
                    throw new UsageException(Usage(), $"the following arguments are required: {Display(o)}");
                if (o.Kind == Kind.Value && !args._values.ContainsKey(o.Long) && o.Default is not null)
                    args._values[o.Long] = o.Default;
            }

            var queue = new Queue<string>(loose);
            var missing = new List<string>();
            for (var p = 0; p < _positionals.Count; p++)
            {
                var (name, arity, _) = _positionals[p];
                var after = _positionals.Skip(p + 1).Count(x => x.Arity is "1" or "+");
                var take = arity switch
                {
                    "1" => Math.Min(1, queue.Count),
                    _ => Math.Max(0, queue.Count - after),
                };
                var values = new List<string>();
                for (var k = 0; k < take; k++) values.Add(queue.Dequeue());
                if ((arity == "1" && values.Count != 1) || (arity == "+" && values.Count == 0)) missing.Add(name);
                args._positional[name] = values;
            }
            if (missing.Count > 0)
                throw new UsageException(Usage(), $"the following arguments are required: {string.Join(", ", missing)}");
            if (queue.Count > 0)
                throw new UsageException(Usage(), $"unrecognized arguments: {string.Join(" ", queue)}");
            return args;
        }

        private static string Display((string Long, string? Short, Kind Kind, string? Default, string? Metavar, bool Required, string[]? Choices, string Help) o) =>
            o.Short is null ? o.Long : $"{o.Short}/{o.Long}";

        private (string Long, string? Short, Kind Kind, string? Default, string? Metavar, bool Required, string[]? Choices, string Help) Find(string name)
        {
            var exact = _options.Where(o => o.Long == name || o.Short == name).ToList();
            if (exact.Count == 1) return exact[0];
            if (name.StartsWith("--"))
            {
                var prefixed = _options.Where(o => o.Long.StartsWith(name, StringComparison.Ordinal)).ToList();
                if (prefixed.Count == 1) return prefixed[0];
                if (prefixed.Count > 1)
                    throw new UsageException(Usage(), $"ambiguous option: {name} could match {string.Join(", ", prefixed.Select(o => o.Long))}");
            }
            throw new UsageException(Usage(), $"unrecognized arguments: {name}");
        }
    }
}
