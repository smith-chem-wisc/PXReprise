using System.Text.Json.Nodes;
using PXReprise.Discovery;

namespace PXReprise.Tests;

/// <summary>
/// D30/D32: a deposit's experiment record, every fact with its source and evidence, written for machines and people.
/// D34: a park that needs a person is announced once, in the run's decisions_needed.tsv, with the choices.
/// </summary>
public class ExperimentRecordTests
{
    private static ExperimentRecord Sample()
    {
        var xr = new ExperimentRecord("PXD000001");
        xr.Species("human", "queue");
        xr.Fact("alkylation", "Carbamidomethyl", ChemistrySource.ProtocolText, "alkylated with | chloroacetamide", guessed: true);
        xr.Probe(new JsonObject
        {
            ["a.raw"] = new JsonObject
            {
                ["pass"] = true, ["ms2_analyzer_dissociation"] = new JsonObject { ["Orbitrap/HCD"] = 100 },
                ["fragmentation"] = new JsonObject { ["dissociation_type"] = "HCD" },
            },
            ["b.raw"] = new JsonObject
            {
                ["pass"] = false, ["ms2_analyzer_dissociation"] = new JsonObject { ["Orbitrap/UVPD"] = 100 },
                ["fragmentation"] = new JsonObject { ["dissociation_type"] = null, ["unsupported"] = "Orbitrap/UVPD in 100 of 100" },
            },
        });
        return xr;
    }

    [Test]
    public void TheRecordKeepsEveryFactWithItsSourceAndEachRun()
    {
        string dir = TestSupport.TempDir();
        Sample().Write(dir, "probed", "to be searched", null);
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ExperimentRecord.Folder, ExperimentRecord.JsonFile)))!;
        Assert.Multiple(() =>
        {
            Assert.That(j["schema"]!.GetValue<string>(), Is.EqualTo("pxreprise-experiment/1"));
            Assert.That(j["facts"]!["species"]!["source"]!.GetValue<string>(), Is.EqualTo("queue"));
            Assert.That(j["facts"]!["alkylation"]!["guessed"]!.GetValue<bool>(), Is.True);
            Assert.That(j["runs"]!["b.raw"]!["fragmentation"]!["unsupported"]!.GetValue<string>(), Does.Contain("UVPD"));
            Assert.That(j["decision_needed"], Is.Null);
        });
    }

    [Test]
    public void TheNotesMarkGuessesAndRefusalsAndKeepTablesIntact()
    {
        string notes = Sample().Notes("waiting_unknown_alkylant", "NBF-Cl", DecisionsNeeded.For("waiting_unknown_alkylant"));
        Assert.Multiple(() =>
        {
            Assert.That(notes, Does.Contain("Carbamidomethyl (GUESS)"));
            Assert.That(notes, Does.Contain("alkylated with \\| chloroacetamide"), "a pipe in evidence never breaks the table");
            Assert.That(notes, Does.Contain("| b.raw |").And.Contain("refused: Orbitrap/UVPD"));
            Assert.That(notes, Does.Contain("## A person must decide").And.Contain("NBF-Cl"));
        });
    }

    [Test]
    public void APersonsDecisionIsAnnouncedOnceAndACapabilityWaitIsNot()
    {
        string root = TestSupport.TempDir();
        Assert.That(DecisionsNeeded.Announce(root, "PXD000001", "waiting_dia", "DIA"), Is.Null);
        Assert.That(File.Exists(Path.Combine(root, DecisionsNeeded.FileName)), Is.False, "a capability wait writes nothing");

        Assert.That(DecisionsNeeded.Announce(root, "PXD000001", "waiting_unknown_alkylant", "NBF-Cl\tin a lysis\nbuffer"), Is.Not.Null);
        DecisionsNeeded.Announce(root, "PXD000001", "waiting_unknown_alkylant", "again");
        DecisionsNeeded.Announce(root, "PXD000002", "deferred_mixed_labelling", "TMT in 20 of 32");
        var lines = File.ReadAllLines(Path.Combine(root, DecisionsNeeded.FileName));
        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo("utc\taccession\tpark\tquestion\tevidence\tchoices"));
            Assert.That(lines, Has.Length.EqualTo(3), "one row per deposit and park");
            Assert.That(lines.Skip(1).Select(l => l.Split('\t').Length), Has.All.EqualTo(6), "tabs and newlines in evidence are flattened");
            Assert.That(lines[1].Split('\t')[4], Is.EqualTo("NBF-Cl in a lysis buffer"));
        });
    }

    [Test]
    public void EveryChemistryParkIsAnnounced()
    {
        foreach (string park in new[] { "waiting_unknown_protease", "waiting_unknown_modification", "waiting_unknown_alkylant", "waiting_multi_protease" })
            Assert.That(DecisionsNeeded.For(park), Is.Not.Null, park);
    }
}
