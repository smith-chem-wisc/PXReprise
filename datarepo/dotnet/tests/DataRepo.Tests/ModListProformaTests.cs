using System.Text.Json;
using DataRepo.Ingest;

namespace DataRepo.Tests;

/// <summary>Parity of <c>ModList.cs</c> / <c>Proforma.cs</c> with <c>modlist.py</c> / <c>proforma.py</c> (datarepo 0.32.0).</summary>
/// <remarks>Every expected value under <c>Fixtures/modlist-proforma</c> was produced by running the Python modules;
/// see that folder's <c>PROVENANCE.md</c>. Nothing here states what the C# was written to do.</remarks>
public class ModListProformaTests
{
    private static readonly string Fixtures =
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "modlist-proforma");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "pyproject.toml")) && Directory.Exists(Path.Combine(dir.FullName, "tests", "data")))
                return dir.FullName;
        throw new DirectoryNotFoundException("repository root (pyproject.toml beside tests/data) not found");
    }

    private static JsonElement Load(string path) =>
        JsonDocument.Parse(File.ReadAllBytes(path)).RootElement;

    private static JsonElement Fixture(string name) => Load(Path.Combine(Fixtures, name));

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

    private static long? Long(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt64();

    /// <summary>A float as the generator wrote it: a JSON number, or Python's repr for a non-finite one.</summary>
    private static double? Dbl(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => e.GetString() switch
        {
            "nan" => double.NaN,
            "inf" => double.PositiveInfinity,
            "-inf" => double.NegativeInfinity,
            var s => throw new FormatException(s),
        },
        _ => e.GetDouble(),
    };

    private static bool SameDouble(double? a, double? b) =>
        a is null ? b is null
        : b is not null && (double.IsNaN(a.Value) ? double.IsNaN(b.Value)
            : BitConverter.DoubleToInt64Bits(a.Value) == BitConverter.DoubleToInt64Bits(b.Value));

    private static ModEntry EntryFromJson(JsonElement e) => new(
        e.GetProperty("name").GetString()!,
        e.GetProperty("targets").EnumerateArray().Select(t => t.GetString()!).ToHashSet(StringComparer.Ordinal),
        Long(e.GetProperty("unimod")),
        Dbl(e.GetProperty("mass")),
        e.GetProperty("source").GetString()!);

    private static List<string> EntryDiffs(IReadOnlyList<ModEntry> actual, JsonElement expected, string label)
    {
        var diffs = new List<string>();
        var want = expected.EnumerateArray().ToList();
        if (actual.Count != want.Count) diffs.Add($"{label}: {actual.Count} entries, Python {want.Count}");
        for (var i = 0; i < Math.Min(actual.Count, want.Count); i++)
        {
            var a = actual[i];
            var w = want[i];
            var targets = a.Targets.OrderBy(t => t, StringComparer.Ordinal).ToList();
            var wantTargets = w.GetProperty("targets").EnumerateArray().Select(t => t.GetString()!).ToList();
            if (a.Name != w.GetProperty("name").GetString()
                || !targets.SequenceEqual(wantTargets)
                || a.Unimod != Long(w.GetProperty("unimod"))
                || !SameDouble(a.MonoisotopicMass, Dbl(w.GetProperty("mass")))
                || a.Source != w.GetProperty("source").GetString()
                || a.UnimodCurie != Str(w.GetProperty("unimod_curie")))
                diffs.Add($"{label}[{i}]: C# {a.Name}|{string.Join(",", targets)}|{a.Unimod}|{a.MonoisotopicMass}|{a.Source} "
                          + $"Python {w.GetRawText()}");
        }
        return diffs;
    }

    private static void AssertRegistry(ModRegistry registry, JsonElement expected, string label)
    {
        var diffs = EntryDiffs(registry.Entries.ToList(), expected.GetProperty("entries"), label);
        Assert.Multiple(() =>
        {
            Assert.That(diffs, Is.Empty, string.Join("\n", diffs.Take(20)));
            Assert.That(registry.Count, Is.EqualTo(expected.GetProperty("count").GetInt32()), label + " count");
            Assert.That(registry.Sources, Is.EqualTo(expected.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ToList()),
                label + " sources");
        });
    }

    private static ModRegistry RegistryFromFixture(JsonElement expected) =>
        new(expected.GetProperty("entries").EnumerateArray().Select(EntryFromJson));

    private static int CompareLookups(ModRegistry registry, JsonElement lookups, string label)
    {
        var entries = registry.Entries.ToList();
        var diffs = new List<string>();
        var n = 0;
        foreach (var q in lookups.EnumerateArray())
        {
            var name = q[0].GetString()!;
            var residue = Str(q[1]);
            int? want = q[2].ValueKind == JsonValueKind.Null ? null : q[2].GetInt32();
            var hit = registry.Lookup(name, residue);
            int? got = hit is null ? null : entries.FindIndex(e => ReferenceEquals(e, hit));
            if (got != want) diffs.Add($"{label} lookup({JsonSerializer.Serialize(name)}, {JsonSerializer.Serialize(residue)}): C# {got}, Python {want}");
            n++;
        }
        Assert.That(diffs, Is.Empty, string.Join("\n", diffs.Take(30)));
        return n;
    }

    private static int CompareParses(ModRegistry registry, JsonElement cases, string label)
    {
        var diffs = new List<string>();
        var n = 0;
        foreach (var c in cases.EnumerateArray())
        {
            var input = c.GetProperty("input").GetString()!;
            var got = Proforma.Parse(input, registry);
            var problems = new List<string>();
            if (got.Proforma != c.GetProperty("proforma").GetString()) problems.Add($"proforma {got.Proforma}");
            if (got.BaseSequence != c.GetProperty("base_sequence").GetString()) problems.Add($"base {got.BaseSequence}");
            if (got.IsModified != c.GetProperty("is_modified").GetBoolean()) problems.Add("is_modified");
            var wantUnresolved = c.GetProperty("unresolved").EnumerateArray().Select(u => u.GetString()!).ToList();
            if (!got.Unresolved.SequenceEqual(wantUnresolved)) problems.Add($"unresolved {string.Join("|", got.Unresolved)}");
            var wantMods = c.GetProperty("mods").EnumerateArray().ToList();
            if (wantMods.Count != got.Mods.Count) problems.Add($"{got.Mods.Count} mods");
            else
                for (var i = 0; i < wantMods.Count; i++)
                {
                    var w = wantMods[i];
                    var m = got.Mods[i];
                    if (m.Position != w[0].GetInt64() || m.Residue != w[1].GetString() || m.Name != w[2].GetString()
                        || m.Category != w[3].GetString() || m.Unimod != Str(w[4]) || !SameDouble(m.Mass, Dbl(w[5]))
                        || m.Resolved != w[6].GetBoolean())
                        problems.Add($"mod {i}: {m}");
                }
            if (problems.Count > 0)
                diffs.Add($"{label} {JsonSerializer.Serialize(input)}: {string.Join("; ", problems)} -- Python {c.GetRawText()}");
            n++;
        }
        Assert.That(diffs, Is.Empty, $"{diffs.Count} of {n} differ:\n" + string.Join("\n", diffs.Take(20)));
        return n;
    }

    private static void CompareCache(ModRegistry registry, JsonElement expected)
    {
        var cache = new ProformaCache(registry);
        foreach (var s in expected.GetProperty("inputs").EnumerateArray())
            cache.Get(s.GetString()!);
        var want = expected.GetProperty("unresolved").EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(cache.Unresolved.Select(kv => (kv.Key, kv.Value)).ToList(), Is.EqualTo(want), "unresolved, in order");
            Assert.That(cache.Size, Is.EqualTo(expected.GetProperty("size").GetInt32()), "size");
        });
    }

    // ---------------------------------------------------------------- registries

    [Test]
    public void RegistryFromMetaMorpheus111MatchesPython()
    {
        // A copy of F:/aging_data/mm_settings/1.1.11's Mods/*.txt and Data/ptmlist.txt.
        AssertRegistry(ModRegistry.FromMetaMorpheus(Path.Combine(Fixtures, "mm_1.1.11")), Fixture("registry_1.1.11.json"), "1.1.11");
    }

    [Test]
    public void RegistryFromRepoFixtureTreeMatchesPython()
    {
        var install = Path.Combine(RepoRoot(), "tests", "data", "work_root", "mm_settings", "1.1.11");
        AssertRegistry(ModRegistry.FromMetaMorpheus(install), Fixture("registry_fixture.json"), "fixture");
    }

    [Test]
    public void RegistryFromMissingInstallIsEmpty()
    {
        var registry = ModRegistry.FromMetaMorpheus(Path.Combine(Fixtures, "no-such-install"));
        AssertRegistry(registry, Fixture("registry_missing.json"), "missing");
        Assert.That(registry.Count, Is.Zero);
    }

    [TestCase("1.1.9")]
    [TestCase("1.1.10")]
    [TestCase("1.1.11")]
    public void RegistryFromAgingInstallMatchesPython(string version)
    {
        var install = Path.Combine("F:", "aging_data", "mm_settings", version);
        if (!Directory.Exists(install)) Assert.Ignore($"{install} is not on this machine");
        AssertRegistry(ModRegistry.FromMetaMorpheus(install), Fixture($"registry_{version}.json"), version);
    }

    [TestCase("1.1.9")]
    [TestCase("1.1.10")]
    [TestCase("1.1.11")]
    [TestCase("fixture")]
    [TestCase("missing")]
    public void LookupsMatchPython(string label)
    {
        var expected = Fixture($"registry_{label}.json");
        var n = CompareLookups(RegistryFromFixture(expected), expected.GetProperty("lookups"), label);
        Assert.That(n, Is.GreaterThan(0));
    }

    [Test]
    public void LookupsOnRegistryReadFromFilesMatchPython()
    {
        var expected = Fixture("registry_1.1.11.json");
        CompareLookups(ModRegistry.FromMetaMorpheus(Path.Combine(Fixtures, "mm_1.1.11")), expected.GetProperty("lookups"), "1.1.11 files");
    }

    [Test]
    public void ModEntryEqualityIsByValue()
    {
        var a = new ModEntry("X", new HashSet<string> { "K", "N" }, 1, 2.5, "a.txt");
        var b = new ModEntry("X", new HashSet<string> { "N", "K" }, 1, 2.5, "a.txt");
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a, Is.Not.EqualTo(b with { Unimod = null }));
        });
    }

    // ---------------------------------------------------------------- the synthetic flat file

    [Test]
    public void SyntheticFlatFileMatchesPython()
    {
        // float(), str.strip/rstrip/splitlines, _targets, IGNORECASE `DR` lines, ptmlist-style blocks and the
        // `+.6f` mass tag, each against Python's own output for one hand-made file.
        var expected = Fixture("synthetic.json");
        var entries = ModList.ParseEntries(expected.GetProperty("text").GetString()!, "synthetic.txt").ToList();
        var diffs = EntryDiffs(entries, expected.GetProperty("entries"), "synthetic");
        Assert.That(diffs, Is.Empty, string.Join("\n", diffs.Take(30)));

        var registry = new ModRegistry(entries);
        var grouped = registry.Entries.ToList();
        var order = expected.GetProperty("grouped").EnumerateArray().Select(g => g.GetInt32()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(registry.Count, Is.EqualTo(expected.GetProperty("count").GetInt32()));
            Assert.That(registry.Sources, Is.EqualTo(new[] { "synthetic.txt" }));
            for (var i = 0; i < entries.Count; i++)
                Assert.That(grouped[order[i]], Is.SameAs(entries[i]), $"grouped order of entry {i}");
        });
        CompareParses(registry, expected.GetProperty("cases"), "synthetic");
        CompareLookups(registry, expected.GetProperty("lookups"), "synthetic");
    }

    [Test]
    public void PythonCaseAndAlphaMatchForEveryCodePoint()
    {
        // str.upper / lower / casefold / isalpha of every single code point, from Python (pycase.json).
        var doc = Fixture("pycase.json");
        var diffs = new List<string>();
        var changed = new HashSet<int>();
        foreach (var row in doc.GetProperty("case").EnumerateArray())
        {
            var cp = row[0].GetInt32();
            changed.Add(cp);
            var s = char.ConvertFromUtf32(cp);
            if (ModList.PyUpper(s) != row[1].GetString()) diffs.Add($"upper U+{cp:X4}: C# {Hex(ModList.PyUpper(s))} Python {Hex(row[1].GetString()!)}");
            if (ModList.PyLower(s) != row[2].GetString()) diffs.Add($"lower U+{cp:X4}: C# {Hex(ModList.PyLower(s))} Python {Hex(row[2].GetString()!)}");
            if (ModList.PyCaseFold(s) != row[3].GetString()) diffs.Add($"casefold U+{cp:X4}: C# {Hex(ModList.PyCaseFold(s))} Python {Hex(row[3].GetString()!)}");
        }
        var alpha = new HashSet<int>();
        foreach (var r in doc.GetProperty("alpha_ranges").EnumerateArray())
            for (var cp = r[0].GetInt32(); cp <= r[1].GetInt32(); cp++) alpha.Add(cp);
        for (var cp = 0; cp < 0x110000; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF) continue;
            var s = char.ConvertFromUtf32(cp);
            if (!changed.Contains(cp))
            {
                if (ModList.PyUpper(s) != s) diffs.Add($"upper U+{cp:X4}: C# {Hex(ModList.PyUpper(s))} Python unchanged");
                if (ModList.PyLower(s) != s) diffs.Add($"lower U+{cp:X4}: C# {Hex(ModList.PyLower(s))} Python unchanged");
                if (ModList.PyCaseFold(s) != s) diffs.Add($"casefold U+{cp:X4}: C# {Hex(ModList.PyCaseFold(s))} Python unchanged");
            }
            if (ModList.PyIsAlpha(s) != alpha.Contains(cp)) diffs.Add($"isalpha U+{cp:X4}: C# {ModList.PyIsAlpha(s)}");
        }
        Assert.That(diffs, Is.Empty, $"{diffs.Count} differ:\n" + string.Join("\n", diffs));
    }

    private static string Hex(string s) => string.Join(" ", s.EnumerateRunes().Select(r => r.Value.ToString("X4")));

    // ---------------------------------------------------------------- MOD_TOKEN and parse

    [Test]
    public void ModTokenMatchesPython()
    {
        var diffs = new List<string>();
        var n = 0;
        foreach (var t in Fixture("mod_token.json").EnumerateArray())
        {
            var token = t[0].GetString()!;
            var m = ModList.ModToken.Match(token);
            var got = m.Success
                ? $"{m.Groups["category"].Value}\u0001{m.Groups["name"].Value}\u0001{m.Groups["residue"].Value}"
                : null;
            var want = t[1].ValueKind == JsonValueKind.Null ? null : $"{t[1].GetString()}\u0001{t[2].GetString()}\u0001{t[3].GetString()}";
            if (got != want) diffs.Add($"{JsonSerializer.Serialize(token)}: C# {got}, Python {want}");
            n++;
        }
        Assert.That(diffs, Is.Empty, string.Join("\n", diffs.Take(20)));
        Assert.That(n, Is.GreaterThan(2000));
    }

    [TestCase("1.1.11")]
    [TestCase("fixture")]
    [TestCase("missing")]
    public void ParseMatchesPython(string label)
    {
        // 1.1.11: real Full Sequence cells from four aging datasets plus every registry entry placed on a
        // residue, the N terminus and the C terminus, plus hand-made edge cases.
        var registry = RegistryFromFixture(Fixture($"registry_{label}.json"));
        var n = CompareParses(registry, Fixture($"parse_{label}.json").GetProperty("cases"), label);
        Assert.That(n, Is.GreaterThan(0));
    }

    [Test]
    public void ParseWithRegistryReadFromFilesMatchesPython()
    {
        var registry = ModRegistry.FromMetaMorpheus(Path.Combine(Fixtures, "mm_1.1.11"));
        CompareParses(registry, Fixture("parse_1.1.11.json").GetProperty("cases"), "1.1.11 files");
    }

    [Test]
    public void ProformaCacheMatchesPythonOnFixtureDataset()
    {
        CompareCache(RegistryFromFixture(Fixture("registry_fixture.json")), Fixture("cache_fixture.json"));
    }

    [Test]
    public void ProformaCacheMatchesPythonOnRealSequences()
    {
        CompareCache(RegistryFromFixture(Fixture("registry_1.1.11.json")), Fixture("cache_1.1.11.json"));
    }

    [Test]
    public void ProformaCacheReturnsTheSameObject()
    {
        var cache = new ProformaCache(new ModRegistry());
        var first = cache.Get("PEP[Foo:Bar on K]");
        Assert.Multiple(() =>
        {
            Assert.That(cache.Get("PEP[Foo:Bar on K]"), Is.SameAs(first));
            Assert.That(cache.Size, Is.EqualTo(1));
            Assert.That(Proforma.Parse("PEP[Foo:Bar on K]", new ModRegistry()), Is.EqualTo(first), "value equality");
        });
    }

    /// <summary>The full comparison (48,930 real sequences, and the 1.1.9/1.1.10 synthetic sets), too large to
    /// commit. Set <c>DATAREPO_MODLIST_FULL</c> to the generator's <c>full/</c> folder to run it.</summary>
    [Test]
    public void FullComparisonWhenAvailable()
    {
        var dir = Environment.GetEnvironmentVariable("DATAREPO_MODLIST_FULL");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Ignore("DATAREPO_MODLIST_FULL is not set");
        var r111 = RegistryFromFixture(Fixture("registry_1.1.11.json"));
        var n = CompareParses(r111, Load(Path.Combine(dir, "parse_1.1.11_full.json")).GetProperty("cases"), "1.1.11 full");
        foreach (var version in new[] { "1.1.9", "1.1.10" })
            n += CompareParses(RegistryFromFixture(Fixture($"registry_{version}.json")),
                Load(Path.Combine(dir, $"parse_{version}.json")).GetProperty("cases"), version);
        CompareCache(r111, Load(Path.Combine(dir, "cache_1.1.11_full.json")));
        TestContext.Out.WriteLine($"{n} full sequences compared");
    }
}
