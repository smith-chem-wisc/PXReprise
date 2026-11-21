#:package mzLib@1.0.598
// Made tests/metamorpheus-output/lfq/lfq_c.mzML (2026-11-21), the third, "thinned" name of the label-free input.
// The same mzML under two names gives no match-between-runs rows (FlashLFQ transfers a peptide only into a file that
// did not identify it), so this copy keeps every scan but replaces every third MS2 spectrum with one 1-intensity peak.
// Peptides identified only from those scans in the other copies are then transferred into this one.
// Scan numbers, retention times and precursor links are unchanged, so nothing else in the file moves.
//
// dotnet run ThinMs2.cs -- <in.mzML> <out.mzML>
using MassSpectrometry;
using MzLibUtil;
using Readers;

if (args.Length != 2) { Console.Error.WriteLine("usage: ThinMs2.cs <in.mzML> <out.mzML>"); return 2; }
var input = args[0];
var output = args[1];

var file = MsDataFileReader.GetDataFile(input);
file.LoadAllStaticData();
var scans = file.GetAllScansList();

int ms2 = 0, blanked = 0;
var outScans = new List<MsDataScan>(scans.Count);
foreach (var s in scans)
{
    if (s.MsnOrder != 2) { outScans.Add(s); continue; }
    ms2++;
    if (ms2 % 3 != 0) { outScans.Add(s); continue; }
    blanked++;
    var one = new MzSpectrum(new[] { s.ScanWindowRange?.Minimum ?? 100.0 }, new[] { 1.0 }, false);
    outScans.Add(new MsDataScan(one, s.OneBasedScanNumber, s.MsnOrder, s.IsCentroid, s.Polarity, s.RetentionTime,
        s.ScanWindowRange, s.ScanFilter, s.MzAnalyzer, 1.0, s.InjectionTime, null, s.NativeId,
        s.SelectedIonMZ, s.SelectedIonChargeStateGuess, s.SelectedIonIntensity, s.IsolationMz, s.IsolationWidth,
        s.DissociationType, s.OneBasedPrecursorScanNumber, s.SelectedIonMonoisotopicGuessMz, s.HcdEnergy,
        s.ScanDescription, s.CompensationVoltage));
}

var thinned = new GenericMsDataFile(outScans.ToArray(), file.GetSourceFile());
MzmlMethods.CreateAndWriteMyMzmlWithCalibratedSpectra(thinned, output, writeIndexed: true);

// Read it back: same scans, the blanked MS2 spectra hold one peak, every other spectrum is unchanged.
var back = MsDataFileReader.GetDataFile(output);
back.LoadAllStaticData();
var backScans = back.GetAllScansList();
if (backScans.Count != scans.Count) { Console.Error.WriteLine($"scan count {backScans.Count} != {scans.Count}"); return 1; }
int onePeak = 0, changed = 0;
for (int i = 0; i < scans.Count; i++)
{
    var a = scans[i]; var b = backScans[i];
    if (a.OneBasedScanNumber != b.OneBasedScanNumber || a.MsnOrder != b.MsnOrder) { Console.Error.WriteLine($"scan {i} differs"); return 1; }
    if (b.MassSpectrum.Size == 1 && a.MassSpectrum.Size > 1) onePeak++;
    else if (a.MassSpectrum.Size != b.MassSpectrum.Size) changed++;
}
Console.WriteLine($"scans {scans.Count}, MS2 {ms2}, blanked {blanked}, read back with one peak {onePeak}, other spectra changed {changed}");
return onePeak == blanked && changed == 0 ? 0 : 1;
