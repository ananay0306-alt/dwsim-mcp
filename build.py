#!/usr/bin/env python3
"""Build dwsim-mcp against the installed DWSIM.app assemblies.

Multi-file evolution of automation/prepare.py, same three proven constraints:
compile with mono's csc (arm64; IL is portable), run on the x64 .NET 8 runtime
(natives are x86_64), and emit a deps.json so the local Microsoft.VisualBasic
shim (10.9.0.0) beats the framework facade.

Output lands in the automation lib workspace, where the assemblies, native
aliases, and overrides already live.
"""
import glob, json, os, re, subprocess, sys

ROOT = os.path.dirname(os.path.abspath(__file__))
LIB = os.environ.get("DWSIM_MCP_LIB", os.path.join(ROOT, "lib"))
NAME = "dwsim-mcp"

FRAMEWORK_RE = re.compile(
    r"^(System($|\.)|mscorlib|Microsoft\.CSharp|Mono\.|netstandard|Accessibility|I18N)", re.I)

def managed(p):
    try:
        with open(p, "rb") as fh:
            return b"BSJB" in fh.read()
    except OSError:
        return False

refs, dlls = [], []
for f in sorted(os.listdir(LIB)):
    if not f.endswith(".dll"):
        continue
    dlls.append(f)
    full = os.path.join(LIB, f)
    if FRAMEWORK_RE.match(f) or not managed(full):
        continue
    refs.append("-reference:" + full)

# Newtonsoft.Json in the workspace is the netstandard2.0 build; consuming its
# API from csc needs mono's netstandard facade on the reference list.
FACADE_CANDIDATES = [
    "/opt/homebrew/lib/mono/4.5/Facades/netstandard.dll",
    "/usr/local/lib/mono/4.5/Facades/netstandard.dll",
    "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/4.5/Facades/netstandard.dll",
]
facade = next((f for f in FACADE_CANDIDATES if os.path.exists(f)), None)
if not facade:
    print("mono netstandard facade not found; install mono (brew install mono)")
    sys.exit(1)
refs.append("-reference:" + facade)

sources = sorted(glob.glob(os.path.join(ROOT, "src", "*.cs")))
if not sources:
    print("no sources in src/"); sys.exit(1)

exe = os.path.join(LIB, NAME + ".exe")
rsp = os.path.join(ROOT, NAME + ".rsp")
with open(rsp, "w") as fh:
    fh.write("\n".join(
        ["-nologo", "-out:" + exe, "-langversion:latest",
         "-nowarn:CS1701,CS1702,CS0618,CS0612"] + refs + sources))

CSC_CANDIDATES = [
    os.environ.get("DWSIM_MCP_CSC"),
    "/opt/homebrew/bin/csc",
    "/usr/local/bin/csc",
    "/Library/Frameworks/Mono.framework/Versions/Current/Commands/csc",
]
csc = next((c for c in CSC_CANDIDATES if c and os.path.exists(c)), "csc")

r = subprocess.run([csc, "@" + rsp], capture_output=True, text=True)
out = (r.stdout or "") + (r.stderr or "")
errs = [l for l in out.splitlines() if ": error " in l]
warns = [l for l in out.splitlines() if ": warning " in l]
if errs:
    print("COMPILE FAILED (%d errors):" % len(errs))
    for l in errs[:30]:
        print("  " + l)
    sys.exit(1)

with open(os.path.join(LIB, NAME + ".runtimeconfig.json"), "w") as fh:
    json.dump({"runtimeOptions": {
        "tfm": "net8.0",
        "framework": {"name": "Microsoft.NETCore.App", "version": "8.0.0"},
        "configProperties": {"System.Drawing.EnableUnixSupport": True},
    }}, fh, indent=2)

runtime = {d: {"assemblyVersion": "10.9.0.0" if d == "Microsoft.VisualBasic.dll" else "0.0.0.0",
               "fileVersion": "10.9.0.0" if d == "Microsoft.VisualBasic.dll" else "0.0.0.0"}
           for d in dlls}
with open(os.path.join(LIB, NAME + ".deps.json"), "w") as fh:
    json.dump({
        "runtimeTarget": {"name": ".NETCoreApp,Version=v8.0", "signature": ""},
        "compilationOptions": {},
        "targets": {".NETCoreApp,Version=v8.0": {
            "app/1.0.0": {"runtime": {NAME + ".exe": {}}, "dependencies": {"locals": "1.0.0"}},
            "locals/1.0.0": {"runtime": runtime}}},
        "libraries": {
            "app/1.0.0": {"type": "project", "serviceable": False, "sha512": ""},
            "locals/1.0.0": {"type": "project", "serviceable": False, "sha512": ""}},
    }, fh, indent=2)

print("BUILT %s (%d sources, %d refs, %d warnings)" % (exe, len(sources), len(refs), len(warns)))
