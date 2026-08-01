using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.Interfaces;
using DWSIM.Thermodynamics.Streams;
using DWSIM.UnitOperations.UnitOperations;

namespace DwsimMcp
{
    // Conditions, execution, inspection, and the escape hatches.
    public sealed partial class ToolRegistry
    {
        // Canonical column spec names -> the exact string DWSIM's
        // SetCondenserSpec/SetReboilerSpec parse correctly. DWSIM's own parser
        // silently falls back to Heat_Duty on a misspelled spec, so we validate
        // here instead of letting that happen.
        private static readonly Dictionary<string, string> ColumnSpecs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["heat duty"] = "Heat Duty",
                ["product molar flow rate"] = "Product Molar Flow Rate",
                ["component molar flow rate"] = "Component Molar Flow Rate",
                ["product mass flow rate"] = "Product Mass Flow Rate",
                ["component mass flow rate"] = "Component Mass Flow Rate",
                ["component fraction"] = "Component Fraction",
                ["component recovery"] = "Component Recovery",
                ["stream ratio"] = "Stream Ratio",
                ["reflux ratio"] = "Reflux Ratio",       // alias handled by DWSIM (condenser)
                ["boilup ratio"] = "Boilup Ratio",       // alias handled by DWSIM (reboiler)
                ["temperature"] = "Temperature",
                ["feed recovery"] = "Feed Recovery",
            };

        private static readonly string[] ColumnSolvers =
        {
            "Wang-Henke (Bubble Point)",
            "Modified Wang-Henke (Bubble Point)",
            "Napthali-Sandholm (Simultaneous Correction)",
        };

        private void RegisterConditionTools()
        {
            Add("set_stream",
                "Set conditions on a material stream. SI units: temperature K, pressure Pa, mass_flow kg/s, " +
                "molar_flow mol/s. Composition: either compound_molar_flows (mol/s per compound — also sets total) " +
                "or mole_fractions (requires molar_flow set in this call or earlier). Unspecified values keep " +
                "their defaults (298.15 K, 101325 Pa, 1 kg/s).",
                Obj(("tag", Str("Material stream tag"), true),
                    ("temperature", Num("Temperature in K"), false),
                    ("pressure", Num("Pressure in Pa"), false),
                    ("mass_flow", Num("Mass flow in kg/s"), false),
                    ("molar_flow", Num("Molar flow in mol/s"), false),
                    ("compound_molar_flows", NumMap("Compound name -> molar flow in mol/s"), false),
                    ("mole_fractions", NumMap("Compound name -> overall mole fraction (should sum to 1)"), false),
                    ("flash_spec", Str("Optional flash specification: PT (default), PH, PS, PVF, TVF"), false)),
                (a, e) =>
                {
                    // Treat explicit JSON nulls as absent — a null token passes
                    // `!= null` checks and would throw mid-application.
                    foreach (var nul in a.Properties().Where(p => p.Value.Type == JTokenType.Null).ToList())
                        nul.Remove();
                    var obj = e.GetByTag(ReqStr(a, "tag"));
                    if (!(obj is MaterialStream ms))
                        throw new ToolException("'" + obj.GraphicObject.Tag + "' is a " + obj.GetType().Name
                            + ", not a material stream.");
                    var fs = e.RequireFlowsheet();
                    var applied = new List<string>();

                    if (a["temperature"] != null) { ms.SetTemperature((double)a["temperature"]); applied.Add("T=" + a["temperature"] + " K"); }
                    if (a["pressure"] != null) { ms.SetPressure((double)a["pressure"]); applied.Add("P=" + a["pressure"] + " Pa"); }
                    if (a["mass_flow"] != null) { ms.SetMassFlow((double)a["mass_flow"]); applied.Add("W=" + a["mass_flow"] + " kg/s"); }
                    if (a["molar_flow"] != null) { ms.SetMolarFlow((double)a["molar_flow"]); applied.Add("F=" + a["molar_flow"] + " mol/s"); }

                    // Validate compound names BEFORE mutating anything — a bad
                    // name mid-loop would leave the stream half-updated.
                    void CheckCompounds(JObject map)
                    {
                        foreach (var p in map.Properties())
                            if (!fs.SelectedCompounds.Keys.Any(k => k == p.Name))
                                throw new ToolException("Compound '" + p.Name + "' is not in the flowsheet "
                                    + "(nothing was changed). Current: "
                                    + string.Join(", ", fs.SelectedCompounds.Keys));
                    }

                    if (a["compound_molar_flows"] is JObject cmf)
                    {
                        CheckCompounds(cmf);
                        foreach (var p in cmf.Properties())
                            ms.SetOverallCompoundMolarFlow(p.Name, (double)p.Value);
                        applied.Add("compound molar flows set (" + cmf.Count + " compounds)");
                    }
                    else if (a["mole_fractions"] is JObject mf)
                    {
                        // The total must come from THIS call: a molar flow DWSIM
                        // derived from a mass spec uses the OLD composition's
                        // molecular weight and silently distorts the result.
                        if (a["molar_flow"] == null)
                            throw new ToolException("mole_fractions requires molar_flow in the same call "
                                + "(a stored molar flow may be derived from the previous composition). "
                                + "Alternatively use compound_molar_flows.");
                        var total = (double)a["molar_flow"];
                        if (total <= 0)
                            throw new ToolException("molar_flow must be positive when setting mole_fractions.");
                        var sum = mf.Properties().Sum(p => (double)p.Value);
                        if (Math.Abs(sum - 1.0) > 0.02)
                            throw new ToolException("mole_fractions sum to " + sum.ToString("F3") + "; they must sum to 1.");
                        CheckCompounds(mf);
                        foreach (var p in mf.Properties())
                            ms.SetOverallCompoundMolarFlow(p.Name, (double)p.Value / sum * total);
                        applied.Add("composition set from mole fractions");
                    }

                    if (a["flash_spec"] != null)
                    {
                        var spec = (string)a["flash_spec"];
                        var valid = new[] { "PT", "TP", "PH", "PS", "PVF", "TVF", "PSF", "VT", "VH", "VS" };
                        if (!valid.Contains(spec.ToUpperInvariant()))
                            throw new ToolException("flash_spec must be one of: " + string.Join(", ", valid));
                        ms.SetFlashSpec(spec);
                        applied.Add("flash spec " + spec);
                    }

                    return "Set on '" + ms.GraphicObject.Tag + "': " + string.Join(", ", applied)
                         + ". Re-run solve for results.";
                });

            Add("connect_column_stream",
                "Connect a stream to a distillation/absorption column in a specific ROLE. Roles: feed (needs stage, " +
                "0-based where 0 = condenser and the last stage = reboiler), distillate, bottoms, condenser_duty " +
                "(energy stream), reboiler_duty (energy stream), top_product (absorption columns). " +
                "Use this instead of connect_objects for columns — duty connections have non-obvious directions.",
                Obj(("column_tag", Str("Column tag"), true),
                    ("stream_tag", Str("Stream tag"), true),
                    ("role", Str("feed | distillate | bottoms | condenser_duty | reboiler_duty | top_product"), true),
                    ("stage", Int("Feed stage, 0-based (feed role only). E.g. 10 on a 20-stage column"), false)),
                (a, e) =>
                {
                    var col = e.GetByTag(ReqStr(a, "column_tag"));
                    var stream = e.GetByTag(ReqStr(a, "stream_tag"));
                    var role = ReqStr(a, "role").ToLowerInvariant();

                    void Call(string method, params object[] args)
                    {
                        var types = args.Select(x => x is int ? typeof(int) : typeof(ISimulationObject)).ToArray();
                        var m = col.GetType().GetMethod(method, types)
                            ?? throw new ToolException(col.GetType().Name + " has no " + method
                                + " — check the column type (e.g. absorption columns have no condenser).");
                        try { m.Invoke(col, args); }
                        catch (System.Reflection.TargetInvocationException tie)
                        {
                            throw new ToolException(method + " failed: " + tie.InnerException?.Message);
                        }
                    }

                    switch (role)
                    {
                        case "feed":
                            var stage = (int?)a["stage"]
                                ?? throw new ToolException("role 'feed' requires a stage (0-based).");
                            Call("ConnectFeed", stream, stage);
                            return "Connected '" + stream.GraphicObject.Tag + "' as feed on stage " + stage + ".";
                        case "distillate": Call("ConnectDistillate", stream); break;
                        case "bottoms": Call("ConnectBottoms", stream); break;
                        case "condenser_duty": Call("ConnectCondenserDuty", stream); break;
                        case "reboiler_duty": Call("ConnectReboilerDuty", stream); break;
                        case "top_product": Call("ConnectTopProduct", stream); break;
                        default:
                            throw new ToolException("Unknown role '" + role
                                + "'. Valid: feed, distillate, bottoms, condenser_duty, reboiler_duty, top_product.");
                    }
                    return "Connected '" + stream.GraphicObject.Tag + "' as " + role + " on '"
                         + col.GraphicObject.Tag + "'.";
                });

            Add("configure_column",
                "Configure a distillation column: stage count, specs, pressures, solver. Specs (choose one per end): "
                + string.Join(", ", ColumnSpecs.Values.Distinct()) + ". 'Reflux Ratio' is the usual condenser spec; "
                + "'Product Molar Flow Rate' (mol/s) the usual reboiler spec. Solver methods: "
                + string.Join(" | ", ColumnSolvers) + " — Napthali-Sandholm is the robust choice for non-ideal "
                + "or azeotropic mixtures; the default Wang-Henke often fails to converge on those.",
                Obj(("tag", Str("Column tag"), true),
                    ("stages", Int("Total number of stages incl. condenser and reboiler (min 4)"), false),
                    ("top_pressure", Num("Condenser/top-stage pressure in Pa"), false),
                    ("pressure_drop", Num("Total column pressure drop in Pa (linear profile)"), false),
                    ("condenser_spec", Obj(("type", Str("Spec name, e.g. 'Reflux Ratio'"), true),
                                           ("value", Num("Spec value"), true),
                                           ("unit", Str("Unit for flow/temperature specs, e.g. 'mol/s', 'K'"), false),
                                           ("compound", Str("Compound name for component specs"), false)), false),
                    ("reboiler_spec", Obj(("type", Str("Spec name, e.g. 'Product Molar Flow Rate'"), true),
                                          ("value", Num("Spec value"), true),
                                          ("unit", Str("Unit, e.g. 'mol/s'"), false),
                                          ("compound", Str("Compound name for component specs"), false)), false),
                    ("solver_method", Str("One of the solver methods listed above"), false),
                    ("max_iterations", Int("Solver iteration cap (default 100)"), false)),
                (a, e) =>
                {
                    var obj = e.GetByTag(ReqStr(a, "tag"));
                    if (!(obj is DistillationColumn col))
                        throw new ToolException("'" + obj.GraphicObject.Tag + "' is a " + obj.GetType().Name
                            + "; configure_column supports DistillationColumn.");
                    var applied = new List<string>();

                    if (a["stages"] != null)
                    {
                        var n = (int)a["stages"];
                        if (n < 4) throw new ToolException("DWSIM requires more than 3 stages.");
                        col.SetNumberOfStages(n);
                        applied.Add("stages=" + n);
                    }
                    if (a["top_pressure"] != null) { col.SetTopPressure((double)a["top_pressure"]); applied.Add("Ptop=" + a["top_pressure"] + " Pa"); }
                    if (a["pressure_drop"] != null) { col.ColumnPressureDrop = (double)a["pressure_drop"]; applied.Add("dP=" + a["pressure_drop"] + " Pa"); }

                    string Canon(string s) =>
                        ColumnSpecs.TryGetValue(s.Trim(), out var c)
                            ? c
                            : throw new ToolException("Unknown column spec '" + s + "'. Valid: "
                                + string.Join(", ", ColumnSpecs.Values.Distinct()));

                    if (a["condenser_spec"] is JObject cs)
                    {
                        col.SetCondenserSpec(Canon((string)cs["type"]), (double)cs["value"],
                            (string)cs["unit"] ?? "", (string)cs["compound"] ?? "");
                        applied.Add("condenser: " + cs["type"] + "=" + cs["value"]);
                    }
                    if (a["reboiler_spec"] is JObject rs)
                    {
                        col.SetReboilerSpec(Canon((string)rs["type"]), (double)rs["value"],
                            (string)rs["unit"] ?? "", (string)rs["compound"] ?? "");
                        applied.Add("reboiler: " + rs["type"] + "=" + rs["value"]);
                    }
                    if (a["solver_method"] != null)
                    {
                        var sm = (string)a["solver_method"];
                        var match = ColumnSolvers.FirstOrDefault(
                            s => s.IndexOf(sm, StringComparison.OrdinalIgnoreCase) >= 0
                              || sm.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                            ?? throw new ToolException("Unknown solver '" + sm + "'. Valid: " + string.Join(" | ", ColumnSolvers));
                        col.SolvingMethodName = match;
                        applied.Add("solver=" + match);
                    }
                    if (a["max_iterations"] != null) { col.MaxIterations = (int)a["max_iterations"]; applied.Add("maxiter=" + a["max_iterations"]); }

                    return "Configured '" + col.GraphicObject.Tag + "': " + string.Join("; ", applied);
                });

            Add("set_object_property",
                "Set any public property on a unit operation by name (reflection). Examples: Pump: CalcMode " +
                "(Delta_P|OutletPressure|Power), Pout (Pa), Eficiencia (percent); Compressor: POut (note capital O), " +
                "AdiabaticEfficiency; Heater: CalcMode (HeatAdded|OutletTemperature), OutletTemperature (K), DeltaQ (kW); " +
                "Valve: CalcMode, OutletPressure (Pa); Reactors: ReactorOperationMode (Isothermic|Adiabatic|" +
                "OutletTemperature), DeltaP (Pa); HeatExchanger: CalculationMode, Area (m2), OverallCoefficient. " +
                "Enums accept their member name as a string. Special case: 'Ratios' on a Splitter accepts an array. " +
                "Use get_object first to see current values.",
                Obj(("tag", Str("Object tag"), true),
                    ("property", Str("Property name (case-insensitive)"), true),
                    ("value", new JObject { ["description"] = "New value: number, string, boolean, or array for Ratios" }, true)),
                (a, e) =>
                {
                    var obj = e.GetByTag(ReqStr(a, "tag"));
                    var prop = ReqStr(a, "property");
                    var value = a["value"] ?? throw new ToolException("Missing required argument: value");

                    // Splitter.Ratios is a read-only property over a mutable ArrayList.
                    if (prop.Equals("Ratios", StringComparison.OrdinalIgnoreCase) && value is JArray ratios)
                    {
                        var rp = Reflect.FindProperty(obj, "Ratios")
                            ?? throw new ToolException(obj.GetType().Name + " has no Ratios property.");
                        if (!(rp.GetValue(obj) is System.Collections.ArrayList list))
                            throw new ToolException("Ratios is not an ArrayList on " + obj.GetType().Name);
                        var vals = ratios.Select(x => (double)x).ToList();
                        if (Math.Abs(vals.Sum() - 1.0) > 1e-6)
                            throw new ToolException("Split ratios must sum to 1 (got " + vals.Sum() + ").");
                        for (int i = 0; i < list.Count; i++)
                            list[i] = i < vals.Count ? vals[i] : 0.0;
                        return "Set split ratios on '" + obj.GraphicObject.Tag + "' to ["
                             + string.Join(", ", vals) + "].";
                    }

                    Reflect.SetProperty(obj, prop, value);
                    return "Set " + obj.GraphicObject.Tag + "." + prop + " = "
                         + value.ToString(Newtonsoft.Json.Formatting.None);
                });

            Add("call_object_method",
                "Call any public method on a flowsheet object by name (reflection escape hatch). Arguments are " +
                "matched positionally; strings that equal an existing object tag are passed as that object. " +
                "Example: {tag: 'T-101', method: 'SetNumberOfStages', args: [25]}.",
                Obj(("tag", Str("Object tag, or 'flowsheet' to target the flowsheet itself"), true),
                    ("method", Str("Method name (case-insensitive)"), true),
                    ("args", new JObject { ["type"] = "array", ["description"] = "Positional arguments" }, false)),
                (a, e) =>
                {
                    var tag = ReqStr(a, "tag");
                    object target = tag.Equals("flowsheet", StringComparison.OrdinalIgnoreCase)
                        ? (object)e.RequireFlowsheet() : e.GetByTag(tag);
                    var args = a["args"] as JArray ?? new JArray();

                    // Resolve tag-string args to simulation objects where a tag matches.
                    var resolved = new JArray();
                    var objArgs = new Dictionary<int, object>();
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (args[i].Type == JTokenType.String)
                        {
                            var maybe = e.Flowsheet?.GetFlowsheetSimulationObject((string)args[i]);
                            if (maybe != null) { objArgs[i] = maybe; resolved.Add(args[i]); continue; }
                        }
                        resolved.Add(args[i]);
                    }

                    object result = objArgs.Count == 0
                        ? Reflect.CallMethod(target, ReqStr(a, "method"), resolved)
                        : CallWithObjects(target, ReqStr(a, "method"), resolved, objArgs);
                    return new JObject { ["result"] = Reflect.Render(result) };
                });
        }

        // Overload matching when some positional args are live simulation objects.
        private static object CallWithObjects(object target, string name, JArray args,
                                              Dictionary<int, object> objArgs)
        {
            var methods = target.GetType().GetMethods()
                .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                         && m.GetParameters().Length >= args.Count
                         && m.GetParameters().Count(p => !p.IsOptional) <= args.Count)
                .ToList();
            if (methods.Count == 0)
                throw new ToolException("No method '" + name + "' taking " + args.Count
                    + " argument(s) on " + target.GetType().Name + ".");
            Exception last = null;
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                var vals = new object[ps.Length];
                try
                {
                    for (int i = 0; i < ps.Length; i++)
                        vals[i] = i >= args.Count ? ps[i].DefaultValue
                                : objArgs.TryGetValue(i, out var o) && ps[i].ParameterType.IsInstanceOfType(o) ? o
                                : Reflect.Coerce(args[i], ps[i].ParameterType);
                }
                catch (Exception ex) { last = ex; continue; }
                try { return m.Invoke(target, vals); }
                catch (System.Reflection.TargetInvocationException tie)
                {
                    throw new ToolException(name + " failed: " + (tie.InnerException?.Message ?? tie.Message));
                }
            }
            throw new ToolException("Arguments did not match any overload of '" + name + "': "
                + (last?.Message ?? "type mismatch"));
        }
    }
}
