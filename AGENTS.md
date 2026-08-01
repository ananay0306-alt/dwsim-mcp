# Agent guide — dwsim-mcp

You are an AI agent. This file tells you everything you need to (a) install this
MCP server on a user's machine, or (b) use it to run chemical process
simulations. It is the canonical reference; the README is the human summary.

---

## A. Installing (agent performing setup)

Preconditions to check, in order:

1. **macOS.** This server is macOS-only today (DWSIM's native solver libraries).
2. **DWSIM.app in /Applications.** If missing, the USER must install it —
   direct them to https://dwsim.org → Download → macOS `.dmg` (free), drag to
   Applications. You cannot skip this; the server loads DWSIM's assemblies.
3. Then run, from the repo root:

```bash
./setup.sh
```

It is idempotent and self-verifying: checks/installs mono (via Homebrew) and an
x64 .NET 8 runtime (`~/.dotnet-x64`), builds the assembly workspace in `lib/`,
compiles the server, and performs a real MCP handshake before reporting success.
Add `--register` to also write the Claude Desktop config entry (a backup of the
existing config is created first). For Claude Code:

```bash
claude mcp add --scope user dwsim "$(pwd)/dwsim-mcp.sh"
```

Restart the Claude app afterwards. Verify with:

```bash
python3 test/run_tests.py          # ~1 min physics smoke test
```

Troubleshooting map:

| Symptom | Cause | Fix |
|---|---|---|
| `DWSIM.app not found` | app not installed | user installs from dwsim.org |
| `csc not found` | no mono | `brew install mono`, re-run |
| Apple Silicon: "Bad CPU type" | Rosetta missing | `softwareupdate --install-rosetta` |
| server connects but first call slow | normal | engine DB load is lazy, ~seconds, once per session |
| `run_script` compile errors about csc | non-standard mono | set `DWSIM_MCP_CSC=/path/to/csc` |

Environment overrides (rarely needed): `DWSIM_APP`, `DWSIM_MCP_DOTNET`,
`DWSIM_MCP_CSC`.

---

## B. Simulating (agent using the tools)

### The golden path

```
create_flowsheet
→ add_compound            (one call per compound, exact DB names; search_compounds if unsure)
→ set_property_package    (see selection guide below)
→ add_object              (streams AND unit ops; material/energy streams are objects)
→ connect_objects         (see port rules below)
→ set_stream / set_object_property / configure_column
→ solve
→ get_results / get_stream
→ save_flowsheet          (path ending .dwxmz — opens in the DWSIM GUI)
```

After `load_flowsheet`, always call `describe_flowsheet` first to orient.

### Units — SI everywhere, no exceptions

K, Pa, kg/s, mol/s, kW, m². Efficiencies are **percent** (75, not 0.75).

### The rule that matters most

**`solve` returning non-empty `errors` means the numbers are wrong**, even
though values are present (DWSIM leaves stale/zero values in place). Never
report results to the user from a non-converged solve. The error text is
DWSIM's own, verbatim. Typical fixes:
- Distillation of non-ideal/azeotropic mixtures: switch solver —
  `configure_column {solver_method: "Napthali-Sandholm"}` (the default
  Wang-Henke bubble-point often fails on these), raise `max_iterations`.
- Check product-flow specs against the feed: a bottoms/distillate rate that
  fights the volatility order can make the spec unreachable.

### Port rules (connect_objects)

- Direction encodes role: stream→unit = feed; unit→stream = product.
- Material inlet is input port 0. **Energy-stream port is input port 1** on
  pumps, heaters, coolers, and reactors.
- Reactors have two product ports: 0 = vapor outlet, 1 = liquid outlet.
- `-1` (default) auto-picks the first free suitable port. `get_ports` lists an
  object's ports with types and occupancy.
- **Columns: never use connect_objects.** Use `connect_column_stream` with
  roles feed (requires 0-based stage; 0 = condenser)/distillate/bottoms/
  condenser_duty/reboiler_duty — the duty connections have non-obvious
  directions that the tool handles.

### Property package selection

| System | Package |
|---|---|
| Pure water / steam | `Steam Tables (IAPWS-IF97)` |
| Hydrocarbons, natural gas, air | `Peng-Robinson (PR)` or `Soave-Redlich-Kwong (SRK)` |
| Polar / non-ideal liquids (alcohols, acids, water+organics) | `NRTL` or `UNIQUAC` |
| Electrolytes, refrigerants, special | `list_property_packages` and pick |

Tell the user which package you chose and why — it is a modelling judgment,
and DWSIM may estimate missing interaction parameters silently.

### Common property names (set_object_property)

| Object | Properties |
|---|---|
| Pump | `CalcMode` (Delta_P\|OutletPressure\|Power), `Pout` Pa, `DeltaP` Pa, `Eficiencia` % |
| Compressor | `CalcMode`, **`POut`** (capital O!), `AdiabaticEfficiency` % |
| Heater/Cooler | `CalcMode` (HeatAdded\|OutletTemperature\|…), `OutletTemperature` K, `DeltaQ` kW |
| Valve | `CalcMode`, `OutletPressure` Pa |
| Reactors | `ReactorOperationMode` (Isothermic\|Adiabatic\|OutletTemperature), `DeltaP`, `OutletTemperature` |
| HeatExchanger | `CalculationMode` (not CalcMode), `Area` m², `OverallCoefficient` W/m²K |
| Splitter | `Ratios` as array summing to 1 |

Enums are passed as their member-name string. `get_object` dumps current
values when unsure.

### Reactions

`create_reaction` = conversion reactions: stoichiometry dict (negative =
reactant), base compound, phase, conversion expression in % (may use `T` in K,
e.g. `"80 - 0.01*(T-300)"`). Auto-added to the default set that reactors use.
All participating compounds must be added first. For equilibrium/kinetic
reactions use `call_object_method` on the flowsheet (`CreateEquilibriumReaction`)
or `run_script`.

### Escape hatches, in escalation order

1. `set_object_property` — any public property, reflection.
2. `call_object_method` — any public method; string args matching an existing
   tag are passed as that object.
3. `run_script` — full C# against the live flowsheet (body of
   `object Run(IFlowsheet sim)`; DWSIM namespaces pre-imported). Use for
   anything else, e.g. per-stage column profiles: `col.Tf` after solving.

### Session model

One flowsheet per session, held in memory — iterate freely (change one property,
re-solve: seconds). State is lost when the session ends: **save to .dwxmz when
the result matters.** The GUI and this server never share live state; the file
is the handoff in both directions.
