#!/usr/bin/env python3
"""dwsim-mcp regression tests.

Drives the server over its real stdio interface — no MCP client needed:

    python3 test/run_tests.py            # pump case (~1 min)
    python3 test/run_tests.py --full     # + reactor/column case (~3 min)

Checks physics against known-good values, verbatim solver-error surfacing,
and protocol purity (stdout must be pure JSON even when native solvers print).
"""
import json, os, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LAUNCHER = os.path.join(ROOT, "dwsim-mcp.sh")
OUT = os.path.join(tempfile.gettempdir(), "dwsim-mcp-tests")
os.makedirs(OUT, exist_ok=True)

def rpc(calls, timeout=900):
    lines, i = [], 0
    for method, params in calls:
        msg = {"jsonrpc": "2.0", "method": method}
        if not method.startswith("notifications"):
            i += 1
            msg["id"] = i
            msg["params"] = params
        lines.append(json.dumps(msg))
    proc = subprocess.run([LAUNCHER], input="\n".join(lines) + "\n",
                          capture_output=True, text=True, timeout=timeout)
    frames = [json.loads(l) for l in proc.stdout.splitlines() if l.strip()]
    return frames, proc

def call(name, args):
    return ("tools/call", {"name": name, "arguments": args})

def text_of(frame):
    return frame["result"]["content"][0]["text"]

def payload_of(frame):
    return json.loads(text_of(frame))

FAILURES = []

def check(label, ok, detail=""):
    print(("  PASS  " if ok else "  FAIL  ") + label + (("  " + detail) if detail and not ok else ""))
    if not ok:
        FAILURES.append(label)

def test_pump():
    print("pump case: water 10 kg/s, 1 atm -> 10 bar @ 75% eff")
    calls = [
        ("initialize", {"protocolVersion": "2025-06-18"}),
        ("notifications/initialized", None),
        call("create_flowsheet", {}),
        call("add_compound", {"name": "Water"}),
        call("set_property_package", {"name": "Steam Tables (IAPWS-IF97)"}),
        call("add_object", {"type": "MaterialStream", "tag": "FEED"}),
        call("add_object", {"type": "MaterialStream", "tag": "DISCHARGE"}),
        call("add_object", {"type": "EnergyStream", "tag": "WORK"}),
        call("add_object", {"type": "Pump", "tag": "P-101"}),
        call("connect_objects", {"from_tag": "FEED", "to_tag": "P-101", "to_port": 0}),
        call("connect_objects", {"from_tag": "P-101", "to_tag": "DISCHARGE", "from_port": 0}),
        call("connect_objects", {"from_tag": "WORK", "to_tag": "P-101", "to_port": 1}),
        call("set_stream", {"tag": "FEED", "temperature": 298.15, "pressure": 101325, "mass_flow": 10.0}),
        call("set_object_property", {"tag": "P-101", "property": "CalcMode", "value": "OutletPressure"}),
        call("set_object_property", {"tag": "P-101", "property": "Pout", "value": 1000000}),
        call("set_object_property", {"tag": "P-101", "property": "Eficiencia", "value": 75}),
        call("solve", {}),
        call("get_results", {}),
        call("save_flowsheet", {"path": os.path.join(OUT, "pump.dwxmz")}),
    ]
    frames, proc = rpc(calls)
    check("all frames answered", len(frames) == 18, f"got {len(frames)}")
    check("protocol purity", all(l.lstrip().startswith("{") for l in proc.stdout.splitlines() if l.strip()))
    solve = payload_of(frames[15])
    check("converged", solve["converged"] is True, json.dumps(solve.get("errors")))
    results = payload_of(frames[16])
    work = next(e for e in results["energy_streams"] if e["tag"] == "WORK")
    check("shaft power ~12.02 kW", abs(work["energy_flow_kW"] - 12.018) < 0.05,
          str(work["energy_flow_kW"]))
    disch = next(s for s in results["material_streams"] if s["tag"] == "DISCHARGE")
    check("discharge at 10 bar", abs(disch["P_Pa"] - 1e6) < 1)

def test_ester_column():
    print("reactor + column: esterification, incl. deliberate solver failure")
    calls = [
        ("initialize", {"protocolVersion": "2025-06-18"}),
        ("notifications/initialized", None),
        call("create_flowsheet", {}),
        call("add_compound", {"name": "Ethanol"}),
        call("add_compound", {"name": "Acetic acid"}),
        call("add_compound", {"name": "Ethyl acetate"}),
        call("add_compound", {"name": "Water"}),
        call("set_property_package", {"name": "NRTL"}),
        call("create_reaction", {"name": "Ester", "stoichiometry":
            {"Ethanol": -1, "Acetic acid": -1, "Ethyl acetate": 1, "Water": 1},
            "base_compound": "Acetic acid", "phase": "Liquid", "conversion_expression": "60"}),
        call("add_object", {"type": "MaterialStream", "tag": "FEED"}),
        call("add_object", {"type": "MaterialStream", "tag": "RVAP"}),
        call("add_object", {"type": "MaterialStream", "tag": "RLIQ"}),
        call("add_object", {"type": "EnergyStream", "tag": "RDUTY"}),
        call("add_object", {"type": "ConversionReactor", "tag": "R-101"}),
        call("add_object", {"type": "MaterialStream", "tag": "DIST"}),
        call("add_object", {"type": "MaterialStream", "tag": "BTMS"}),
        call("add_object", {"type": "EnergyStream", "tag": "QC"}),
        call("add_object", {"type": "EnergyStream", "tag": "QR"}),
        call("add_object", {"type": "DistillationColumn", "tag": "T-101"}),
        call("connect_objects", {"from_tag": "FEED", "to_tag": "R-101", "to_port": 0}),
        call("connect_objects", {"from_tag": "R-101", "to_tag": "RVAP", "from_port": 0}),
        call("connect_objects", {"from_tag": "R-101", "to_tag": "RLIQ", "from_port": 1}),
        call("connect_objects", {"from_tag": "RDUTY", "to_tag": "R-101", "to_port": 1}),
        call("set_object_property", {"tag": "R-101", "property": "ReactorOperationMode", "value": "Isothermic"}),
        call("set_object_property", {"tag": "R-101", "property": "DeltaP", "value": 0}),
        call("configure_column", {"tag": "T-101", "stages": 20, "top_pressure": 101325,
            "pressure_drop": 0,
            "condenser_spec": {"type": "Reflux Ratio", "value": 2.0},
            "reboiler_spec": {"type": "Product Molar Flow Rate", "value": 65.0, "unit": "mol/s"}}),
        call("connect_column_stream", {"column_tag": "T-101", "stream_tag": "RLIQ", "role": "feed", "stage": 10}),
        call("connect_column_stream", {"column_tag": "T-101", "stream_tag": "DIST", "role": "distillate"}),
        call("connect_column_stream", {"column_tag": "T-101", "stream_tag": "BTMS", "role": "bottoms"}),
        call("connect_column_stream", {"column_tag": "T-101", "stream_tag": "QC", "role": "condenser_duty"}),
        call("connect_column_stream", {"column_tag": "T-101", "stream_tag": "QR", "role": "reboiler_duty"}),
        call("set_stream", {"tag": "FEED", "temperature": 340, "pressure": 101325, "molar_flow": 100,
            "mole_fractions": {"Ethanol": 0.5, "Acetic acid": 0.5, "Ethyl acetate": 0, "Water": 0}}),
        call("solve", {}),   # deliberately mis-specified: must NOT converge
        call("configure_column", {"tag": "T-101", "solver_method": "Napthali-Sandholm",
            "max_iterations": 200,
            "reboiler_spec": {"type": "Product Molar Flow Rate", "value": 50.0, "unit": "mol/s"}}),
        call("solve", {}),   # fixed: must converge
        call("get_results", {}),
    ]
    frames, proc = rpc(calls)
    bad = payload_of(frames[-4])
    check("mis-spec surfaces verbatim error", bad["converged"] is False and len(bad["errors"]) > 0,
          json.dumps(bad)[:120])
    good = payload_of(frames[-2])
    check("fixed column converges", good["converged"] is True, json.dumps(good.get("errors"))[:120])
    results = payload_of(frames[-1])
    dist = next(s for s in results["material_streams"] if s["tag"] == "DIST")
    check("ester in distillate ~59%", abs(dist["mole_fractions"]["Ethyl acetate"] - 0.592) < 0.02,
          str(dist["mole_fractions"]))
    check("IPOPT banner on stderr not stdout",
          "Ipopt" not in proc.stdout and "Ipopt" in proc.stderr)

if __name__ == "__main__":
    test_pump()
    if "--full" in sys.argv:
        test_ester_column()
    print()
    if FAILURES:
        print("FAILED: " + ", ".join(FAILURES))
        sys.exit(1)
    print("ALL TESTS PASSED")
