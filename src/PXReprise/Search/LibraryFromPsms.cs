using System.Globalization;
using System.Text;
using Chemistry;
using Omics.SpectrumMatch;
using Readers;
using LibraryReader = Readers.SpectralLibrary.SpectralLibrary;

namespace PXReprise.Search;

/// <summary>
/// The library a search of low-resolution MS2 adds (decision 0092), built by PXReprise from the search's PSM table,
/// because MetaMorpheus cannot write or update one there: either switch makes it match fragments at every charge
/// (<c>ClassicSearchEngine</c>, <c>matchAllCharges: WriteSpectralLibrary</c>), which at ±0.5 Da scores decoys as targets.
/// The search itself only reads the chain.
///
/// It follows MetaMorpheus 1.1.15's own update (<c>PostSearchAnalysisTask.UpdateSpectralLibrary</c>):
/// <list type="bullet">
/// <item>the PSMs the search's filter passes at 1%, targets only, no contaminants, with one full sequence and one mass
/// (<c>FilteredPsms.Filter(..., includeDecoys: false, includeContaminants: false, includeAmbiguous: false,
/// includeAmbiguousMods: false)</c>);</item>
/// <item>the best-scoring of them per full sequence and precursor charge;</item>
/// <item>a parent spectrum of the same name is kept when it holds more matched fragments than that PSM's score, rounded
/// down, and replaced otherwise; every other parent spectrum is kept, and every other best PSM is added.</item>
/// </list>
/// Each spectrum is made as MetaMorpheus makes one (<c>SpectralMatch.ToLibrarySpectrum</c>): the full sequence, the
/// theoretical precursor m/z at the PSM's charge, the matched fragments with their measured intensities, and the scan's
/// retention time. <see cref="LibrarySpectrum.ToString"/> writes them scaled to the most intense, as MetaMorpheus's files
/// are. (mzLib's <c>PsmFromTsv.ToLibrarySpectrum</c> differs on both: the measured precursor m/z, and intensities over
/// their sum.) The table is read line by line: an ion-trap deposit's can exceed 7 GB.
/// </summary>
public static class LibraryFromPsms
{
    /// <summary>What a build read and wrote.</summary>
    public sealed record Result(int PsmsPassing, int Precursors, int ParentKept, int Replaced, int Added, string Path)
    {
        public int Spectra => ParentKept + Replaced + Added;
    }

    /// <summary>The PSM table's column for the filter a results.txt names (<see cref="MetaMorpheusResults.FilterNamed"/>):
    /// "pep q-value", "q-value notch", or "q-value" (every MetaMorpheus before 1.1.15).</summary>
    public static string FilterColumnNamed(string? named) => named switch
    {
        "pep q-value" => SpectrumMatchFromTsvHeader.PEP_QValue,
        "q-value notch" => SpectrumMatchFromTsvHeader.QValueNotch,
        "q-value" or null => SpectrumMatchFromTsvHeader.QValue,
        _ => throw new SearchSetupException($"results.txt names the filter '{named}', which has no PSM-table column"),
    };

    /// <summary>The PSM table's column for each identification filter MetaMorpheus names (D82).</summary>
    public static string FilterColumn(string identificationFilter) => identificationFilter switch
    {
        "QValue" => SpectrumMatchFromTsvHeader.QValue,
        "QValueNotch" => SpectrumMatchFromTsvHeader.QValueNotch,
        "PepQValue" => SpectrumMatchFromTsvHeader.PEP_QValue,
        _ => throw new SearchSetupException($"no PSM-table column for the identification filter '{identificationFilter}'"),
    };

    /// <summary>
    /// Writes <paramref name="outputPath"/>: <paramref name="parentLibrary"/> (null for a chain's first version) updated
    /// with the PSMs of <paramref name="psmTable"/> that pass <paramref name="filterColumn"/> at
    /// <paramref name="threshold"/>.
    /// </summary>
    public static Result Write(string psmTable, string filterColumn, string? parentLibrary, string outputPath, double threshold = 0.01)
    {
        var best = BestPerPrecursor(psmTable, filterColumn, threshold, out int passing);
        int precursors = best.Count;
        var spectra = new List<LibrarySpectrum>();
        int kept = 0, replaced = 0;
        if (parentLibrary is not null)
        {
            using var parent = new LibraryReader(parentLibrary);
            {
                foreach (var og in parent.GetAllLibrarySpectra())
                {
                    if (best.Remove((og.Sequence, og.ChargeState), out var psm))
                    {
                        if (og.MatchedFragmentIons.Count > Math.Truncate(psm.Score)) { spectra.Add(og); kept++; }
                        else { spectra.Add(ToLibrarySpectrum(psm)); replaced++; }
                    }
                    else { spectra.Add(og); kept++; }
                }
            }
        }
        int added = 0;
        foreach (var psm in best.Values) { spectra.Add(ToLibrarySpectrum(psm)); added++; }

        string tmp = outputPath + ".partial";
        using (var o = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            foreach (var s in spectra) o.WriteLine(s.ToString());
        File.Move(tmp, outputPath, overwrite: true);
        return new Result(passing, precursors, kept, replaced, added, outputPath);
    }

    private static LibrarySpectrum ToLibrarySpectrum(PsmFromTsv p) =>
        new(p.FullSequence, p.MonoisotopicMass.ToMz(p.PrecursorCharge), p.PrecursorCharge, p.MatchedIons, p.RetentionTime, isDecoy: false);

    /// <summary>The best-scoring passing PSM per (full sequence, precursor charge), parsed only once it is the best.</summary>
    internal static Dictionary<(string, int), PsmFromTsv> BestPerPrecursor(string psmTable, string filterColumn, double threshold, out int passing)
    {
        using var r = new StreamReader(psmTable);
        string header = r.ReadLine() ?? throw new SearchSetupException($"{psmTable} is empty");
        var parsed = SpectrumMatchTsvReader.ParseHeader(header);
        var names = header.Split('\t');
        int Col(string name)
        {
            int i = Array.IndexOf(names, name);
            if (i < 0) throw new SearchSetupException($"{psmTable} has no '{name}' column, so no library can be built from it");
            return i;
        }
        int dct = Col(SpectrumMatchFromTsvHeader.DecoyContaminantTarget), fq = Col(filterColumn), full = Col(SpectrumMatchFromTsvHeader.FullSequence),
            bas = Col(SpectrumMatchFromTsvHeader.BaseSequence), z = Col(SpectrumMatchFromTsvHeader.PrecursorCharge),
            sc = Col(SpectrumMatchFromTsvHeader.Score), mass = Col(SpectrumMatchFromTsvHeader.MonoisotopicMass);
        int last = new[] { dct, fq, full, bas, z, sc, mass }.Max();
        var lines = new Dictionary<(string, int), (double Score, string Line)>();
        passing = 0;
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            var c = line.Split('\t');
            if (c.Length <= last || c[dct] != "T") continue;
            if (!double.TryParse(c[fq], NumberStyles.Float, CultureInfo.InvariantCulture, out double q) || q > threshold) continue;
            if (c[full].Contains('|') || c[bas].Contains('|') || c[mass].Contains('|') || c[full].Length == 0) continue;
            // MetaMorpheus 1.1.13 wrote the charge as "4.00000"
            if (!double.TryParse(c[z], NumberStyles.Float, CultureInfo.InvariantCulture, out double zd) || zd < 1) continue;
            int charge = (int)Math.Round(zd);
            if (!double.TryParse(c[sc], NumberStyles.Float, CultureInfo.InvariantCulture, out double score)) continue;
            passing++;
            var key = (c[full], charge);
            if (!lines.TryGetValue(key, out var have) || score > have.Score) lines[key] = (score, line);
        }
        var split = new[] { '\t' };
        return lines.ToDictionary(kv => kv.Key, kv => new PsmFromTsv(kv.Value.Line, split, parsed));
    }
}
