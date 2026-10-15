namespace PXReprise.Discovery;

/// <summary>
/// D34 (the user: "you have to tell the person a call needs to be made. don't just walk away"): every park that needs a
/// person's decision is written to the run's <c>decisions_needed.tsv</c>, one row per deposit and park, saying what must
/// be decided, on what evidence, and the choices. The session that runs the batch reads it, tells the question's owner in
/// a thread message, and shows it in the session brief until it is answered. The answer comes back as question data
/// (decisions.tsv, [designs], [exceptions]), never as an engine change. A park that only waits on a capability we have
/// not built (a profile, a label engine) is not a decision and is counted in the census instead.
/// </summary>
public static class DecisionsNeeded
{
    public const string FileName = "decisions_needed.tsv";

    private const string Header = "utc\taccession\tpark\tquestion\tevidence\tchoices";

    public sealed record Decision(string Question, string Choices);

    private static readonly Dictionary<string, Decision> ByPark = new(StringComparer.Ordinal)
    {
        ["waiting_unknown_protease"] = new("Which protease made these peptides? The one the deposit names is not in mzLib's protease list.",
            "name the protease in a curated SDRF; or exclude the deposit"),
        ["waiting_unknown_modification"] = new("Which cysteine modification should the search use? The deposit names one MetaMorpheus cannot resolve.",
            "name a modelled modification in a curated SDRF; or exclude the deposit"),
        ["waiting_unknown_alkylant"] = new("How were the cysteines blocked? The protocol names a thiol reagent with no modelled modification (e.g. NBF-Cl).",
            "name a modelled alkylant in a curated SDRF; search with no cysteine modification (state it); or exclude the deposit"),
        ["waiting_multi_protease"] = new("Which protease digested each file? The deposit used several and the file names do not say which.",
            "assign a protease per file in a curated SDRF; or exclude the deposit"),
        ["deferred_mixed_labelling"] = new("The spectra show isobaric reporter ions in some files and not others. Which files are searched, and how?",
            "exclude one group of files ([exceptions]); split into one search per group (waits on dataRepo R8); or exclude the deposit"),
        ["deferred_needs_design_selection"] = new("The deposit is larger than a search takes whole. Which files make the experiment?",
            "select files by name ([exceptions] include/exclude) or lift the cap for it ([exceptions]); or exclude the deposit"),
        ["waiting_species"] = new("The probe runs' own spectra say another species than the deposit's record (D35).",
            "record each run's species in a curated SDRF; approve and search that species; or exclude the deposit"),
        ["waiting_other_organism"] = new("The deposit's PRIDE record names an organism outside the question beside the one it is queued as: runs of another species, a host and its pathogen, or a mislabel.",
            "say which runs to search ([exceptions] include_files / exclude_files) or what all its runs are ([exceptions] species); or exclude the deposit"),
        ["waiting_mixed_species"] = new("A probe run holds several species and none dominates: a mixed sample (xenograft, infection, spike-in) or a mislabel.",
            "say what the sample is in a curated SDRF (mixed samples wait for sdrf's plan, sdrf 008); or exclude the deposit"),
        ["waiting_alkylation_check"] = new("Carbamidomethyl was expected, but the probe runs have almost no cysteine peptides: the cysteines were blocked some other way, or not at all.",
            "name the reagent in a curated SDRF; search with no fixed cysteine modification; or exclude the deposit"),
        ["waiting_no_identifications"] = new("The probe search identified (almost) nothing: the peptides are not what the search looks for (a label, chemistry, species or acquisition the text does not state).",
            "say what the deposit is (a curated SDRF naming its label or chemistry, or a profile that takes it); or exclude the deposit"),
        ["unsupported_fragmentation"] = new("Some files' MS2 fragmentation cannot be searched per file (an unsupported type, no header, or ion-trap and Orbitrap MS2 mixed).",
            "exclude those files ([exceptions]); or exclude the deposit until a profile supports them"),
    };

    /// <summary>The decision a park needs, or null when the park only waits on a capability.</summary>
    public static Decision? For(string park) => ByPark.GetValueOrDefault(park);

    /// <summary>The parks that are announced; anything else is not.</summary>
    public static IReadOnlyCollection<string> AnnouncedParks => ByPark.Keys;

    /// <summary>
    /// Appends the row for <paramref name="park"/> unless the same deposit and park are already listed (a deposit is
    /// announced once per park, not once per pass). Returns the decision, or null when nothing needed announcing.
    /// </summary>
    public static Decision? Announce(string runRoot, string accession, string park, string? evidence)
    {
        if (For(park) is not { } d) return null;
        Directory.CreateDirectory(runRoot);
        string path = Path.Combine(runRoot, FileName);
        if (File.Exists(path) && File.ReadLines(path).Skip(1).Any(l => l.Split('\t') is { Length: > 2 } c && c[1] == accession && c[2] == park))
            return d;
        if (!File.Exists(path)) File.WriteAllText(path, Header + "\n");
        File.AppendAllText(path, string.Join('\t', DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), accession, park,
            Clean(d.Question), Clean(evidence), Clean(d.Choices)) + "\n");
        return d;
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
