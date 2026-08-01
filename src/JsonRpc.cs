using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace DwsimMcp
{
    // Newline-delimited JSON-RPC 2.0 over the MCP stdio transport.
    // One request at a time — DWSIM's engine is not thread-safe and MCP
    // clients serialize tool calls anyway.
    public sealed class JsonRpcLoop
    {
        private readonly TextReader _in;
        private readonly TextWriter _out;
        private readonly McpServer _server;
        private readonly Logger _log;

        public JsonRpcLoop(TextReader input, TextWriter output, McpServer server, Logger log)
        {
            _in = input; _out = output; _server = server; _log = log;
        }

        public int Run()
        {
            string line;
            while (!_channelDead && (line = _in.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;

                JObject msg;
                try { msg = JObject.Parse(line); }
                catch (Exception ex)
                {
                    _log.Error("unparseable frame: " + ex.Message);
                    Send(ErrorResponse(JValue.CreateNull(), -32700, "Parse error"));
                    continue;
                }

                var id = msg["id"];
                // method may be absent or a non-string token — an unguarded
                // (string) cast here would crash the loop on a malformed frame.
                var methodToken = msg["method"];
                var method = methodToken != null && methodToken.Type == JTokenType.String
                    ? (string)methodToken : null;
                var @params = msg["params"] as JObject ?? new JObject();

                if (method == null)
                {
                    if (id != null) Send(ErrorResponse(id, -32600, "Invalid request: no method"));
                    continue;
                }

                bool isNotification = id == null;
                try
                {
                    var result = _server.Dispatch(method, @params, out bool handled);
                    if (isNotification) continue;          // notifications never get responses
                    if (!handled)
                    {
                        Send(ErrorResponse(id, -32601, "Method not found: " + method));
                        continue;
                    }
                    Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
                }
                catch (Exception ex)
                {
                    _log.Error("dispatch(" + method + "): " + ex);
                    if (!isNotification)
                        Send(ErrorResponse(id, -32603, "Internal error: " + ex.Message));
                }
            }
            return 0;
        }

        private static JObject ErrorResponse(JToken id, int code, string message) =>
            new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };

        // A write failure means the client is gone (EPIPE on the protocol
        // channel). Exit the loop cleanly instead of crashing with a stack
        // trace — there is nobody left to talk to.
        private bool _channelDead;

        private void Send(JObject obj)
        {
            if (_channelDead) return;
            try
            {
                _out.WriteLine(obj.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                _log.Error("protocol channel write failed (client gone?): " + ex.Message);
                _channelDead = true;
            }
        }
    }
}
