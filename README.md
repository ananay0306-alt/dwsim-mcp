# dwsim-mcp

**Talk to a chemical process simulator.** An MCP server that puts
[DWSIM](https://dwsim.org) — the free, open-source process simulator — inside
Claude. Build flowsheets, set stream conditions, run the solver, and read
results, all in plain English:

> *"Model a pump moving 10 kg/s of water from 1 atm to 10 bar at 75%
> efficiency — what's the shaft power?"* → Claude builds it, solves it: **12.0 kW.**

> *"Load my-column.dwxmz, it won't converge, figure out why."* → Claude reads
> the topology, re-solves, reports the solver's error verbatim and what to
> change.

Everything runs locally on your machine. No cloud, no accounts, no cost beyond
your Claude subscription. There is no free simulator with an AI copilot on the
market — commercial ones (Aspen, AVEVA) start at tens of thousands per seat.
This is that, for anyone.

## Requirements

- macOS (Apple Silicon via Rosetta, or Intel)
- [DWSIM](https://dwsim.org) installed at `/Applications/DWSIM.app` (free)
- [Homebrew](https://brew.sh) (used to install mono if you don't have it)
- Claude Desktop or Claude Code

## Install

```bash
git clone https://github.com/ananay0306-alt/dwsim-mcp
cd dwsim-mcp
./setup.sh --register     # builds everything, registers with Claude Desktop
```

Restart Claude Desktop. Done — ask it to build a simulation.

For Claude Code instead:

```bash
claude mcp add --scope user dwsim "$(pwd)/dwsim-mcp.sh"
```

Verify the install (runs a real pump simulation through the server):

```bash
python3 test/run_tests.py
```

**If you are an AI agent** installing or using this: read [AGENTS.md](AGENTS.md).

## What Claude can do with it

26 tools covering the full workflow: compounds and thermodynamic property
packages (29 available — Peng-Robinson, NRTL, Steam Tables, CoolProp, …), all
major unit operations (pumps, compressors, heaters, valves, heat exchangers,
five reactor types, rigorous distillation/absorption columns, recycles),
conversion reactions, solving with **verbatim solver-error reporting**, rich
result inspection, and three escape hatches up to `run_script` — live C#
against the open flowsheet, so nothing in DWSIM's API is out of reach.

Flowsheets save as ordinary `.dwxmz` files that open in the DWSIM GUI, and
GUI-made files load back — the conversation and the app are two views of the
same work.

## How it works

```
Claude ──MCP (stdio)──> dwsim-mcp.sh ──> dwsim-mcp.exe (x64 .NET 8)
                                            └─ loads DWSIM.app's engine
                                               assemblies in-process
```

Notable engineering (details in [PRD.md](PRD.md)):

- **fd surgery in the launcher** — DWSIM's native solvers print banners
  directly to stdout, which would corrupt the MCP protocol stream; the
  launcher moves the protocol to fd 3 and chatter to stderr.
- **Compile arm64, run x64** — DWSIM's natives are x86_64-only; managed IL is
  portable, so the server compiles with mono's csc and runs under Rosetta.
- **A generated deps.json** makes a local `Microsoft.VisualBasic` shim win
  over the framework facade, without which DWSIM's startup type scan crashes.
- **Errors are data** — DWSIM signals non-convergence by *returning* an error
  list; `solve` passes it through verbatim so a failed column can never
  masquerade as converged zeros.

The DWSIM app itself is never modified and does not need to be running.

## Project layout

```
setup.sh        one-command install: prerequisites, workspace, build, verify
dwsim-mcp.sh    launcher used by MCP clients
build.py        compiles src/ against the DWSIM.app assemblies
src/            C# server source (~8 files)
shim/           Microsoft.VisualBasic compatibility shim sources
test/           physics regression tests, run over the real stdio interface
AGENTS.md       canonical guide for AI agents (install + tool usage)
PRD.md          product requirements document
lib/            (generated) assembly workspace — not in git
```

## Limits

- macOS only for now (the architecture ports; the native-library handling is
  the platform-specific part).
- Steady-state simulation; dynamics, optimization sweeps, and costing are on
  the roadmap (see PRD).
- One flowsheet per session; save to `.dwxmz` to persist.
- Tested against DWSIM 9.0.5.

## License

GPL v3 (see [COPYING](COPYING)) — inherited from DWSIM, whose assemblies this
server loads in-process. DWSIM binaries are **not** distributed in this repo;
you install DWSIM separately and the server links against your installation.

Built with [Claude](https://claude.com) — the server was designed, written,
adversarially reviewed, and tested by Claude driving the DWSIM automation API.
