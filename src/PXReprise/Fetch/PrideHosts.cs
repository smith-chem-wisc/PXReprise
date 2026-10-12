using UsefulProteomicsDatabases;
using CvParam = MzLibUtil.CvParam;

namespace PXReprise.Fetch;

/// <summary>Which host serves PRIDE's raw files over HTTPS (machine key <c>pride_https</c>).</summary>
public enum PrideHttps
{
    /// <summary>EMBL-EBI's Globus collection over plain HTTPS: anonymous, no Globus software, the same paths.</summary>
    Globus,
    /// <summary><c>ftp.pride.ebi.ac.uk</c>, mzLib's own choice.</summary>
    Ftp,
}

/// <summary>
/// The Globus HTTPS route (pride 014, PXR-W4). EBI's helpdesk answered our damaged-download report (ticket #893983) with
/// "try Globus". In pride's trial on 2026-10-12, the same five PXD058855 files fetched side by side came back damaged
/// 2 of 5 times from <c>ftp.pride.ebi.ac.uk</c> and 0 of 5 from the Globus host (0 of 15 over all its runs). mzLib
/// 1.0.594 uses an explicit <c>https://</c> location exactly as given (since #1093), so the route is a copy of the file
/// whose only location is the Globus URL. The Globus host sends no ETag or Last-Modified, so a dropped download there
/// restarts from byte 0.
/// </summary>
public static class PrideHosts
{
    public const string FtpPrefix = "ftp://ftp.pride.ebi.ac.uk/pride/data/archive/";
    /// <summary>"EMBL-EBI Public Data" (collection 47772002-3e5b-4fd3-b97c-18cee38d6df2), PRIDE's archive under it.</summary>
    public const string GlobusBase = "https://g-a8b222.dd271.03c0.data.globus.org/pride/data/archive/";

    /// <summary>The machine file's value: <c>globus</c> (the default) or <c>ftp</c>.</summary>
    public static PrideHttps Parse(string? value) => (value ?? "globus").ToLowerInvariant() switch
    {
        "globus" => PrideHttps.Globus,
        "ftp" => PrideHttps.Ftp,
        _ => throw new ArgumentException($"pride_https must be \"globus\" or \"ftp\", not \"{value}\""),
    };

    /// <summary>The file with the Globus URL as its only location, or null when PRIDE lists no FTP path to map.</summary>
    public static PrideArchiveFile? ViaGlobus(PrideArchiveFile f)
    {
        string? ftp = f.PublicFileLocations?.Select(l => l?.Value)
            .FirstOrDefault(v => v is not null && v.StartsWith(FtpPrefix, StringComparison.OrdinalIgnoreCase));
        if (ftp is null) return null;
        return new PrideArchiveFile
        {
            FileName = f.FileName, FileSizeBytes = f.FileSizeBytes, Checksum = f.Checksum, FileCategory = f.FileCategory,
            PublicFileLocations = new() { new CvParam { Name = "Globus HTTPS", Value = GlobusBase + ftp[FtpPrefix.Length..] } },
        };
    }
}
