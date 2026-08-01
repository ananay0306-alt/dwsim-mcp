using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace DwsimMcp
{
    // MCP method dispatch: lifecycle + tools/list + tools/call.
    // The engine (and DWSIM assembly load) is lazy so initialize/tools/list
    // return instantly and client handshakes never wait on the ~seconds of
    // ChEDL database loading.
    public sealed class McpServer
    {
        public const string ServerVersion = "0.1.0";
        private const string FallbackProtocol = "2025-06-18";

        private readonly Logger _log;
        private readonly ToolRegistry _tools;
        private Engine _engine;

        public McpServer(Logger log)
        {
            _log = log;
            _tools = new ToolRegistry();
        }

        private Engine Engine
        {
            get
            {
                if (_engine == null)
                {
                    _log.Info("lazy-initializing DWSIM engine...");
                    _engine = new Engine(_log);
                    _log.Info("engine ready");
                }
                return _engine;
            }
        }

        public JToken Dispatch(string method, JObject @params, out bool handled)
        {
            handled = true;
            switch (method)
            {
                case "initialize":
                    var requested = (string)@params["protocolVersion"] ?? FallbackProtocol;
                    return new JObject
                    {
                        ["protocolVersion"] = requested,
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject
                        {
                            ["name"] = "dwsim-mcp",
                            ["version"] = ServerVersion,
                        },
                        ["instructions"] =
                            "DWSIM chemical process simulator. Typical flow: create_flowsheet -> " +
                            "add_compound (repeat) -> set_property_package -> add_object for streams/unit ops -> " +
                            "connect_objects -> set_stream / configure_column / set_object_property -> solve -> " +
                            "get_stream / get_results. All units are SI (K, Pa, kg/s, mol/s, kW). " +
                            "solve returns the solver's error list verbatim: an empty errors array means converged; " +
                            "a non-empty one means the numbers are NOT trustworthy even though values are present. " +
                            "Call describe_flowsheet after load_flowsheet to orient yourself.",
                    };

                case "notifications/initialized":
                case "notifications/cancelled":
                case "notifications/roots/list_changed":
                    return null; // notifications: acknowledged by silence

                case "ping":
                    return new JObject();

                case "tools/list":
                    return new JObject { ["tools"] = _tools.ListJson() };

                case "tools/call":
                    return CallTool(@params);

                case "resources/list":
                    return new JObject { ["resources"] = new JArray() };

                case "prompts/list":
                    return new JObject { ["prompts"] = new JArray() };

                default:
                    handled = false;
                    return null;
            }
        }

        private JToken CallTool(JObject @params)
        {
            var name = (string)@params["name"];
            var args = @params["arguments"] as JObject ?? new JObject();
            _log.Info("tool call: " + name);

            if (name == null || !_tools.Has(name))
                return ToolError("Unknown tool: " + name);

            try
            {
                var payload = _tools.Invoke(name, args, Engine);
                return new JObject
                {
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = payload is string s
                                ? s
                                : ((JToken)payload).ToString(Newtonsoft.Json.Formatting.Indented),
                        },
                    },
                };
            }
            catch (ToolException tex)
            {
                return ToolError(tex.Message);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? " | inner: " + ex.InnerException.Message : "";
                _log.Error("tool " + name + " failed: " + ex);
                return ToolError(ex.GetType().Name + ": " + ex.Message + inner);
            }
        }

        private static JObject ToolError(string message) =>
            new JObject
            {
                ["content"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = message },
                },
                ["isError"] = true,
            };
    }

    // Thrown by tool handlers for expected, user-fixable problems
    // (unknown tag, bad enum value). Message goes straight to the model.
    public sealed class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }
}
