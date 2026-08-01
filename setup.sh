#!/bin/zsh
# dwsim-mcp setup — builds everything a fresh machine needs.
#
#   ./setup.sh              install prerequisites, build workspace + server, verify
#   ./setup.sh --register   additionally merge the server into Claude Desktop's config
#
# Requirements it checks/installs:
#   1. DWSIM.app in /Applications (you install this yourself — free from dwsim.org)
#   2. mono (csc compiler)             — via Homebrew if available
#   3. x64 .NET 8 runtime              — into ~/.dotnet-x64 (Rosetta on Apple Silicon)
#   4. assembly workspace in ./lib     — built from the DWSIM.app bundle
#   5. the server itself               — compiled into ./lib/dwsim-mcp.exe
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
DWSIM_APP="${DWSIM_APP:-/Applications/DWSIM.app}"
MB="$DWSIM_APP/Contents/MonoBundle"
DOTNET_DIR="${DWSIM_MCP_DOTNET:-$HOME/.dotnet-x64}"
LIB="$ROOT/lib"

step() { printf '==> %s\n' "$1"; }
die()  { printf 'ERROR: %s\n' "$1" >&2; exit 1; }

# Apple Silicon needs Rosetta to run the x64 runtime DWSIM's natives require.
if [ "$(uname -m)" = "arm64" ] && ! /usr/bin/pgrep -q oahd; then
  die "Rosetta 2 is required on Apple Silicon. Install it with:
  softwareupdate --install-rosetta --agree-to-license
then re-run this script."
fi

# ---- 1. DWSIM.app ----------------------------------------------------------
step "checking DWSIM.app"
[ -d "$MB" ] || die "DWSIM.app not found at $DWSIM_APP.
Install it first (free): https://dwsim.org -> Download -> macOS .dmg,
drag DWSIM.app to /Applications, then re-run this script.
(Set DWSIM_APP=/path/to/DWSIM.app if it lives elsewhere.)"

# ---- 2. mono / csc ---------------------------------------------------------
step "checking mono (csc)"
CSC=""
for c in "$DWSIM_MCP_CSC" /opt/homebrew/bin/csc /usr/local/bin/csc \
         /Library/Frameworks/Mono.framework/Versions/Current/Commands/csc; do
  [ -n "$c" ] && [ -x "$c" ] && CSC="$c" && break
done
if [ -z "$CSC" ]; then
  if command -v brew >/dev/null 2>&1; then
    step "installing mono via Homebrew"
    brew install mono
    for c in /opt/homebrew/bin/csc /usr/local/bin/csc; do
      [ -x "$c" ] && CSC="$c" && break
    done
  fi
fi
[ -n "$CSC" ] || die "csc not found. Install mono (brew install mono) and re-run."
echo "    csc: $CSC"

# ---- 3. x64 .NET 8 runtime -------------------------------------------------
step "checking x64 .NET 8 runtime"
if [ ! -x "$DOTNET_DIR/dotnet" ]; then
  step "installing x64 .NET 8 runtime into $DOTNET_DIR"
  TMP="$(mktemp -d)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TMP/dotnet-install.sh" \
    || die "download of dotnet-install.sh failed (network?)"
  chmod +x "$TMP/dotnet-install.sh"
  "$TMP/dotnet-install.sh" --runtime dotnet --channel 8.0 --architecture x64 \
      --install-dir "$DOTNET_DIR"
fi
NETCORE_DIR="$(ls -d "$DOTNET_DIR"/shared/Microsoft.NETCore.App/8.* | sort | tail -1)"
[ -n "$NETCORE_DIR" ] || die "x64 .NET 8 runtime not usable in $DOTNET_DIR"
echo "    runtime: $NETCORE_DIR"

# ---- 4. assembly workspace -------------------------------------------------
step "building assembly workspace in lib/"
rm -rf "$LIB"
mkdir -p "$LIB"

# 4a. symlink the entire app bundle (assemblies, native dylibs, data files)
for f in "$MB"/*; do
  ln -s "$f" "$LIB/$(basename "$f")"
done

# 4b. remove assemblies .NET 8 supplies itself (framework shadowing breaks
#     startup), then restore the two the type scan still needs app-locally.
python3 - "$LIB" <<'PY'
import os, re, sys
lib = sys.argv[1]
FR = re.compile(r"^(System($|\.)|mscorlib|Microsoft\.(CSharp|VisualBasic)|Mono\.|netstandard|WindowsBase|Accessibility|I18N)", re.I)
removed = 0
for f in sorted(os.listdir(lib)):
    if f.endswith(".dll") and FR.match(f) and os.path.islink(os.path.join(lib, f)):
        os.remove(os.path.join(lib, f)); removed += 1
print(f"    removed {removed} framework-shadowing assemblies")
PY
for keep in System.Windows.Forms.dll System.Drawing.dll; do
  ln -sf "$MB/$keep" "$LIB/$keep"
done

# 4c. .NET-8-compatible replacements for assemblies whose bundled builds
#     target .NET Framework and cannot load on the modern runtime.
step "fetching .NET 8 shim packages from nuget.org"
TMPN="$(mktemp -d)"
fetch() { # name version tfm
  curl -fsSL -o "$TMPN/$1.zip" "https://www.nuget.org/api/v2/package/$1/$2" \
    || die "download of $1 $2 from nuget.org failed (network?)"
  unzip -o -q "$TMPN/$1.zip" -d "$TMPN/$1"
  rm -f "$LIB/$1.dll"
  cp "$TMPN/$1/lib/$3/$1.dll" "$LIB/"
  echo "    $1 $2 ($3)"
}
fetch Newtonsoft.Json 13.0.3 netstandard2.0
fetch System.Configuration.ConfigurationManager 8.0.0 net8.0
fetch System.Security.Permissions 8.0.0 net8.0
fetch System.Runtime.Caching 8.0.0 net8.0
fetch System.Drawing.Common 8.0.0 net8.0

# 4d. Microsoft.VisualBasic shim: the framework facade lacks the VB `My` base
#     types DWSIM's startup type-scan needs; this local assembly (v10.9.0.0,
#     declared in deps.json) defines them and forwards everything else.
step "compiling Microsoft.VisualBasic shim"
"$CSC" -nologo -nostdlib -target:library -out:"$LIB/Microsoft.VisualBasic.dll" \
  -reference:"$NETCORE_DIR/System.Runtime.dll" \
  -reference:"$NETCORE_DIR/System.Private.CoreLib.dll" \
  -reference:"$NETCORE_DIR/Microsoft.VisualBasic.Core.dll" \
  "$ROOT/shim/stubs.cs" "$ROOT/shim/forwards.cs"

# 4e. native aliases: .NET Core ignores Mono's DllMap configs, so the names
#     P/Invoke asks for must exist as symlinks (Ipopt39 -> libipopt, ...).
step "creating native library aliases from DllMap configs"
python3 - "$MB" "$LIB" <<'PY'
import glob, os, sys, xml.etree.ElementTree as ET
mb, lib = sys.argv[1], sys.argv[2]
made = 0
for cfg in glob.glob(os.path.join(mb, "*.dll.config")):
    try:
        root = ET.parse(cfg).getroot()
    except ET.ParseError:
        continue
    for dm in root.iter("dllmap"):
        dll, target = dm.get("dll"), dm.get("target")
        if not dll or not target or not target.endswith(".dylib"):
            continue
        tgt = os.path.join(mb, target)
        if not os.path.exists(tgt):
            continue
        for alias in (dll + ".dylib", "lib" + dll + ".dylib"):
            p = os.path.join(lib, alias)
            if not os.path.exists(p):
                os.symlink(tgt, p); made += 1
print(f"    created {made} aliases")
PY

# ---- 5. build the server ---------------------------------------------------
step "compiling dwsim-mcp server"
python3 "$ROOT/build.py"

# ---- 6. verify -------------------------------------------------------------
step "verifying MCP handshake"
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  | "$ROOT/dwsim-mcp.sh" 2>/dev/null \
  | python3 -c '
import json, sys
frames = [json.loads(l) for l in sys.stdin if l.strip()]
tools = next((f["result"]["tools"] for f in frames if "result" in f and "tools" in f.get("result", {})), None)
assert tools, "tools/list failed"
print(f"    OK - server answers with {len(tools)} tools")
'

# ---- 7. register -----------------------------------------------------------
CFG="$HOME/Library/Application Support/Claude/claude_desktop_config.json"
if [ "$1" = "--register" ]; then
  step "registering in Claude Desktop config"
  python3 - "$ROOT" "$CFG" <<'PY'
import json, os, sys, shutil
root, cfg_path = sys.argv[1], sys.argv[2]
cfg = {}
if os.path.exists(cfg_path):
    shutil.copy(cfg_path, cfg_path + ".backup-before-dwsim-mcp")
    with open(cfg_path) as fh:
        content = fh.read().strip()
    cfg = json.loads(content) if content else {}
else:
    os.makedirs(os.path.dirname(cfg_path), exist_ok=True)
cfg.setdefault("mcpServers", {})["dwsim"] = {"command": os.path.join(root, "dwsim-mcp.sh")}
with open(cfg_path, "w") as fh:
    json.dump(cfg, fh, indent=2)
print("    registered (backup written). Restart Claude Desktop to activate.")
PY
else
  step "done — register the server with your Claude:"
  cat <<EOF

  Claude Desktop: add to $CFG
    {"mcpServers": {"dwsim": {"command": "$ROOT/dwsim-mcp.sh"}}}
  (or re-run: ./setup.sh --register)

  Claude Code:
    claude mcp add --scope user dwsim $ROOT/dwsim-mcp.sh

  Then restart Claude and just ask it to build a simulation.
EOF
fi
