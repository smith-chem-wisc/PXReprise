using PXReprise.Config;

namespace PXReprise.Search;

/// <summary>
/// G40: a modification no UniMod entry describes (NBF, aging 072 PXR-A69) reaches MetaMorpheus through the profile's own
/// modification file. MetaMorpheus reads every file in its settings folder's <c>Mods</c> directory at start
/// (GlobalVariables.LoadModifications, 1.1.12), so the file is installed there, under its own name, before a search that
/// uses it. A file of that name with other contents is refused, never replaced: searches already ran against it, and a
/// changed list ships under a new name. A modification MetaMorpheus still did not know would fail the search on its
/// "Unrecognized mod" warning.
/// </summary>
public static class CustomModificationFile
{
    /// <summary>
    /// Installs <paramref name="p"/>'s custom modification file into <paramref name="settingsDir"/>'s <c>Mods</c> folder,
    /// calling <paramref name="seed"/> first when MetaMorpheus has not created that settings folder yet, and returns the
    /// installed path. <paramref name="needed"/> are the <c>Type\tIdWithMotif</c> ids the search uses.
    /// </summary>
    public static string Install(Profile p, string settingsDir, IReadOnlyCollection<string> needed, Action seed)
    {
        if (p.CustomModifications is not { } name || p.ThiolReagents is not { } modelled)
            throw new SearchSetupException($"the deposit's chemistry needs {Describe(needed)}, and {p.Key} ships no custom modifications");
        // The chemistry is cached per deposit; read under another profile, it may name a modification this one lacks.
        var missing = needed.Where(id => !modelled.Values.Contains(id)).ToList();
        if (missing.Count > 0)
            throw new SearchSetupException($"{p.Key} does not model {Describe(missing)}: the deposit's chemistry was read under another profile");

        if (!Directory.Exists(settingsDir)) seed();
        string mods = Path.Combine(settingsDir, "Mods");
        if (!Directory.Exists(mods))
            throw new SearchSetupException($"MetaMorpheus's settings folder {settingsDir} has no Mods folder");
        string target = Path.Combine(mods, name);
        byte[] want = File.ReadAllBytes(p.ListPath(name));
        if (!File.Exists(target))
        {
            // Written beside Mods, then moved in: MetaMorpheus reads every file in Mods, so a half-written one never sits there.
            string staging = Path.Combine(settingsDir, $".{name}.{Guid.NewGuid():N}.partial");
            File.WriteAllBytes(staging, want);
            try { File.Move(staging, target); }
            catch (IOException) when (File.Exists(target)) { File.Delete(staging); }   // another search installed it first
        }
        if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(want))
            throw new SearchSetupException($"{target} differs from {p.ListPath(name)}: a changed modification list must ship under a new file name, never replace one that searches used");
        return target;
    }

    private static string Describe(IEnumerable<string> ids) => string.Join(", ", ids.Select(i => i.Replace('\t', ' ')));
}
