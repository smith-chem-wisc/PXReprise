using System.Text.RegularExpressions;
using PXReprise.Cli;
using PXReprise.Discovery;

namespace PXReprise.Config;

public static class ProfileLoader
{
    public static Profile Load(string file)
    {
        var root = TomlSection.Load(file);
        string id = root.RequiredString("id");
        if (!Regex.IsMatch(id, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            throw new ConfigException(file, $"'id' must be lower-case words joined by '-', not '{id}'");
        int version = checked((int)root.RequiredInteger("version"));
        if (version < 1) throw new ConfigException(file, "'version' must be 1 or more");
        var status = ParseEnum<ProfileStatus>(file, "status", root.OptionalString("status") ?? "available");
        string description = root.OptionalString("description") ?? "";

        var acc = root.RequiredTable("accepts");
        var accepts = new ProfileAccepts(
            acc.StringList("acquisition").Select(v => ParseEnum<AcquisitionMode>(file, "accepts.acquisition", v)).ToList(),
            acc.StringList("labelling").Select(v => ParseEnum<Labelling>(file, "accepts.labelling", v)).ToList(),
            acc.StringList("instrument").Select(v => ParseEnum<InstrumentClass>(file, "accepts.instrument", v)).ToList(),
            acc.StringList("file_types"),
            acc.OptionalBool("crosslinking") ?? false,
            acc.OptionalBool("nonspecific_cleavage") ?? false);
        acc.RefuseUnknownKeys();

        var eng = root.RequiredTable("engine");
        string mm = eng.RequiredString("metamorpheus");
        var tasks = eng.StringList("tasks", required: true);
        // "<category>\t<IdWithMotif>" as MetaMorpheus's ListOfModsGptmd holds them; checked against the pinned
        // MetaMorpheus's Mods files when a search is prepared, not here.
        var extraMods = eng.StringList("gptmd_extra_mods");
        // One library per organism: the first search writes it, every later one updates and consumes it (aging D33).
        bool library = eng.OptionalBool("spectral_library") ?? false;
        // Read so published profiles stay valid, but no longer used: a search's limits are the machine's (search_timeout_h,
        // search_stall_minutes), because how long it takes depends on the box and its load, not on the method.
        double timeout = eng.OptionalNumber("timeout_h") ?? 6;
        eng.RefuseUnknownKeys();

        var dbs = new SortedDictionary<string, OrganismDatabase>(StringComparer.Ordinal);
        if (root.OptionalTable("databases") is { } dbTable)
        {
            foreach (string organism in dbTable.Keys.ToList())
            {
                var o = dbTable.RequiredTable(organism);
                string? proteome = o.OptionalString("proteome"), uniprot = o.OptionalString("uniprot");
                if ((proteome is null) == (uniprot is null))
                    throw new ConfigException(file, $"databases.{organism} needs exactly one of 'proteome' (a prepared file) or 'uniprot' (a proteome ID)");
                if (uniprot is not null && !Regex.IsMatch(uniprot, "^UP[0-9]{9}$"))
                    throw new ConfigException(file, $"databases.{organism}.uniprot must be a UniProt proteome ID like UP000005640, not '{uniprot}'");
                dbs[organism] = new OrganismDatabase(proteome, o.StringList("extra"),
                    o.OptionalInteger("taxon") is { } tx ? checked((int)tx) : null, uniprot);
                o.RefuseUnknownKeys();
            }
            dbTable.RefuseUnknownKeys();
        }

        var con = root.RequiredTable("contaminants");
        string panel = con.RequiredString("panel");
        string? exclude = con.OptionalString("exclude");
        con.RefuseUnknownKeys();

        var quant = root.RequiredTable("quant");
        string method = quant.RequiredString("method");
        bool mbr = quant.OptionalBool("mbr") ?? false;
        // "sdrf": ExperimentalDesign.tsv from the question's curated SDRF, else the deposit's (G15). Changes quantification.
        string design = quant.OptionalString("design") ?? "none";
        if (!Profile.Designs.Contains(design))
            throw new ConfigException(file, $"[quant] design must be one of {string.Join(", ", Profile.Designs)}, not '{design}'");
        quant.RefuseUnknownKeys();

        var dep = root.RequiredTable("deposit");
        var deposit = new DepositPolicy(
            dep.OptionalBool("whole") ?? true,
            checked((int)(dep.OptionalInteger("max_files") ?? 60)),
            dep.OptionalString("probe") ?? "probe_spread",
            dep.OptionalInteger("max_est_ms2") ?? 0,
            checked((int)(dep.OptionalInteger("max_file_mb") ?? 5000)),
            checked((int)(dep.OptionalInteger("defer_if_median_file_mb") ?? 0)));
        dep.RefuseUnknownKeys();

        var qc = root.RequiredTable("qc");
        var gates = new QcGates(
            qc.RequiredString("ms2_analyzer"),
            qc.OptionalNumber("min_fraction_orbitrap_hcd") ?? 0,
            checked((int)(qc.OptionalInteger("min_ms2") ?? 0)),
            qc.StringList("excludable"),
            qc.OptionalNumber("max_excluded_frac") ?? 0);
        qc.RefuseUnknownKeys();

        root.RefuseUnknownKeys();
        return new Profile(id, version, status, description, accepts, mm, tasks, extraMods, library, timeout, dbs, panel, exclude,
            method, mbr, deposit, gates, Path.GetDirectoryName(Path.GetFullPath(file))!, design);
    }

    /// <summary>Every <c>*.toml</c> in a directory, keyed by <c>id@version</c>; a duplicate key is refused.</summary>
    public static IReadOnlyDictionary<string, Profile> LoadDirectory(string dir)
    {
        if (!Directory.Exists(dir)) throw new UsageException($"no profiles directory: {dir}");
        var all = new SortedDictionary<string, Profile>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(dir, "*.toml").OrderBy(f => f, StringComparer.Ordinal))
        {
            var p = Load(f);
            if (!all.TryAdd(p.Key, p)) throw new ConfigException(f, $"profile {p.Key} is defined twice");
        }
        return all;
    }

    internal static T ParseEnum<T>(string file, string key, string value) where T : struct, Enum
    {
        foreach (var e in Enum.GetValues<T>())
            if (Snake(e.ToString()) == value) return e;
        throw new ConfigException(file,
            $"'{key}' has '{value}'; allowed: {string.Join(", ", Enum.GetValues<T>().Select(e => Snake(e.ToString())))}");
    }

    internal static string Snake(string pascal) => Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
