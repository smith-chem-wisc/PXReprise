---
title: Home
nav_order: 1
---

# PXReprise

**Reprocess public proteomics data at scale, for any biological question.**

Thousands of mass-spectrometry experiments sit in public archives such as
[PRIDE](https://www.ebi.ac.uk/pride/), each searched once, by its authors, with their own software and settings.
PXReprise re-analyses them the same way, so results from different labs can be compared.

You describe a biological question in a small text file (which keywords to search PRIDE for, which deposits are
relevant). PXReprise does the rest, deposit by deposit:

1. **Finds** every PRIDE deposit that matches your keywords, and decides which are relevant.
2. **Screens** each one: what kind of experiment is it, and can a method it knows search it?
3. **Downloads** the raw files of every deposit it can search, all of them or none.
4. **Checks** every file's quality (right instrument mode, enough spectra).
5. **Searches** them with [MetaMorpheus](https://github.com/smith-chem-wisc/MetaMorpheus), with identical settings every time.
6. **Records** everything: results, a quality report, and exactly how each result was made.

Nothing in the program is specific to one question. Ageing was the first; type 2 diabetes is next. A new question is
a new text file, never a change to the program.

## Two ways to start

| | |
|---|---|
| **Have an AI agent do it** | Give your coding agent (Claude Code, Codex, Cursor, ...) one sentence and it installs, checks and runs everything. [Point your agent here →](agents.md) |
| **Do it yourself** | About 30 minutes of setup, then an hour for a first test run. [Getting started →](getting-started.md) |

## What you need

- A computer with room to work: 100 GB of free disk at least (raw files are deleted after each search).
- [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0). **No Python.**
- [MetaMorpheus 1.1.11](https://github.com/smith-chem-wisc/MetaMorpheus/releases/tag/1.1.11), the command-line zip.
- Windows is tested. Linux and macOS should work but are untested.

## Learn more

- [How it works](concepts.md): questions, profiles and the machine file, in plain words.
- [Writing a question](questions.md): every setting in `question.toml`.
- [Commands](commands.md): what each `pxreprise` command does.
- [Reading the results](results.md): where results land, and what each status means.
