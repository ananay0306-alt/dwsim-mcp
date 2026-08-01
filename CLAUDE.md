# dwsim-mcp

Read **AGENTS.md** — it is the canonical guide for both installing this MCP
server and using its tools. Everything below is the 10-second version.

- Install: user installs DWSIM.app from dwsim.org, then `./setup.sh --register`,
  restart Claude. Verify: `python3 test/run_tests.py`.
- Use: create_flowsheet → add_compound → set_property_package → add_object →
  connect_objects → set_stream → solve → get_results. SI units. Save results
  with save_flowsheet to a `.dwxmz`.
- Non-empty `errors` from `solve` = numbers are NOT valid. Column won't
  converge → `configure_column {solver_method: "Napthali-Sandholm"}`.
- Energy streams connect to input port 1. Columns use `connect_column_stream`,
  never `connect_objects`.
- Building the server: `python3 build.py` (sources in `src/`, output in `lib/`).
  Rebuilding the workspace from scratch: `./setup.sh`.
