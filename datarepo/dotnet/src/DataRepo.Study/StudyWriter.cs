using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;
using YamlDotNet.Core;

namespace DataRepo.Study;

/// <summary>One study layer's delivery: which tables are being handed over, and in which files.</summary>
/// <param name="Path">The manifest file, as given.</param>
/// <param name="StudyManifestVersion">How the manifest is read; only 1 exists.</param>
/// <param name="Layer">Which study layer's schema the rows are written against.</param>
/// <param name="Store">The store the bundle is written to, resolved against the manifest's directory.</param>
/// <param name="Tables">Table name to the delivered file, resolved against the manifest's directory.</param>
/// <param name="Definitions">The delivery's own definition register.</param>
/// <param name="Instance">Who produced it, as written in the YAML (recorded, never read into a row).</param>
/// <param name="Delivery">The producer's label for this hand-over, as written in the YAML.</param>
/// <param name="LayerVersion">The layer version the manifest declares, as text, or null.</param>
/// <param name="Notes">The producer's prose, as written in the YAML.</param>
/// <param name="Raw">The parsed document itself.</param>
public sealed record StudyManifest(
    string Path,
    int StudyManifestVersion,
    string Layer,
    string Store,
    IReadOnlyDictionary<string, string> Tables,
    IReadOnlyList<string> Definitions,
    object? Instance,
    object? Delivery,
    string? LayerVersion,
    object? Notes,
    IReadOnlyDictionary<string, object?> Raw)
{
    /// <summary>The manifest's contribution to the bundle's content hash: <see cref="StudyWriter.StudyContentFields"/> only.</summary>
    public Dictionary<string, object?> ContentDeclaration() => new(StringComparer.Ordinal)
    {
        ["layer"] = Layer,
        ["tables"] = Tables.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => (object?)System.IO.Path.GetFileName(kv.Value), StringComparer.Ordinal),
        ["definitions"] = Definitions.Order(StringComparer.Ordinal).Cast<object?>().ToList(),
        ["study_manifest_version"] = (long)StudyManifestVersion,
    };
}

/// <summary>What writing a study bundle did, in the terms an operator needs.</summary>
/// <param name="BundlePath">The bundle's directory.</param>
/// <param name="BundleId">Its content-addressed id.</param>
/// <param name="Layer">The study layer.</param>
/// <param name="RowCounts">Rows written per table (or recorded, when skipped).</param>
/// <param name="Skipped">True when the bundle was already written and was left alone.</param>
public sealed record StudyResult(string BundlePath, string BundleId, string Layer, IReadOnlyDictionary<string, long> RowCounts, bool Skipped = false);

/// <summary>Write a study bundle: the rows a study layer contributes, delivered separately from a search.</summary>
/// <remarks>
/// <para>Ported from <c>src/datarepo/study.py</c> (named <c>StudyWriter</c> rather than <c>Study</c> so it cannot
/// collide with its namespace). This is DATAREPO-20(a). <c>age_effects</c> is the output of a modelling stage
/// that runs long after a search -- aging's stage 7 -- and it is not in the folder <c>datarepo ingest</c> reads,
/// so an age effect cannot arrive the way a PSM does. It needed its own path, and the path had to satisfy
/// three things at once:</para>
/// <list type="bullet">
/// <item><b>Delivering a model result must never force a re-ingest.</b> A study bundle is a separate,
/// separately content-addressed object. Writing one does not read, touch or re-identify a single search
/// bundle, so a re-fit cannot move the id of a bundle somebody has already cited.</item>
/// <item><b>A study layer adds tables and never alters a core one</b> (U5). The bundle holds only its layer's
/// tables and <c>build</c> unions it in beside the core rather than into it.</item>
/// <item><b>The producer declares what they are handing over</b>, exactly as D9 makes them declare a search.
/// The contract is <c>study.yaml</c>, deliberately the dumbest one that works: a table per file, columns named
/// as the schema names them. dataRepo does not know what shape aging's modelling stage writes internally and
/// is not going to guess -- the one thing it insists on is that the rows land in the schema the definition
/// was transcribed into, with the definition's rules enforced on the way in.</item>
/// </list>
/// <para><b>The default is running ahead of the answer, on purpose (D7).</b> Two things it deliberately does
/// NOT decide, because they are aging's to decide and a check here would freeze a guess into the data:
/// <c>feature_id</c> at meta grain is not resolved against anything (DATAREPO-20(c) asks what a feature's
/// cross-dataset identity even is); and <c>definition_id</c> is resolved against the delivery's OWN declared
/// definitions, not the core <c>definitions</c> table (aging 024 section 2a): their definitions are not
/// produced by a search, and a <c>definition_id</c> that is not among them refuses the write.</para>
/// </remarks>
public static class StudyWriter
{
    /// <summary>The <c>study_manifest_version</c> values this build reads.</summary>
    public static readonly IReadOnlySet<int> SupportedStudyManifestVersions = new HashSet<int> { 1 };

    /// <summary>Reserved-word flags this module COMPUTES, by layer and table: flag column to value column.</summary>
    /// <remarks>The flag is true when the value is an SDRF reserved word, with the core's list and meaning
    /// (<c>sample_characteristics.value_reserved</c>). A curator's <c>not applicable</c> (a cell line has no
    /// tissue) and <c>not available</c> (the sources were read and do not say) are answers, and no row means
    /// "not curated". Refusing the words, as the first draft did, made the second and third look the same: G42
    /// from the curator's side (aging 078, REQ-DATAREPO-6). Computed rather than trusted, so it cannot disagree
    /// with the value.</remarks>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> StudyReservedFlags =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal)
        {
            ["aging"] = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["curated_sample_characteristics"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["value_reserved"] = "value" },
            },
        };

    /// <summary>The version of the STUDY INGEST PATH, and the only version in a study bundle's content hash.</summary>
    /// <remarks>Same rule as the ingester's version and for the same reason: a study bundle id has to mean
    /// "these are the same model results". Bump it in the same commit as any change to what this module reads,
    /// parses, coerces or writes. It is separate from the ingester's because the two paths move independently.
    /// It is Python's value: the C# study path writes the same rows for the same delivery, so it is the same
    /// path, and a study bundle written by either has the same id.</remarks>
    public const string StudyIngesterVersion = "0.6.0";

    /// <summary>The study bundle's manifest file.</summary>
    public const string StudyBundleManifest = "study.json";

    /// <summary>Where study bundles live inside the instance's store: <c>&lt;store&gt;/_study/&lt;layer&gt;/&lt;bundle id&gt;/</c>.</summary>
    /// <remarks>The leading underscore keeps it out of the dataset namespace -- a ProteomeXchange or MassIVE
    /// accession never starts with one -- and <see cref="WriteStudyBundle"/> refuses a layer name that would
    /// collide anyway, rather than trusting the convention to hold forever.</remarks>
    public const string StudyDir = "_study";

    /// <summary>In a delimited file, a list-valued column's cell is split on this. Parquet carries real lists
    /// and is not touched. <c>age_effect_meta.dataset_ids</c> is the column that makes this necessary: C3 and
    /// D1 need <i>which</i> datasets a pooled estimate came from, not how many.</summary>
    public const string ListSeparator = ";";

    /// <summary>Manifest fields that shape what a study bundle CONTAINS (<c>study.STUDY_CONTENT_FIELDS</c>).</summary>
    /// <remarks>"Anything that reaches a written row is an input to the content hash -- and nothing else is".
    /// <c>tables</c> covers both halves of a delivery at once, because each entry is hashed as
    /// <c>table:&lt;name&gt;</c> against the file's own SHA-256. <c>definitions</c> is content, not prose: it
    /// decides whether a row may be written at all. <c>study_manifest_version</c> is in the hash because a
    /// version exists only when the interpretation changed.</remarks>
    public static readonly IReadOnlyList<string> StudyContentFields = ["layer", "tables", "definitions", "study_manifest_version"];

    /// <summary>Fields that do NOT go into the content hash, each with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> StudyNonContentFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["path"] = "where the manifest file happens to sit; a moved manifest delivers the same rows",
        ["store"] = "where the bundle is written, which is an operator's choice and not its content",
        ["instance"] = "who produced it; recorded in study.json, never read into a row",
        ["delivery"] = "the producer's label for this hand-over; prose, like a manifest `reason`",
        ["notes"] = "the producer's prose; never read into a row",
        ["layer_version"] = "checked against STUDY_VERSIONS and recorded, but the layer's version is "
                            + "already hashed from STUDY_VERSIONS rather than from what a manifest claims",
        ["raw"] = "the parsed document itself, which is the container for every field above",
    };

    private static readonly HashSet<string> TrueWords = new(StringComparer.Ordinal) { "true", "1", "yes" };
    private static readonly HashSet<string> FalseWords = new(StringComparer.Ordinal) { "false", "0", "no" };

    /// <summary>A study layer's table spec by name.</summary>
    public static IReadOnlyDictionary<string, TableSpec> LayerTables(string layer) =>
        Bundle.Tables.Study[layer].ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>Read and check a study delivery manifest.</summary>
    /// <exception cref="ManifestException">The file is missing or malformed, declares an unknown
    /// <c>study_manifest_version</c> or an unknown layer, names a table the layer does not have, or points at a
    /// file that is not there.</exception>
    /// <exception cref="InvalidOperationException"><c>definitions</c> is a scalar other than a string (Python
    /// raises TypeError there, not ManifestError; kept so the two agree).</exception>
    public static StudyManifest LoadStudyManifest(string path)
    {
        var shown = BundleWriter.PathText(path);
        if (!File.Exists(path)) throw new ManifestException($"no study manifest at {shown}");
        object? parsed;
        try
        {
            parsed = PyYaml.LoadFile(path);
        }
        catch (YamlException exc)
        {
            throw new ManifestException($"{shown} is not valid YAML: {exc.Message}");
        }
        if (parsed is not Dictionary<string, object?> doc)
            throw new ManifestException($"{shown} must contain a mapping, found {PyFormat.TypeName(parsed)}");

        var version = doc.GetValueOrDefault("study_manifest_version");
        if (!IsSupportedVersion(version))
        {
            var supported = string.Join(", ", SupportedStudyManifestVersions.Order().Select(v => v.ToString(CultureInfo.InvariantCulture)));
            throw new ManifestException($"{shown} declares study_manifest_version {PyFormat.Repr(version)}; this build reads {supported}");
        }

        var layerValue = doc.GetValueOrDefault("layer");
        if (!Truthy(layerValue))
            throw new ManifestException($"{shown} does not say which study 'layer' it delivers");
        var layer = PyFormat.StrAny(layerValue);
        if (!Bundle.Tables.Study.ContainsKey(layer))
        {
            var known = string.Join(", ", Bundle.Tables.Study.Keys.Order(StringComparer.Ordinal));
            if (known.Length == 0) known = "(none)";
            throw new ManifestException(
                $"{shown} delivers study layer '{layer}', which this build does not carry. Layers available: {known}");
        }

        var declared = doc.GetValueOrDefault("layer_version");
        var layerVersion = Bundle.Tables.StudyVersions[layer];
        if (declared is not null && PyFormat.StrAny(declared) != layerVersion)
            throw new ManifestException(
                $"{shown} declares layer_version {PyFormat.Repr(declared)} for '{layer}', and this build carries "
                + $"{layerVersion}. Deliver against the layer this build has, or build with the "
                + "version the rows were written for -- a column added between the two would land "
                + "silently null.");

        if (doc.GetValueOrDefault("store") is null)
            throw new ManifestException($"{shown} is missing 'store'");

        if (doc.GetValueOrDefault("tables") is not Dictionary<string, object?> rawTables || rawTables.Count == 0)
            throw new ManifestException(
                $"{shown} must declare a non-empty 'tables' mapping of <table>: <file>. A delivery that "
                + "names no table is not a delivery.");
        var layerTables = LayerTables(layer);
        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, rel) in rawTables)
        {
            if (!layerTables.ContainsKey(name))
            {
                var known = string.Join(", ", layerTables.Keys.Order(StringComparer.Ordinal));
                throw new ManifestException($"{shown}: study layer '{layer}' has no table '{name}'. Tables: {known}");
            }
            var tablePath = Resolve(rel, path);
            if (!File.Exists(tablePath))
                throw new ManifestException($"{shown}: {name} points at {tablePath}, which is not a file");
            tables[name] = tablePath;
        }

        var rawDefinitions = doc.GetValueOrDefault("definitions");
        IEnumerable<object?> definitionItems = rawDefinitions switch
        {
            _ when !Truthy(rawDefinitions) => [],
            string s => [s],
            IDictionary d => d.Keys.Cast<object?>(),
            IList l => l.Cast<object?>(),
            _ => throw new InvalidOperationException($"'{PyFormat.TypeName(rawDefinitions)}' object is not iterable"),
        };
        var definitions = definitionItems.Select(PyFormat.StrAny).ToList();

        return new StudyManifest(
            Path: path,
            StudyManifestVersion: 1,
            Layer: layer,
            Store: Resolve(doc["store"], path),
            Tables: tables,
            Definitions: definitions,
            Instance: doc.GetValueOrDefault("instance"),
            Delivery: doc.GetValueOrDefault("delivery"),
            LayerVersion: declared is null ? null : PyFormat.StrAny(declared),
            Notes: doc.GetValueOrDefault("notes"),
            Raw: doc);
    }

    /// <summary>Python's <c>version in {1}</c>: set membership is equality, so <c>1.0</c> and <c>true</c> are
    /// members too, exactly as they are in Python.</summary>
    private static bool IsSupportedVersion(object? version) => version switch
    {
        bool b => b && SupportedStudyManifestVersions.Contains(1),
        long l => l is >= int.MinValue and <= int.MaxValue && SupportedStudyManifestVersions.Contains((int)l),
        double d => d == Math.Floor(d) && Math.Abs(d) < int.MaxValue && SupportedStudyManifestVersions.Contains((int)d),
        _ => false,
    };

    /// <summary>Python truthiness for a parsed YAML value.</summary>
    private static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        long l => l != 0,
        double d => d != 0,
        ICollection c => c.Count > 0,
        _ => true,
    };

    /// <summary>A manifest path, with relative ones taken against the manifest's own directory (resolved).</summary>
    /// <remarks>An absolute value is kept as written, as Python's <c>Path</c> keeps it. A rooted but driveless
    /// value on Windows (<c>/x</c>) takes the manifest's drive, as pathlib's join does. Unlike
    /// <c>Path.resolve()</c>, symbolic links and junctions are not followed; the resolved path is only
    /// recorded and shown, never hashed.</remarks>
    private static string Resolve(object? value, string manifestPath)
    {
        var text = PyFormat.StrAny(value);
        if (Path.IsPathFullyQualified(text)) return BundleWriter.PathText(text);
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var combined = Path.IsPathRooted(text)
            ? Path.Combine(Path.GetPathRoot(directory)!, text.TrimStart('/', '\\'))
            : Path.Combine(directory, text);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
    }

    /// <summary>Python's <c>PurePath.suffix</c>: from the last dot, unless the name starts or ends with it.</summary>
    private static string Suffix(string name)
    {
        var i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[i..] : "";
    }

    /// <summary>Turn a delimited file's list columns into real lists, splitting on <see cref="ListSeparator"/>.</summary>
    private static Row SplitLists(Row row, TableSpec spec)
    {
        foreach (var column in spec.Columns)
        {
            if (!column.IsList || !row.TryGetValue(column.Name, out var value) || value is not string cell) continue;
            row[column.Name] = cell.Trim().Split(ListSeparator)
                .Select(part => part.Trim()).Where(part => part.Length > 0).Cast<object?>().ToList();
        }
        return row;
    }

    /// <summary>Read one delivered table file into rows.</summary>
    /// <remarks>Three formats, chosen by extension, and no sniffing: <c>.parquet</c>, <c>.tsv</c> and
    /// <c>.csv</c>. Guessing a delimiter is how a column full of <c>A;B</c> becomes two columns on somebody
    /// else's machine. A text file is read as Python's <c>csv.DictReader</c> reads it (<see cref="PyCsv"/>).</remarks>
    /// <param name="path">The producer's file.</param>
    /// <param name="spec">The study table, used to split list columns in text formats.</param>
    /// <param name="label">What to call the table in an error.</param>
    /// <exception cref="IngestException">The extension is not one of the three, or the file cannot be read.</exception>
    public static List<Row> ReadTableFile(string path, TableSpec spec, string label)
    {
        var name = Path.GetFileName(path);
        var suffix = Suffix(name).ToLowerInvariant();
        if (suffix == ".parquet") return ArrowTables.ReadParquet(path).Rows;
        if (suffix is not (".tsv" or ".csv"))
            throw new IngestException(
                $"{label}: {name} has extension '{(suffix.Length > 0 ? suffix : "(none)")}'. A delivered table is "
                + ".parquet, .tsv or .csv -- the delimiter is taken from the extension rather than "
                + "sniffed, because a guess turns one column of 'A;B' into two.");
        var delimiter = suffix == ".tsv" ? '\t' : ',';
        List<string>? header;
        List<Row> rows;
        try
        {
            header = PyCsv.ReadDicts(path, delimiter, out rows);
        }
        catch (IOException exc)
        {
            throw new IngestException($"{label}: cannot read {path}: {exc.Message}");
        }
        if (header is null) throw new IngestException($"{label}: {name} is empty, so it has no header row");
        return rows.Select(row => SplitLists(row, spec)).ToList();
    }

    /// <summary>Key values that appear more than once, using the layer's declared natural key.</summary>
    /// <remarks>The keys were declared while the tables were empty precisely so this check could exist the
    /// moment something filled them (<see cref="Integrity.StudyCompositeIdentifiers"/>). A key with a null
    /// component is skipped, matching <c>integrity.check</c>: <c>age_effect_refusals</c> keys on
    /// <c>feature_id</c> and a dataset-level refusal has none.</remarks>
    private static List<string> DuplicateKeys(string layer, string table, IReadOnlyList<Row> rows)
    {
        if (!Integrity.StudyCompositeIdentifiers.TryGetValue(layer, out var keys) || !keys.TryGetValue(table, out var key) || key.Length == 0)
            return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var cells = key.Select(column => row.GetValueOrDefault(column)).ToList();
            if (cells.Any(v => v is null || v is "")) continue;
            var value = cells.Select(PyFormat.StrAny).ToArray();
            var identity = string.Join("\0", value);
            if (!seen.Add(identity)) duplicates[identity] = value;
        }
        return duplicates.Values.Order(TupleOrder.Instance).Select(v => string.Join(":", v)).ToList();
    }

    /// <summary>Python's ordering of tuples of strings: element by element.</summary>
    private sealed class TupleOrder : IComparer<string[]>
    {
        public static readonly TupleOrder Instance = new();

        public int Compare(string[]? x, string[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
            {
                var c = string.CompareOrdinal(x[i], y[i]);
                if (c != 0) return c;
            }
            return x.Length.CompareTo(y.Length);
        }
    }

    /// <summary>Set each <see cref="StudyReservedFlags"/> column from its value column, in place.</summary>
    /// <remarks>A delivered flag is allowed, and must agree: one that says a word is not reserved when it is
    /// (or the reverse) is returned as <c>column=value</c> so the write can refuse it. An empty value is left
    /// alone, because a required column refuses it later with a better message.</remarks>
    private static List<string> FlagReserved(string layer, string table, IReadOnlyList<Row> rows)
    {
        var conflicts = new List<string>();
        if (!StudyReservedFlags.TryGetValue(layer, out var byTable) || !byTable.TryGetValue(table, out var flags))
            return conflicts;
        foreach (var (flag, column) in flags)
        {
            foreach (var row in rows)
            {
                if (row.GetValueOrDefault(column) is not string value || value.Trim().Length == 0) continue;
                var computed = Sdrf.NotAvailable.Contains(value.Trim().ToLowerInvariant());
                var original = row.GetValueOrDefault(flag);
                var delivered = original;
                if (delivered is string text)
                {
                    text = text.Trim().ToLowerInvariant();
                    delivered = TrueWords.Contains(text) ? true : FalseWords.Contains(text) ? false : text.Length > 0 ? (object?)text : null;
                }
                if (delivered is not null && !PyEqualsBool(delivered, computed))
                    conflicts.Add($"{flag}={PyFormat.Repr(original)} for {column}={PyFormat.Repr(value.Trim())}");
                row[flag] = computed;
            }
        }
        return conflicts;
    }

    /// <summary>Python's <c>value == flag</c> for a bool: True is 1 and 1.0, False is 0 and 0.0.</summary>
    private static bool PyEqualsBool(object value, bool flag) => value switch
    {
        bool b => b == flag,
        long l => l == (flag ? 1 : 0),
        double d => d == (flag ? 1.0 : 0.0),
        _ => false,
    };

    /// <summary>Definition ids a table's rows carry that the delivery did not declare.</summary>
    /// <remarks>Checked against the delivery's OWN register rather than the core <c>definitions</c> table,
    /// which is aging's correction to our offer (their 024 section 2a). A delivery that declares nothing is not
    /// checked. That is deliberate rather than lax -- a producer who has not adopted the register yet is not
    /// silently handed a stricter contract than the one they agreed to, and a delivery that declares even one
    /// definition opts fully in.</remarks>
    private static HashSet<string> UnknownDefinitions(IReadOnlyList<string> declared, TableSpec spec, IReadOnlyList<Row> rows)
    {
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        if (declared.Count == 0 || spec.Columns.All(c => c.Name != "definition_id")) return unknown;
        var known = new HashSet<string>(declared, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var value = row.GetValueOrDefault("definition_id");
            if (value is null || value is "") continue;
            var text = PyFormat.StrAny(value);
            if (!known.Contains(text)) unknown.Add(text);
        }
        return unknown;
    }

    /// <summary>Content hash of the delivered files, the layer, the schema and the study ingest path.</summary>
    /// <remarks>The layer's version comes from <see cref="Bundle.Tables.StudyVersions"/> rather than from what
    /// the manifest claims, so a bundle written against a layer that has since gained a column cannot share an
    /// id with one written after. The package version is NOT hashed, which is why a C# and a Python study bundle
    /// of the same delivery share an id.</remarks>
    public static string StudyBundleId(StudyManifest manifest, IEnumerable<IReadOnlyDictionary<string, object?>> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(
            $"datarepo-study/{StudyIngesterVersion}\nschema/{Bundle.SchemaContract.Version}\n"
            + $"study/{manifest.Layer}/{Bundle.Tables.StudyVersions[manifest.Layer]}\n"));
        var ordered = sources
            .Select(e => (Role: (string)e["role"]!, Path: (string)e["path"]!, Sha: (string)e["sha256"]!))
            .OrderBy(e => e.Role, StringComparer.Ordinal).ThenBy(e => e.Path, StringComparer.Ordinal);
        foreach (var entry in ordered)
            hash.AppendData(Encoding.UTF8.GetBytes($"{entry.Role}\t{entry.Sha}\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>The sources a delivery is identified by: one per table file, then the manifest's declaration.</summary>
    public static List<IReadOnlyDictionary<string, object?>> Sources(StudyManifest manifest)
    {
        var sources = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var (name, path) in manifest.Tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sources.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = $"table:{name}",
                ["path"] = path,
                ["size_bytes"] = new FileInfo(path).Length,
                ["sha256"] = BundleWriter.Sha256File(path),
            });
        var canonical = PyFormat.Json(manifest.ContentDeclaration(), sortKeys: true, ensureAscii: false);
        sources.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "declaration:study",
            ["path"] = "<declaration:study>",
            ["kind"] = "declaration",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
        });
        return sources;
    }

    /// <summary>Read a delivery and write it as one content-addressed study bundle.</summary>
    /// <remarks>Every table goes through <see cref="ArrowTables.FromRows(string, IReadOnlyList{ColumnSpec}, IReadOnlyList{IReadOnlyDictionary{string, object}})"/>
    /// against the study schema, so the definition's rules are enforced here and not merely documented: a
    /// <c>beta</c> with no <c>se</c> is a write error, and a refused fit has nowhere to put a null <c>beta</c>
    /// because it has a table of its own.</remarks>
    /// <param name="manifest">A loaded <c>study.yaml</c>.</param>
    /// <param name="store">Override the manifest's store.</param>
    /// <param name="overwrite">Rewrite a bundle that is already there. Without it, an unchanged delivery is a
    /// no-op and says so, exactly as <c>ingest</c> does.</param>
    /// <returns>A <see cref="StudyResult"/>; <c>Skipped</c> is true when the bundle was already written.</returns>
    /// <exception cref="IngestException">A delivered file cannot be read, a row has an unknown column or a null
    /// in a required one, or a table repeats its own key.</exception>
    public static StudyResult WriteStudyBundle(StudyManifest manifest, string? store = null, bool overwrite = false)
    {
        var root = string.IsNullOrEmpty(store) ? manifest.Store : BundleWriter.PathText(store);  // Path(store)
        var layer = manifest.Layer;
        if (layer.StartsWith('.') || layer.Contains('/') || layer.Contains('\\'))
            throw new IngestException($"study layer '{layer}' is not usable as a directory name");

        var sources = Sources(manifest);
        var bundleId = StudyBundleId(manifest, sources);
        var output = Path.Combine(root, StudyDir, layer, bundleId);
        var manifestPath = Path.Combine(output, StudyBundleManifest);
        if (File.Exists(manifestPath) && !overwrite)
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            if (existing.RootElement.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Object)
                foreach (var table in tables.EnumerateObject())
                    counts[table.Name] = (long)table.Value.GetDouble();
            return new StudyResult(output, bundleId, layer, counts, Skipped: true);
        }

        var specs = LayerTables(layer);
        var prepared = new List<(string Name, RecordBatch Batch)>();
        foreach (var (name, path) in manifest.Tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var spec = specs[name];
            var label = $"{layer}.{name}";
            var rows = ReadTableFile(path, spec, label);
            var duplicates = DuplicateKeys(layer, name, rows);
            if (duplicates.Count > 0)
            {
                var key = string.Join(", ", Integrity.StudyCompositeIdentifiers[layer][name]);
                var sample = string.Join(", ", duplicates.Take(3));
                throw new IngestException(
                    $"table {label}: {duplicates.Count} duplicate key(s) on ({key}), e.g. {sample}. "
                    + "Two rows for one fit are two different answers to one question, and which is "
                    + "right is the producer's call, not this ingester's.");
            }
            var conflicts = FlagReserved(layer, name, rows);
            if (conflicts.Count > 0)
                throw new IngestException(
                    $"table {label}: {conflicts.Count} delivered reserved-word flag(s) disagree with "
                    + $"the value, e.g. {string.Join(", ", conflicts.Take(3))}. The flag is computed from the SDRF "
                    + "reserved-word list; leave the column empty or correct it.");
            var unknown = UnknownDefinitions(manifest.Definitions, spec, rows);
            if (unknown.Count > 0)
            {
                var declared = string.Join(", ", manifest.Definitions.Order(StringComparer.Ordinal));
                if (declared.Length == 0) declared = "(none declared)";
                throw new IngestException(
                    $"table {label}: {unknown.Count} definition_id(s) the delivery does not declare, "
                    + $"e.g. {string.Join(", ", unknown.Order(StringComparer.Ordinal).Take(3))}. Declared: {declared}. A number whose "
                    + "definition id does not resolve is the failure a definition register exists to "
                    + "prevent, so the write fails rather than the row landing (aging 024 section 2a). "
                    + "Add it to `definitions:` in the study manifest, or correct the column.");
            }
            prepared.Add((name, ToBatch(label, spec, rows)));
        }

        Directory.CreateDirectory(output);
        var rowCounts = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, batch) in prepared)
        {
            ArrowTables.WriteParquet(batch, Path.Combine(output, $"{name}.parquet"));
            rowCounts[name] = batch.Length;
        }

        var doc = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bundle_id"] = bundleId,
            ["kind"] = "study",
            ["layer"] = layer,
            ["layer_version"] = Bundle.Tables.StudyVersions[layer],
            ["schema_version"] = Bundle.SchemaContract.Version,
            ["instance"] = manifest.Instance,
            ["delivery"] = manifest.Delivery,
            // Both versions, for the same reason the search bundle records both: `version` is what an
            // operator installed, `study_ingest_path` is what the id was computed from.
            ["ingester"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "datarepo",
                ["version"] = BundleWriter.PackageVersion,
                ["study_ingest_path"] = StudyIngesterVersion,
            },
            ["written_utc"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
            ["tables"] = rowCounts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["sources"] = sources,
            ["notes"] = manifest.Notes,
        };
        // Python writes it in text mode, so its newlines are the platform's; JSON escapes every newline inside
        // a string, so only the layout's newlines change.
        var text = (PyFormat.JsonIndented(doc) + "\n").Replace("\n", Environment.NewLine);
        File.WriteAllText(manifestPath, text, new UTF8Encoding(false));
        return new StudyResult(output, bundleId, layer, rowCounts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
    }

    /// <summary><c>bundle.rows_to_table</c>, with Python's handling of a text row longer than its header.</summary>
    /// <remarks><c>csv.DictReader</c> puts a long row's extra cells under the key <c>None</c>, and
    /// <c>rows_to_table</c> then refuses that row as having unknown columns <c>[None]</c> -- at that row, after
    /// every earlier row has been checked. So the rows before it are written first, and their refusal (if any)
    /// is the one raised.</remarks>
    private static RecordBatch ToBatch(string label, TableSpec spec, List<Row> rows)
    {
        var first = rows.FindIndex(r => r.ContainsKey(PyCsv.RestKey));
        if (first < 0) return ArrowTables.FromRows(label, spec.Columns, rows);
        ArrowTables.FromRows(label, spec.Columns, rows.Take(first).ToList<IReadOnlyDictionary<string, object?>>());
        var known = spec.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var named = rows[first].Keys.Where(k => k != PyCsv.RestKey && !known.Contains(k)).ToList();
        if (named.Count > 0)
            // Python cannot sort None among strings and raises TypeError here; say so rather than invent a message.
            throw new InvalidOperationException("'<' not supported between instances of 'NoneType' and 'str'");
        throw new IngestException($"table {label}: row {first} has unknown columns [None]");
    }
}
