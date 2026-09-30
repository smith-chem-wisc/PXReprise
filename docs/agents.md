---
title: Use an AI agent
nav_order: 2
---

# Have an AI agent install and run PXReprise

PXReprise ships a runbook written for AI coding agents:
[`AGENTS.md`](https://github.com/smith-chem-wisc/PXReprise/blob/master/AGENTS.md). It takes an agent from an empty
machine to a verified installation, then helps you write and run your own question. Each step says what to run, what
success looks like, and what to do when it fails.

## What to say to your agent

Open your agent (Claude Code, OpenAI Codex, Cursor, Gemini CLI, or similar) in an empty folder where you want
PXReprise to live, and paste:

> Install PXReprise and prove it works, following
> https://raw.githubusercontent.com/smith-chem-wisc/PXReprise/master/AGENTS.md exactly.
> Ask me before accepting any licence or starting anything that runs for more than an hour.

That is all. The agent will:

1. Check for .NET 10 and git, and tell you what to install if either is missing.
2. Download and build PXReprise, and run its tests.
3. Download MetaMorpheus.
4. Write a machine file for your computer, asking where the work should go.
5. **Ask you** whether you accept Thermo's licence for reading `.raw` files.
6. Run a one-hour test on a small public deposit, and report the result.

When that works, ask it for the next part:

> Help me write a PXReprise question about &lt;your topic&gt;, run a census, and show me what it would search.

The agent interviews you in plain words: what you study, which terms a paper would use, which organisms and tissues
count, and what looks similar but should not. You never write a regular expression. It turns your answers into rules,
runs a census (a minute or two, no downloads), and shows you samples of what got in, everything that was excluded, and
what no rule caught, each with the text that decided it. You say what is wrong; it fixes the rules or records your
decision with your reason, and runs the census again. When you are happy, it reports how many deposits would be
searched and why the rest would not, and asks before starting the batch itself, which can run for days. The method is
the one in the [tutorial](tutorial.md).

## What the agent will not do

- Change a profile (a versioned scientific method). If none fits your data, it tells you.
- Accept a licence on your behalf.
- Delete results, or hand-edit the batch's state file.

## If you would rather see the steps yourself

The runbook is plain text and readable by people too:
[AGENTS.md](https://github.com/smith-chem-wisc/PXReprise/blob/master/AGENTS.md). The same steps, written for people,
are in [Getting started](getting-started.md).
