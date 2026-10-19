using System.Text.RegularExpressions;

namespace PXReprise.Search;

/// <summary>
/// What a profile's <c>[engine] metamorpheus</c> names (v0.4.26). A release (<c>"1.1.12"</c>) is checked against the
/// banner of <c>CMD -g</c>, as before. A commit pin (<c>"master@3a6a76632"</c>: a label, <c>@</c>, then 7 to 40 hex
/// digits of the commit) runs a build of one exact commit that is no release, such as the command-line zip MetaMorpheus's
/// CI builds on every push to master (the user, 2026-10-19: "we are being held back too far by the slow release cycles").
/// Such a build's banner says "Not a release version.", so the commit in its <c>--help</c> text (<c>CMD 1.0.0+&lt;sha&gt;</c>)
/// is checked instead. The value itself names the build wherever a release did: the machine's <c>[metamorpheus]</c> key,
/// the settings folder, the spectral library registry and provenance.
/// </summary>
public sealed record MetaMorpheusPin(string Name, string? Commit)
{
    private static readonly Regex ReleasePattern = new(@"^\d+(\.\d+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new(@"^[A-Za-z0-9._-]+@(?<sha>[0-9a-f]{7,40})$", RegexOptions.CultureInvariant);

    public bool IsRelease => Commit is null;

    public static bool TryParse(string value, out MetaMorpheusPin? pin)
    {
        pin = ReleasePattern.IsMatch(value) ? new MetaMorpheusPin(value, null)
            : CommitPattern.Match(value) is { Success: true } m ? new MetaMorpheusPin(value, m.Groups["sha"].Value)
            : null;
        return pin is not null;
    }

    public static MetaMorpheusPin Parse(string value) =>
        TryParse(value, out var pin) ? pin! : throw new ArgumentException(
            $"'{value}' names no MetaMorpheus build: a release (\"1.1.12\") or a commit pin (\"master@3a6a76632\")", nameof(value));

    /// <summary>
    /// Throws unless the build is the one pinned: a release by its banner, a commit pin by the commit its help text
    /// carries (<paramref name="commit"/> is "unknown" when the help text names none).
    /// </summary>
    public void Check(string banner, string commit, string profileKey)
    {
        if (IsRelease)
        {
            if (banner != Name) throw new SearchSetupException($"MetaMorpheus is {banner}, {profileKey} expects {Name}");
        }
        else if (!commit.StartsWith(Commit!, StringComparison.Ordinal))
            throw new SearchSetupException($"MetaMorpheus is built from commit {commit}, {profileKey} expects {Name}");
    }
}
