#!/bin/zsh
# dwsim-mcp launcher — the command an MCP client runs.
#
# fd surgery, load-bearing: MCP stdio requires stdout to carry ONLY protocol
# JSON, but DWSIM prints to Console and its native solvers (IPOPT banner)
# write directly to fd 1. So: fd 3 becomes the protocol channel (the client's
# original stdout) and fd 1 is aliased to stderr, which MCP clients capture
# as server logs. DWSIM_MCP_FD3 tells the server the channel is armed.
ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DWSIM_MCP_DOTNET:-$HOME/.dotnet-x64}/dotnet"
exec 3>&1 1>&2
export DWSIM_MCP_FD3=1
cd "$ROOT/lib" || exit 1
exec "$DOTNET" dwsim-mcp.exe
