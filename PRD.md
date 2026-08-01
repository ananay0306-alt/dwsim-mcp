# dwsim-mcp — Product Requirements Document

**An MCP server that puts the DWSIM chemical process simulator inside Claude.**

| | |
|---|---|
| Status | v1 build in progress |
| Target platform | macOS (Apple Silicon via Rosetta), DWSIM 9.0.5 |
| License | GPL v3 (inherited from DWSIM; server links DWSIM assemblies in-process) |
| Prior art | OntoLedgy `ol_dwsim_interop_services` (Windows-only, flash-calc scope, AGPL) |

---

## 1. Problem

DWSIM is the only serious free process simulator, but driving it programmatically on
macOS requires the bespoke toolchain built in this project (compile C# per-script,
run on an x64 .NET 8 runtime, one cold process per question). A chemical engineer —
or Claude acting for one — cannot iterate: every "change the reflux and re-solve"
pays a full engine cold-start and a file round-trip, and only works when a developer
drives the shell.

Commercial AI copilots for process simulation exist (Aspen, AVEVA integrations) but
all sit on top of paid simulators. DWSIM's own AI Assistant is in the paid,
Windows-only Patreon edition. There is no free, open, macOS-capable equivalent.

## 2. Goals

1. **Conversational simulation.** A user in Claude Desktop (or Claude Code) builds,
   modifies, solves, and interrogates DWSIM flowsheets in plain English.
2. **Stateful iteration.** The flowsheet lives in the server process. A parameter
   change + re-solve touches one property — seconds, not a rebuild.
3. **Honest solver reporting.** DWSIM signals convergence failure by *returning an
   error list*, not throwing. `solve` must surface those errors verbatim. A
   converged-looking result full of zeros is the worst possible outcome.
4. **No ceiling.** Named tools cover the common 90%; reflection-based escape
   hatches (`set_object_property`, `call_object_method`, `run_script`) reach the
   rest of DWSIM's surface without a server release.
5. **GUI round-trip.** Anything built here saves to `.dwxmz` and opens in the
   DWSIM app; anything hand-edited there loads back.

## 3. Non-goals (v1)

- Driving or mirroring the live DWSIM GUI window (file handoff is the boundary).
- Dynamics/integrator runs, optimization sweeps, costing, GHG tooling (v2).
- Windows/Linux support (architecture permits it later; macOS proven first).
- Bundling DWSIM binaries (user installs DWSIM.app; server links against it —
  keeps GPL distribution obligations minimal and avoids version drift).
- Multi-flowsheet concurrency. One active flowsheet per server session.

## 4. Users

- **Primary:** chemical engineers / students with Claude Desktop and free DWSIM,
  no Aspen budget.
- **Secondary:** Claude Code sessions doing engineering automation (this project).

## 5. Architecture

```
Claude Desktop ── MCP (JSON-RPC over stdio, ndjson) ──> launcher (zsh)
                                                          │  fd juggling:
                                                          │  3=real stdout (protocol)
                                                          │  1→2 (DWSIM/native chatter→stderr)
                                                          └─> x64 .NET 8 runtime (Rosetta)
                                                                └─> dwsim-mcp.exe
                                                                      ├─ JsonRpc loop (stdin → fd3)
                                                                      ├─ ToolRegistry (schemas + handlers)
                                                                      └─ Engine session
                                                                            └─ Automation3 → FlowsheetBase
                                                                                  └─ /Applications/DWSIM.app assemblies
                                                                                       + native x86_64 solvers
```

### 5.1 The stdout problem (critical)

MCP stdio requires stdout to carry *only* protocol JSON. DWSIM prints to Console
("Estimated NRTL IP set…") and its native solvers (IPOPT banner) write directly to
fd 1, bypassing .NET entirely. **Console redirection is not sufficient.**

Solution — the launcher performs fd surgery before exec:

```zsh
exec 3>&1 1>&2          # fd3 := protocol channel; fd1 := stderr
exec dotnet dwsim-mcp.exe
```

The server writes protocol frames to fd 3 (`FileStream` over `SafeFileHandle(3)`),
reads stdin normally, and lets all engine/native chatter fall through fd 1 → stderr,
which MCP clients capture as logs. If fd 3 is absent (manual run), fall back to
real stdout and push `Console.Out` to stderr.

### 5.2 Runtime constraints (inherited, already proven)

1. **Compile arm64, run x64.** Sources compile with Homebrew mono's `csc` (IL is
   portable); execution uses `~/.dotnet-x64` .NET 8 under Rosetta because DWSIM's
   natives are x86_64-only.
2. **Generated `deps.json` is mandatory** so the local higher-versioned
   `Microsoft.VisualBasic` shim (10.9.0.0) beats the framework facade — otherwise
   `FlowsheetBase.Initialize()`'s `GetTypes()` scan throws on VB `My` types.
3. **Native aliases replace Mono DllMap.** .NET Core ignores `.dll.config`;
   `Ipopt39.dylib → libipopt.dylib` etc. live as symlinks in the lib workspace.
4. Assembly workspace = symlinks into `DWSIM.app/Contents/MonoBundle` + 6 local
   overrides. The app bundle is never modified.
5. Engine init (~seconds, ChEDL DB load) is **lazy** — deferred to the first tool
   call that needs it, so `initialize`/`tools/list` respond instantly and client
   startup never times out.

### 5.3 Protocol

- MCP stdio transport, newline-delimited JSON-RPC 2.0.
- `initialize` → echo client `protocolVersion` (fallback `2025-06-18`), capabilities
  `{tools:{}}`, serverInfo `{name:"dwsim-mcp", version}`.
- `notifications/initialized` accepted; notifications never get responses.
- `tools/list` → static registry with JSON Schema `inputSchema` per tool. Tool
  descriptions carry domain hints (energy-port convention, solver names, units)
  because the model plans from them.
- `tools/call` → `{content:[{type:"text", text:<JSON payload>}], isError?}`.
  Handler exceptions → `isError:true` with message + stack head; protocol-level
  errors only for malformed requests.
- `ping` → `{}`.

## 6. Tool surface (v1)

Units: SI throughout (K, Pa, kg/s, mol/s, kW). Every tool that takes an object
names it by its flowsheet **tag**.

### Session
| Tool | Purpose |
|---|---|
| `create_flowsheet` | New empty flowsheet (replaces current). |
| `load_flowsheet` | Load `.dwxmz`/`.dwxml` from path (replaces current). |
| `save_flowsheet` | Save current to path (compressed iff `.dwxmz`). |
| `describe_flowsheet` | Compounds, property packages, reactions, object list + connection map, calc status. The model's orientation call. |

### Basis
| Tool | Purpose |
|---|---|
| `search_compounds` | Search available compound DB by substring (returns matches, capped). |
| `add_compound` | Add named compound. |
| `list_property_packages` | Available PP names. |
| `set_property_package` | Instantiate + attach a PP by name. |
| `create_reaction` | v1: conversion reactions — stoichiometry dict, base compound, phase, conversion expression; auto-adds to DefaultSet. |

### Topology
| Tool | Purpose |
|---|---|
| `list_object_types` | The supported `add_object` type names (from ObjectType map). |
| `add_object` | Add unit op / stream by type name + tag (+ optional x,y). |
| `connect_objects` | `ConnectObjects(from, to, fromPort, toPort)` by tags. Description documents the energy-port-1 convention. |
| `disconnect_objects` | Sever a connection. |
| `remove_object` | Delete an object by tag. |
| `auto_layout` | Tidy graphic layout before save. |

### Conditions
| Tool | Purpose |
|---|---|
| `set_stream` | T, P, mass/molar flow, composition (molar-flow dict or mole-fraction dict + total). |
| `configure_column` | Column-specific composite: stages, feed stage, condenser/reboiler specs, top pressure, pressure drop, solver method, max iterations. |
| `set_object_property` | Reflection setter: any public writable property by name, with enum-by-name, nullable, and numeric coercion. First escape hatch. |
| `call_object_method` | Reflection invoker: any public method by name with JSON args (e.g. `ConnectFeed(stream, 10)`). Second escape hatch. |

### Execution & inspection
| Tool | Purpose |
|---|---|
| `solve` | `CalculateFlowsheet2` → returns solver **error list verbatim** + per-object calculated flags. Never a bare boolean. |
| `get_stream` | T, P, flows, enthalpy, vapor fraction, overall + per-phase composition. |
| `get_object` | Reflection dump of an object's readable scalar/enum/string properties (grouped, capped). |
| `get_results` | Compact whole-flowsheet results table: all material streams + energy streams + key unit-op numbers. |

### Escape hatch (third tier)
| Tool | Purpose |
|---|---|
| `run_script` | Compile a C# snippet (`public static object Run(IFlowsheet sim)`) against the live flowsheet via mono `csc`, load, invoke, return result. Covers anything reflection can't express (lambdas, loops, multi-step logic). |

## 7. Error-handling philosophy

- Solver non-convergence is **data, not an exception**: `solve` returns
  `{errors:[...verbatim...], calculated:{tag:bool}, success:bool}` where `success`
  means "solver ran", not "converged".
- Tool misuse (unknown tag, bad enum, missing compound) returns `isError:true`
  with an actionable message including near-miss suggestions where cheap
  (e.g. list valid tags on unknown-tag).
- Engine chatter goes to stderr + a rotating log file under `logs/`; never stdout.
- The server never dies on a handler exception; the JSON-RPC loop is wrapped.

## 8. Build & delivery plan

1. **Research** — parallel source-reading agents extract exact API signatures
   (fan-out, done first; no guessed member names in the server).
2. **Implement** — C# sources in `dwsim-mcp/src/`: `Program.cs` (fd wiring, loop),
   `JsonRpc.cs`, `McpServer.cs` (registry/dispatch), `Engine.cs` (session +
   Automation3), `Tools*.cs` (handlers), `Reflect.cs` (coercion helpers).
3. **Build script** — `build.py` (multi-file evolution of the proven `prepare.py`):
   csc @rsp against the assembly workspace → `lib/dwsim-mcp.exe`
   + generated `deps.json`/`runtimeconfig.json`.
4. **Launcher** — `dwsim-mcp/dwsim-mcp.sh` doing the fd redirection + exec.
5. **Test** — ndjson piped scenarios (no client needed): initialize/list handshake;
   pump case end-to-end vs. known 12.02 kW; ester reactor+column vs. known
   98.7% recovery incl. the deliberate non-convergent variant asserting verbatim
   error surfacing; save/load round-trip; protocol-purity check (fd3 carries only
   JSON while stderr carries the IPOPT banner).
6. **Review** — adversarial multi-agent pass over protocol compliance and
   handler correctness; fix; re-test.
7. **Register** — merge entry into `~/Library/Application Support/Claude/
   claude_desktop_config.json` (backup first), plus `claude mcp add` instructions
   for CLI use.

## 9. Acceptance criteria

- [ ] `echo`-piped MCP handshake completes; `tools/list` returns full registry with valid JSON Schemas.
- [ ] Pump case built purely through tool calls reproduces 12.02 kW shaft power.
- [ ] Ester case reproduces −159.4 kW duty and 98.7% recovery; RR=3 variant reproduces 7020/7177 kW duties.
- [ ] Mis-specified column returns DWSIM's max-iterations error verbatim through `solve`.
- [ ] Saved `.dwxmz` reloads via `load_flowsheet` with objects + results intact.
- [ ] Protocol stream on fd3 parses as pure ndjson across a full session that triggers the IPOPT banner.
- [ ] Registered and callable from the user's Claude Desktop.

## 10. Risks

| Risk | Mitigation |
|---|---|
| Native chatter corrupts protocol | fd-3 design; purity test in CI list |
| Engine cold-start stalls client handshake | lazy engine init |
| DWSIM API drift on upgrade | server pins to installed app; research from matching tag; regression scenarios |
| Reflection tools let the model set nonsense | property dumps + solve-error verbatim reporting keep it observable; engineer owns judgment (documented) |
| Rosetta deprecation | out of our hands; upstream ARM build is the fix; server design is arch-agnostic |

## 11. v2 roadmap (explicitly deferred)

Equilibrium/kinetic reactions, column stage profiles as first-class tool,
sensitivity sweeps, optimizer access, dynamics/integrator, costing & GHG,
Linux support, multi-session, MCP resources (expose flowsheet as browsable
resource tree), publishing as a public GPL v3 repo with install docs.
