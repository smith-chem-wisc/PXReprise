using PXReprise.Config;
using UsefulProteomicsDatabases;

namespace PXReprise.Discovery;

public enum RelevanceVerdict { Relevant, NotRelevant, Excluded, DecidedInclude, DecidedExclude }

public sealed record RelevanceResult(RelevanceVerdict Verdict, string? Evidence)
{
    public bool IsIn => Verdict is RelevanceVerdict.Relevant or RelevanceVerdict.DecidedInclude;
}

public enum RouteKind { Search, WaitingOnCapability, Held, OutOfScope }

/// <summary>Where a deposit goes: searched under a profile, waiting on a capability no profile has yet, held, or out.</summary>
public sealed record Route(RouteKind Kind, string? Profile, string Reason);

public static class Relevance
{
    public static RelevanceResult Evaluate(PrideProjectSearchResult r, RelevanceRules rules)
    {
        if (rules.Decisions.TryGetValue(r.Accession, out var d))
            return new(d.Verdict == DecisionVerdict.Include ? RelevanceVerdict.DecidedInclude : RelevanceVerdict.DecidedExclude,
                d.Reason);
        string text = AcquisitionClassifier.Text(r);
        var hit = rules.RequireAny.Select(x => x.Match(text)).FirstOrDefault(m => m.Success);
        if (rules.RequireAny.Count > 0 && hit is null) return new(RelevanceVerdict.NotRelevant, null);
        var excl = rules.ExcludeIfAny.Select(x => x.Match(text)).FirstOrDefault(m => m.Success);
        if (excl is not null && !rules.UnlessAny.Any(x => x.IsMatch(text)))
            return new(RelevanceVerdict.Excluded, AcquisitionClassifier.Snippet(text, excl));
        return new(RelevanceVerdict.Relevant, hit is null ? null : AcquisitionClassifier.Snippet(text, hit));
    }
}

public static class Router
{
    /// <summary>
    /// The first of the question's profiles, in the question's order, that accepts the deposit. An accepting profile
    /// that is still <c>pending</c> (e.g. TMT before its quant lands) routes to <see cref="RouteKind.WaitingOnCapability"/>
    /// with that profile named, so the census says what finishing it would unlock.
    /// </summary>
    public static Route Assign(string accession, Acquisition a, RelevanceResult relevance, Question q,
        IReadOnlyDictionary<string, Profile> profiles)
    {
        if (!relevance.IsIn) return new(RouteKind.OutOfScope, null, relevance.Verdict.ToString());
        if (q.Holds.TryGetValue(accession, out string? why)) return new(RouteKind.Held, null, why);

        var refusals = new List<string>();
        foreach (string key in q.Profiles)
        {
            if (!profiles.TryGetValue(key, out var p))
                throw new ConfigException(q.SourceFile, $"profile {key} is not defined (known: {string.Join(", ", profiles.Keys)})");
            string? refusal = p.Accepts.Refusal(a);
            if (refusal is null)
                return p.Status == ProfileStatus.Available
                    ? new(RouteKind.Search, p.Key, "accepted")
                    : new(RouteKind.WaitingOnCapability, p.Key, $"{p.Key} is pending");
            refusals.Add(refusal);
        }
        return new(RouteKind.WaitingOnCapability, null, Capability(a, refusals));
    }

    /// <summary>What the deposit would need, named by the first thing no allowed profile covers.</summary>
    private static string Capability(Acquisition a, IReadOnlyList<string> refusals)
    {
        if (a.Crosslinked) return "crosslinking";
        if (a.Mode == AcquisitionMode.Dia) return "dia";
        if (a.Instrument == InstrumentClass.Timstof) return "timstof";
        if (a.Labelling == Labelling.Mixed) return "mixed_labelling: needs a hand decision";
        if (a.Labelling == Labelling.Metabolic) return "metabolic_labelling";
        if (a.Labelling == Labelling.Isobaric) return "isobaric";
        if (a.Labelling == Labelling.O18) return "o18_labelling";
        return refusals.Count > 0 ? refusals[0] : "no profile allowed";
    }
}
