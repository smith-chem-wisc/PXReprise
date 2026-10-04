# PXReprise

**Reprocess public proteomics data at scale, for any biological question.**

Documentation: **<https://smith-chem-wisc.github.io/PXReprise/>**

You describe a biological question in a small text file. PXReprise finds the relevant deposits in
[PRIDE](https://www.ebi.ac.uk/pride/), decides which it can search and how, downloads them, checks every file's
quality, searches them with [MetaMorpheus](https://github.com/smith-chem-wisc/MetaMorpheus) under one versioned
method, and records exactly how every result was made. A new question is a new text file, never a change to the program.

## Have an AI agent set it up

Open your coding agent (Claude Code, Codex, Cursor, ...) in an empty folder and paste:

> Install PXReprise and prove it works, following
> https://raw.githubusercontent.com/smith-chem-wisc/PXReprise/master/AGENTS.md exactly.
> Ask me before accepting any licence or starting anything that runs for more than an hour.

## Or do it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), git, and 100 GB of free disk.
No Python.

```
git clone https://github.com/smith-chem-wisc/PXReprise.git
cd PXReprise
dotnet build -c Release
dotnet publish src/PXReprise -c Release -o <tools>/pxreprise
```

Then download [MetaMorpheus 1.1.11](https://github.com/smith-chem-wisc/MetaMorpheus/releases/tag/1.1.11)
(`MetaMorpheus_CommandLine.zip`), copy `machines/example.toml` to `machines/my-machine.toml`, and fill in its paths.
The full walk-through, including a one-hour test run, is in
[Getting started](https://smith-chem-wisc.github.io/PXReprise/getting-started.html).

## Using it

`pxreprise` is one program; the first word after it says what to do:

```
pxreprise validate question.toml                                  # check a question file
pxreprise census question.toml --queue                            # what would be searched, and why (no downloads)
pxreprise batch run question.toml --machine machines/my-machine.toml   # do it all, deposit by deposit
pxreprise batch status question.toml                              # progress
pxreprise batch stop question.toml                                # finish the current deposit, then stop
```

See [Commands](https://smith-chem-wisc.github.io/PXReprise/commands.html) for the rest.

## How it is organised

| | Answers | Lives in |
|---|---|---|
| **Question** | what is studied: PRIDE keywords, relevance rules, hand decisions | your own folder (`question.toml`) |
| **Profile** | how one kind of data is searched, versioned (`label-free-dda@2`) | `profiles/` |
| **Machine** | where things live on one computer | `machines/my-machine.toml` |

PXReprise is orchestration, policy and provenance. File reading, chemistry and quantification come from
[mzLib](https://github.com/smith-chem-wisc/mzLib); search is MetaMorpheus, run as its own process per dataset.

## Developing

```
dotnet test --filter "Category!=ExternalService&Category!=LocalCorpus"   # offline; required
dotnet test --filter "Category=ExternalService"                         # live PRIDE/UniProt canaries; skip when a service is down
```

.NET 10; mzLib 1.0.593 from NuGet (MetaMorpheus 1.1.11, a separate process, uses 1.0.592). Every command prints one JSON envelope
(`{"ok":true,"data":...}` or `{"ok":false,"error":{...}}`) and exits 0 (ok), 1 (handled failure) or 2 (usage).

## Licence

MIT; see [LICENSE](LICENSE). Reading Thermo `.raw` files requires accepting Thermo's
[RawFileReader licence](https://github.com/thermofisherlsms/RawFileReader), which is the operator's choice
(`accept_thermo_licence` in the machine file).
