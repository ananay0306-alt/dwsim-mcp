using System;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DwsimMcp
{
    // Entry point. The launcher script performs fd surgery before exec:
    //   exec 3>&1 1>&2
    // so fd 3 is the MCP protocol channel and fd 1 (stdout) is aliased to
    // stderr — DWSIM's Console chatter and the native solvers' direct fd-1
    // writes (IPOPT banner) land in the client's log stream, never in the
    // protocol. If fd 3 is absent (manual run), we fall back to real stdout
    // for protocol and push Console.Out to stderr.
    public static class Program
    {
        public static int Main(string[] args)
        {
            // The launcher exports DWSIM_MCP_FD3=1 after `exec 3>&1 1>&2`, so
            // fd presence is declared, not probed (a zero-byte write cannot
            // detect an invalid descriptor).
            Stream protocol;
            if (Environment.GetEnvironmentVariable("DWSIM_MCP_FD3") == "1")
            {
                var h = new SafeFileHandle(new IntPtr(3), ownsHandle: false);
                protocol = new FileStream(h, FileAccess.Write);
            }
            else
            {
                protocol = Console.OpenStandardOutput();
                Console.SetOut(Console.Error);
            }

            var log = new Logger();
            log.Info("dwsim-mcp starting; pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                   + " arch=" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

            var writer = new StreamWriter(protocol, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

            var server = new McpServer(log);
            var rc = new JsonRpcLoop(reader, writer, server, log).Run();
            log.Info("dwsim-mcp exiting rc=" + rc);
            return rc;
        }
    }

    public sealed class Logger
    {
        private readonly object _lock = new object();
        private readonly string _path;

        public Logger()
        {
            // The exe lives in <repo>/lib, so logs go to <repo>/logs.
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "logs");
            try { dir = Path.GetFullPath(dir); Directory.CreateDirectory(dir); }
            catch { dir = Path.GetTempPath(); }
            _path = Path.Combine(dir, "dwsim-mcp.log");
        }

        public void Info(string msg) => Write("INFO", msg);
        public void Error(string msg) => Write("ERROR", msg);

        private void Write(string level, string msg)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + msg;
            lock (_lock)
            {
                Console.Error.WriteLine("[dwsim-mcp] " + line);
                try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
            }
        }
    }
}
