using System.Collections.Concurrent;
using Omics.Modifications;
using Proteomics;
using UsefulProteomicsDatabases;

namespace DataRepo.Ingest.Sources;

/// <summary>Sequence-level peptide specificity (G76, D40), from mzLib's <see cref="PeptideUniquenessClassifier"/>.</summary>
/// <remarks>
/// <para>Until G76, <c>peptidoforms.is_unique</c> was parsimony-unique (one accession in MetaMorpheus's
/// parsimony list), so a peptide in P02751 and three of its isoforms read <c>true</c> (aging 075). Now both
/// columns come from the SEARCHED sequences: a peptide belongs to a protein when the protein's sequence
/// contains it anywhere, whatever the protease; I and L are one residue; decoys are ignored; contaminants
/// count, because they are real sequences in the search space. That is mzLib's rule, not ours.</para>
/// <para>D40 maps mzLib's four classes onto the two columns (the user, 2026-10-04):</para>
/// <list type="table">
/// <item><term>Unique</term><description>is_unique true, is_isoform_specific true</description></item>
/// <item><term>SharedWithinGene</term><description>true, false: it supports the gene, not one isoform</description></item>
/// <item><term>SharedAcrossGenes</term><description>false, false</description></item>
/// <item><term>NotInDatabase</term><description>null, null: no searched sequence holds it, so neither is known</description></item>
/// </list>
/// <para>Databases are loaded exactly as pyMzLib's <c>proteins classify-peptides</c> loads them (bridge v0.4.0
/// <c>Proteins.Load</c>): no decoys generated, no sequence variants applied, each file's contaminant flag
/// passed to the loader.</para>
/// </remarks>
public static class Specificity
{
    /// <summary>Loaded databases by (path, sha256), so datasets that share a database load it once per process.</summary>
    private static readonly ConcurrentDictionary<(string Path, string Sha256, bool Contaminant), List<Protein>> Cache = new();

    /// <summary>The two columns for one sharing class (D40).</summary>
    public static (bool? IsUnique, bool? IsIsoformSpecific) Columns(PeptideSharing sharing) => sharing switch
    {
        PeptideSharing.Unique => (true, true),
        PeptideSharing.SharedWithinGene => (true, false),
        PeptideSharing.SharedAcrossGenes => (false, false),
        _ => (null, null),
    };

    /// <summary>The proteins of every searched database, loaded as the bridge loads them for classification.</summary>
    /// <param name="databases">(path, sha256, contaminant) for each searched database file.</param>
    public static List<Protein> LoadProteins(IEnumerable<(string Path, string Sha256, bool Contaminant)> databases)
    {
        var proteins = new List<Protein>();
        foreach (var db in databases)
            proteins.AddRange(Cache.GetOrAdd(db, key => Load(key.Path, key.Contaminant)));
        return proteins;
    }

    private static List<Protein> Load(string path, bool contaminant)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".xml") || lower.EndsWith(".xml.gz"))
            // maxHeterozygousVariants: 0 applies no sequence variant, so each entry is one protein under its own
            // accession, as the bridge reads it.
            return ProteinDbLoader.LoadProteinXML(
                path, generateTargets: true, DecoyType.None, Array.Empty<Modification>(), contaminant,
                modTypesToExclude: null, out _, maxThreads: 1, maxHeterozygousVariants: 0);
        return ProteinDbLoader.LoadProteinFasta(path, generateTargets: true, DecoyType.None, contaminant, out _, maxThreads: 1);
    }

    /// <summary>Classifies each distinct base sequence; a sequence mzLib cannot take (empty, or not A-Z) is absent
    /// from the result, and its columns stay null.</summary>
    public static Dictionary<string, PeptideSharing> Classify(IEnumerable<string> baseSequences, List<Protein> proteins)
    {
        var valid = baseSequences.Where(s => s.Length > 0 && s.All(c => c is >= 'A' and <= 'Z'))
            .Distinct(StringComparer.Ordinal).ToList();
        var results = PeptideUniquenessClassifier.Classify(valid, proteins);
        return results.ToDictionary(r => r.Peptide, r => r.Sharing, StringComparer.Ordinal);
    }
}
