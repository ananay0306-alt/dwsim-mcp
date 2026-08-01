using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.Interfaces;

namespace DwsimMcp
{
    public sealed partial class ToolRegistry
    {
        private sealed class Tool
        {
            public string Name;
            public string Description;
            public JObject Schema;
            public Func<JObject, Engine, object> Handler;
        }

        private readonly Dictionary<string, Tool> _tools = new Dictionary<string, Tool>();

        public bool Has(string name) => _tools.ContainsKey(name);

        public object Invoke(string name, JObject args, Engine engine) =>
            _tools[name].Handler(args, engine);

        public JArray ListJson()
        {
            var arr = new JArray();
            foreach (var t in _tools.Values)
                arr.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["inputSchema"] = t.Schema,
                });
            return arr;
        }

        private void Add(string name, string description, JObject schema,
                         Func<JObject, Engine, object> handler)
        {
            _tools[name] = new Tool { Name = name, Description = description, Schema = schema, Handler = handler };
        }

        // ---- schema helpers -------------------------------------------------

        private static JObject Obj(params (string name, JObject prop, bool required)[] props)
        {
            var properties = new JObject();
            var required = new JArray();
            foreach (var (n, p, r) in props)
            {
                properties[n] = p;
                if (r) required.Add(n);
            }
            var schema = new JObject { ["type"] = "object", ["properties"] = properties };
            if (required.Count > 0) schema["required"] = required;
            return schema;
        }

        private static JObject Str(string desc) => new JObject { ["type"] = "string", ["description"] = desc };
        private static JObject Num(string desc) => new JObject { ["type"] = "number", ["description"] = desc };
        private static JObject Int(string desc) => new JObject { ["type"] = "integer", ["description"] = desc };
        private static JObject NumMap(string desc) => new JObject
        {
            ["type"] = "object",
            ["description"] = desc,
            ["additionalProperties"] = new JObject { ["type"] = "number" },
        };

        private static string ReqStr(JObject a, string name)
        {
            var v = (string)a[name];
            if (string.IsNullOrWhiteSpace(v))
                throw new ToolException("Missing required argument: " + name);
            return v;
        }

        public ToolRegistry()
        {
            RegisterSessionTools();
            RegisterBasisTools();
            RegisterTopologyTools();
            RegisterConditionTools();
            RegisterExecutionTools();
        }

        // ---- session --------------------------------------------------------

        private void RegisterSessionTools()
        {
            Add("create_flowsheet",
                "Create a new empty flowsheet, replacing the current one. Start here for a new simulation.",
                Obj(),
                (a, e) => { e.NewFlowsheet(); return "New flowsheet created."; });

            Add("load_flowsheet",
                "Load a DWSIM flowsheet (.dwxmz or .dwxml) from an absolute file path, replacing the current one. " +
                "Call describe_flowsheet afterwards to orient yourself.",
                Obj(("path", Str("Absolute path to the flowsheet file"), true)),
                (a, e) => { e.Load(ReqStr(a, "path")); return Describe(e); });

            Add("save_flowsheet",
                "Save the current flowsheet. Use a .dwxmz extension for the normal compressed format that the DWSIM GUI opens.",
                Obj(("path", Str("Absolute destination path, e.g. /Users/me/Desktop/process.dwxmz"), true)),
                (a, e) =>
                {
                    var path = ReqStr(a, "path");
                    e.Save(path);
                    return "Saved to " + path;
                });

            Add("describe_flowsheet",
                "Overview of the current flowsheet: compounds, property packages, reactions, every object with its " +
                "type and calculation status, and the connection map. Call this after load_flowsheet or when unsure of state.",
                Obj(),
                (a, e) => Describe(e));
        }

        private static JObject Describe(Engine e)
        {
            var fs = e.RequireFlowsheet();

            var objects = new JArray();
            foreach (var o in fs.SimulationObjects.Values.OrderBy(x => x.GraphicObject?.Tag))
            {
                var g = o.GraphicObject;
                var entry = new JObject
                {
                    ["tag"] = g?.Tag,
                    ["type"] = o.GetType().Name,
                    ["calculated"] = g?.Calculated ?? false,
                };
                var conns = new JArray();
                if (g != null)
                {
                    foreach (var c in g.OutputConnectors.Where(c => c.IsAttached && c.AttachedConnector != null))
                        conns.Add(g.Tag + " -> " + c.AttachedConnector.AttachedTo?.Tag);
                    if (g.EnergyConnector != null && g.EnergyConnector.IsAttached && g.EnergyConnector.AttachedConnector != null)
                        conns.Add(g.Tag + " -> " + g.EnergyConnector.AttachedConnector.AttachedTo?.Tag + " (energy)");
                }
                if (conns.Count > 0) entry["connections"] = conns;
                objects.Add(entry);
            }

            var reactions = new JArray();
            foreach (var r in fs.Reactions.Values)
                reactions.Add(new JObject { ["id"] = r.ID, ["name"] = r.Name });

            var packages = new JArray();
            try
            {
                var ppProp = fs.GetType().GetProperty("PropertyPackages");
                if (ppProp?.GetValue(fs) is System.Collections.IDictionary ppd)
                    foreach (var v in ppd.Values)
                        packages.Add(v.GetType().Name);
            }
            catch { }

            return new JObject
            {
                ["compounds"] = new JArray(fs.SelectedCompounds.Keys.Cast<object>().ToArray()),
                ["property_packages"] = packages,
                ["reactions"] = reactions,
                ["objects"] = objects,
                ["solved"] = ReadSolved(fs),
            };
        }

        private static JToken ReadSolved(IFlowsheet fs)
        {
            try
            {
                var p = fs.GetType().GetProperty("Solved");
                if (p != null) return JToken.FromObject(p.GetValue(fs));
            }
            catch { }
            return JValue.CreateNull();
        }

        // ---- basis ----------------------------------------------------------

        private void RegisterBasisTools()
        {
            Add("search_compounds",
                "Search the compound database by name substring (case-insensitive). Returns exact database names — " +
                "add_compound requires the exact name.",
                Obj(("query", Str("Substring to search for, e.g. 'ethan'"), true),
                    ("limit", Int("Max results (default 25)"), false)),
                (a, e) =>
                {
                    var q = ReqStr(a, "query");
                    var limit = (int?)a["limit"] ?? 25;
                    var fs = e.RequireFlowsheet();
                    var hits = fs.AvailableCompounds.Keys
                        .Where(k => k.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(k => k.Length).ThenBy(k => k)
                        .Take(limit).ToList();
                    return new JObject
                    {
                        ["matches"] = new JArray(hits.Cast<object>().ToArray()),
                        ["total_available"] = fs.AvailableCompounds.Count,
                    };
                });

            Add("add_compound",
                "Add a compound to the flowsheet by its exact database name (case-sensitive — use search_compounds " +
                "if unsure). All compounds must be added before creating reactions or setting compositions.",
                Obj(("name", Str("Exact compound name, e.g. 'Water', 'Ethanol', 'Acetic acid'"), true)),
                (a, e) =>
                {
                    var name = ReqStr(a, "name");
                    var fs = e.RequireFlowsheet();
                    if (!fs.AvailableCompounds.ContainsKey(name))
                    {
                        var near = fs.AvailableCompounds.Keys
                            .Where(k => k.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Take(5).ToList();
                        throw new ToolException("Compound '" + name + "' not found (names are case-sensitive)."
                            + (near.Count > 0 ? " Did you mean: " + string.Join(", ", near) : ""));
                    }
                    fs.AddCompound(name);
                    return "Added " + name + ". Flowsheet compounds: "
                        + string.Join(", ", fs.SelectedCompounds.Keys);
                });

            Add("list_property_packages",
                "List the available thermodynamic property package names. One must be attached with " +
                "set_property_package before solving.",
                Obj(),
                (a, e) => new JArray(e.RequireFlowsheet().GetAvailablePropertyPackages().Cast<object>().ToArray()));

            Add("set_property_package",
                "Instantiate and attach a property package by exact name from list_property_packages. " +
                "Guidance: 'Steam Tables (IAPWS-IF97)' for pure water/steam; 'Peng-Robinson (PR)' or " +
                "'Soave-Redlich-Kwong (SRK)' for hydrocarbons/gases; 'NRTL' or 'UNIQUAC' for polar/non-ideal " +
                "liquid mixtures (alcohols, acids, water+organics).",
                Obj(("name", Str("Exact package name"), true)),
                (a, e) =>
                {
                    var name = ReqStr(a, "name");
                    var fs = e.RequireFlowsheet();
                    var avail = fs.GetAvailablePropertyPackages();
                    if (!avail.Contains(name))
                    {
                        var near = avail.Where(k => k.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        throw new ToolException("Unknown property package '" + name + "'."
                            + (near.Count > 0 ? " Did you mean: " + string.Join(", ", near) : ""));
                    }
                    fs.CreateAndAddPropertyPackage(name);
                    return "Attached property package: " + name;
                });

            Add("create_reaction",
                "Create a CONVERSION reaction and add it to the default reaction set (used by ConversionReactor). " +
                "All participating compounds must already be added. Stoichiometric coefficients: negative for " +
                "reactants, positive for products. Conversion expression is % conversion of the base compound and " +
                "may use T (temperature in K), e.g. '60' or '80 - 0.01*(T-300)'.",
                Obj(("name", Str("Reaction name/ID"), true),
                    ("stoichiometry", NumMap("Compound name -> stoichiometric coefficient, e.g. {\"Ethanol\": -1, \"Acetic acid\": -1, \"Ethyl acetate\": 1, \"Water\": 1}"), true),
                    ("base_compound", Str("The compound the conversion refers to; must appear in stoichiometry"), true),
                    ("phase", Str("Reaction phase: Mixture, Vapor, Liquid or Solid"), true),
                    ("conversion_expression", Str("Percent conversion of base compound, may reference T in K"), true),
                    ("description", Str("Optional description"), false)),
                (a, e) =>
                {
                    var fs = e.RequireFlowsheet();
                    var stoich = (a["stoichiometry"] as JObject
                        ?? throw new ToolException("stoichiometry must be an object of compound -> coefficient"))
                        .Properties().ToDictionary(p => p.Name, p => (double)p.Value);
                    var baseComp = ReqStr(a, "base_compound");
                    if (!stoich.ContainsKey(baseComp))
                        throw new ToolException("base_compound '" + baseComp + "' must be a key of stoichiometry.");
                    foreach (var c in stoich.Keys)
                        if (!fs.SelectedCompounds.Keys.Any(k => k == c))
                            throw new ToolException("Compound '" + c + "' is not in the flowsheet. add_compound it first. "
                                + "Current: " + string.Join(", ", fs.SelectedCompounds.Keys));

                    var name = ReqStr(a, "name");
                    var rx = fs.CreateConversionReaction(name, (string)a["description"] ?? "",
                        stoich, baseComp, ReqStr(a, "phase"), ReqStr(a, "conversion_expression"));
                    fs.AddReaction(rx);
                    fs.AddReactionToSet(rx.ID, "DefaultSet", true, 0);
                    return "Created conversion reaction '" + name + "' and added it to DefaultSet.";
                });
        }

        // ---- topology -------------------------------------------------------

        private void RegisterTopologyTools()
        {
            Add("list_object_types",
                "List the object type names accepted by add_object.",
                Obj(),
                (a, e) => new JArray(Engine.TypeMap.Keys.OrderBy(k => k).Cast<object>().ToArray()));

            Add("add_object",
                "Add a unit operation or stream to the flowsheet. Streams are objects too: material and energy " +
                "streams must be added and then connected to unit operations.",
                Obj(("type", Str("Object type from list_object_types, e.g. MaterialStream, Pump, DistillationColumn"), true),
                    ("tag", Str("Display name, e.g. 'FEED', 'P-101'. Must be unique."), true),
                    ("x", Int("Optional canvas X (default auto)"), false),
                    ("y", Int("Optional canvas Y (default auto)"), false)),
                (a, e) =>
                {
                    var typeName = ReqStr(a, "type");
                    if (!Engine.TypeMap.TryGetValue(typeName, out var otype))
                        throw new ToolException("Unknown type '" + typeName + "'. Valid: "
                            + string.Join(", ", Engine.TypeMap.Keys.OrderBy(k => k)));
                    var tag = ReqStr(a, "tag");
                    var fs = e.RequireFlowsheet();
                    if (fs.GetFlowsheetSimulationObject(tag) != null)
                        throw new ToolException("An object tagged '" + tag + "' already exists.");
                    var x = (int?)a["x"] ?? (50 + 60 * (fs.SimulationObjects.Count % 8));
                    var y = (int?)a["y"] ?? (50 + 70 * (fs.SimulationObjects.Count / 8));
                    var obj = fs.AddObject(otype, x, y, tag);
                    return "Added " + typeName + " '" + tag + "' (" + obj.GetType().Name + ").";
                });

            Add("connect_objects",
                "Connect two objects. One side must be a stream (material or energy). Direction matters: " +
                "from=stream, to=unit connects the stream as a FEED; from=unit, to=stream connects it as a PRODUCT. " +
                "Ports: -1 (default) auto-picks the first free suitable port. Convention: on pumps, heaters, coolers " +
                "and reactors, input port 0 is the material inlet and input port 1 is the energy-stream port. " +
                "Use get_ports to inspect an object's ports. For column feed/distillate/bottoms/duty connections " +
                "use connect_column_stream instead.",
                Obj(("from_tag", Str("Tag of the upstream object"), true),
                    ("to_tag", Str("Tag of the downstream object"), true),
                    ("from_port", Int("Output port index on from-object (-1 = auto)"), false),
                    ("to_port", Int("Input port index on to-object (-1 = auto)"), false)),
                (a, e) =>
                {
                    var from = e.GetByTag(ReqStr(a, "from_tag"));
                    var to = e.GetByTag(ReqStr(a, "to_tag"));
                    var fp = (int?)a["from_port"] ?? -1;
                    var tp = (int?)a["to_port"] ?? -1;
                    e.RequireFlowsheet().ConnectObjects(from.GraphicObject, to.GraphicObject, fp, tp);
                    return "Connected " + from.GraphicObject.Tag + " -> " + to.GraphicObject.Tag + ".";
                });

            Add("disconnect_objects",
                "Remove the connection between two objects.",
                Obj(("from_tag", Str("Tag of the upstream object"), true),
                    ("to_tag", Str("Tag of the downstream object"), true)),
                (a, e) =>
                {
                    var from = e.GetByTag(ReqStr(a, "from_tag"));
                    var to = e.GetByTag(ReqStr(a, "to_tag"));
                    e.RequireFlowsheet().DisconnectObjects(from.GraphicObject, to.GraphicObject);
                    return "Disconnected " + from.GraphicObject.Tag + " -> " + to.GraphicObject.Tag + ".";
                });

            Add("get_ports",
                "List an object's connection ports: index, name, type (ConIn=material in, ConOut=material out, " +
                "ConEn=energy) and whether each is already connected.",
                Obj(("tag", Str("Object tag"), true)),
                (a, e) =>
                {
                    var obj = e.GetByTag(ReqStr(a, "tag"));
                    return new JArray(obj.GetConnectionPortsList().Cast<object>().ToArray());
                });

            Add("remove_object",
                "Delete an object from the flowsheet by tag (also severs its connections).",
                Obj(("tag", Str("Object tag"), true)),
                (a, e) =>
                {
                    var tag = ReqStr(a, "tag");
                    e.GetByTag(tag); // validate + nice error with existing tags
                    var fs = e.RequireFlowsheet();
                    var m = fs.GetType().GetMethod("DeleteObject",
                        new[] { typeof(string), typeof(bool) });
                    if (m == null)
                        throw new ToolException("DeleteObject API not found on this flowsheet implementation.");
                    m.Invoke(fs, new object[] { tag, false });
                    return "Removed '" + tag + "'.";
                });

            Add("auto_layout",
                "Automatically arrange the flowsheet graphics. Call before save_flowsheet so the diagram is tidy in the GUI.",
                Obj(),
                (a, e) => { e.RequireFlowsheet().AutoLayout(); return "Layout arranged."; });
        }
    }
}
