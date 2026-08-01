using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.Interfaces;
using DWSIM.Thermodynamics.Streams;

namespace DwsimMcp
{
    // solve / inspection / run_script.
    public sealed partial class ToolRegistry
    {
        private void RegisterExecutionTools()
        {
            Add("solve",
                "Run the flowsheet solver. Returns the solver's error list VERBATIM plus per-object calculated " +
                "flags. IMPORTANT: an empty errors array means converged; if errors is non-empty the result values " +
                "are NOT trustworthy even though numbers may be present. Convergence tips: for distillation of " +
                "non-ideal/azeotropic mixtures switch the column solver to Napthali-Sandholm via configure_column; " +
                "check that product-flow specs are consistent with the feed and volatility order.",
                Obj(("timeout_seconds", Int("Optional solver timeout in seconds (default 3600)"), false)),
                (a, e) =>
                {
                    var errors = e.Solve((int?)a["timeout_seconds"]);
                    var fs = e.RequireFlowsheet();
                    var calc = new JObject();
                    foreach (var o in fs.SimulationObjects.Values.OrderBy(x => x.GraphicObject?.Tag))
                        calc[o.GraphicObject?.Tag ?? o.Name] = o.GraphicObject?.Calculated ?? false;
                    return new JObject
                    {
                        ["converged"] = errors == null || errors.Count == 0,
                        ["errors"] = new JArray((errors ?? new List<Exception>())
                            .Select(x => (object)(x.InnerException?.Message ?? x.Message)).ToArray()),
                        ["calculated"] = calc,
                    };
                });

            Add("get_stream",
                "Full state of a material stream: T, P, flows, enthalpy, vapor fraction, overall composition " +
                "(mole fractions + molar flows), and per-phase compositions where present. For an energy stream " +
                "returns its power in kW.",
                Obj(("tag", Str("Stream tag"), true)),
                (a, e) =>
                {
                    var obj = e.GetByTag(ReqStr(a, "tag"));
                    if (obj is DWSIM.UnitOperations.Streams.EnergyStream es)
                        return new JObject { ["type"] = "EnergyStream", ["energy_flow_kW"] = es.GetEnergyFlow() };
                    if (!(obj is MaterialStream ms))
                        throw new ToolException("'" + obj.GraphicObject.Tag + "' is a " + obj.GetType().Name
                            + ", not a stream. Use get_object for unit operations.");
                    return StreamJson(ms);
                });

            Add("get_object",
                "Snapshot of a unit operation's public scalar properties (reflection). Use to inspect results " +
                "(DeltaQ = duty/power in kW on most units) or to discover property names for set_object_property.",
                Obj(("tag", Str("Object tag"), true)),
                (a, e) =>
                {
                    var obj = e.GetByTag(ReqStr(a, "tag"));
                    return new JObject
                    {
                        ["tag"] = obj.GraphicObject?.Tag,
                        ["type"] = obj.GetType().FullName,
                        ["calculated"] = obj.GraphicObject?.Calculated ?? false,
                        ["properties"] = Reflect.DumpProperties(obj),
                    };
                });

            Add("get_results",
                "Compact results table for the whole flowsheet: every material stream (T, P, flows), every energy " +
                "stream (kW), and every unit operation's key numbers. Call after solve.",
                Obj(),
                (a, e) =>
                {
                    var fs = e.RequireFlowsheet();
                    var streams = new JArray();
                    var energy = new JArray();
                    var units = new JArray();
                    foreach (var o in fs.SimulationObjects.Values.OrderBy(x => x.GraphicObject?.Tag))
                    {
                        var tag = o.GraphicObject?.Tag;
                        if (o is MaterialStream ms)
                        {
                            streams.Add(new JObject
                            {
                                ["tag"] = tag,
                                ["T_K"] = Round(ms.GetTemperature()),
                                ["P_Pa"] = Round(ms.GetPressure()),
                                ["mass_flow_kg_s"] = Round(ms.GetMassFlow()),
                                ["molar_flow_mol_s"] = Round(ms.GetMolarFlow()),
                                ["mole_fractions"] = Composition(ms),
                            });
                        }
                        else if (o is DWSIM.UnitOperations.Streams.EnergyStream es)
                        {
                            energy.Add(new JObject { ["tag"] = tag, ["energy_flow_kW"] = Round(es.GetEnergyFlow()) });
                        }
                        else
                        {
                            var u = new JObject
                            {
                                ["tag"] = tag,
                                ["type"] = o.GetType().Name,
                                ["calculated"] = o.GraphicObject?.Calculated ?? false,
                            };
                            foreach (var pname in new[] { "DeltaQ", "DeltaP", "DeltaT", "Eficiencia",
                                                          "AdiabaticEfficiency", "OutletTemperature" })
                            {
                                var p = Reflect.FindProperty(o, pname);
                                if (p == null || !p.CanRead) continue;
                                object v;
                                try { v = p.GetValue(o); } catch { continue; }
                                if (v is double d) u[pname] = Round(d);
                                else if (v != null && Nullable.GetUnderlyingType(p.PropertyType) == typeof(double))
                                    u[pname] = Round((double)v);
                            }
                            units.Add(u);
                        }
                    }
                    return new JObject
                    {
                        ["material_streams"] = streams,
                        ["energy_streams"] = energy,
                        ["unit_operations"] = units,
                    };
                });

            Add("run_script",
                "ESCAPE HATCH: compile and run a C# snippet against the live flowsheet. The snippet is the BODY of " +
                "public static object Run(DWSIM.Interfaces.IFlowsheet sim) — return a value to see it. Full access " +
                "to DWSIM's API; common namespaces are pre-imported (DWSIM.Interfaces, " +
                "DWSIM.Thermodynamics.Streams, DWSIM.UnitOperations.UnitOperations, DWSIM.UnitOperations.Reactors, " +
                "System.Linq). Example: var s=(MaterialStream)sim.GetFlowsheetSimulationObject(\"FEED\"); " +
                "return s.GetTemperature(); Use only when named tools cannot express the operation.",
                Obj(("code", Str("C# statements forming the body of Run(IFlowsheet sim)"), true)),
                (a, e) => RunScript(ReqStr(a, "code"), e));
        }

        private static JToken Round(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(Math.Round(v, 6));

        private static JObject Composition(MaterialStream ms)
        {
            var comp = new JObject();
            foreach (var c in ms.Phases[0].Compounds.Values)
                comp[c.Name] = Round(c.MoleFraction.GetValueOrDefault());
            return comp;
        }

        private static JObject StreamJson(MaterialStream ms)
        {
            var overall = new JObject();
            var flows = new JObject();
            foreach (var c in ms.Phases[0].Compounds.Values)
            {
                overall[c.Name] = Round(c.MoleFraction.GetValueOrDefault());
                flows[c.Name] = Round(c.MolarFlow.GetValueOrDefault());
            }

            var phases = new JObject();
            var phaseNames = new Dictionary<int, string>
            { [1] = "overall_liquid", [2] = "vapor", [3] = "liquid1", [4] = "liquid2", [7] = "solid" };
            foreach (var kv in phaseNames)
            {
                if (!ms.Phases.ContainsKey(kv.Key)) continue;
                var ph = ms.Phases[kv.Key];
                var frac = ph.Properties.molarfraction.GetValueOrDefault();
                if (frac <= 0) continue;
                var pcomp = new JObject();
                foreach (var c in ph.Compounds.Values)
                    pcomp[c.Name] = Round(c.MoleFraction.GetValueOrDefault());
                phases[kv.Value] = new JObject
                {
                    ["molar_fraction"] = Round(frac),
                    ["mole_fractions"] = pcomp,
                };
            }

            return new JObject
            {
                ["tag"] = ms.GraphicObject?.Tag,
                ["calculated"] = ms.GraphicObject?.Calculated ?? false,
                ["T_K"] = Round(ms.GetTemperature()),
                ["P_Pa"] = Round(ms.GetPressure()),
                ["mass_flow_kg_s"] = Round(ms.GetMassFlow()),
                ["molar_flow_mol_s"] = Round(ms.GetMolarFlow()),
                ["mass_enthalpy_kJ_kg"] = Round(ms.GetMassEnthalpy()),
                ["vapor_molar_fraction"] = Round(ms.Phases.ContainsKey(2)
                    ? ms.Phases[2].Properties.molarfraction.GetValueOrDefault() : 0),
                ["overall_mole_fractions"] = overall,
                ["compound_molar_flows_mol_s"] = flows,
                ["phases"] = phases,
            };
        }

        // ---- run_script -----------------------------------------------------

        private static int _scriptCounter;

        private static object RunScript(string code, Engine e)
        {
            var fs = e.RequireFlowsheet();
            var lib = AppContext.BaseDirectory.TrimEnd('/');
            var n = System.Threading.Interlocked.Increment(ref _scriptCounter);
            // Per-process work dir: Claude Desktop and Claude Code each run
            // their own server, and a shared dir with counter-based names
            // would collide between them.
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var work = Path.Combine(Path.GetTempPath(), "dwsim-mcp-scripts", "pid" + pid);
            Directory.CreateDirectory(work);
            var src = Path.Combine(work, "snippet" + n + ".cs");
            var dll = Path.Combine(work, "snippet" + n + ".dll");

            File.WriteAllText(src, @"
using System;
using System.Collections.Generic;
using System.Linq;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.Thermodynamics.Streams;
using DWSIM.UnitOperations.Streams;
using DWSIM.UnitOperations.UnitOperations;
using DWSIM.UnitOperations.Reactors;

public static class DwsimMcpSnippet" + n + @"
{
    public static object Run(DWSIM.Interfaces.IFlowsheet sim)
    {
" + code + @"
    ;
    return null;
    }
}");

            // Reference the same assemblies the server itself was built against.
            // Quote paths — the workspace location is user-chosen and may
            // contain spaces.
            var refs = Directory.GetFiles(lib, "*.dll")
                .Where(f => IsManaged(f))
                .Select(f => "-reference:\"" + f + "\"")
                .ToList();
            var rsp = Path.Combine(work, "snippet" + n + ".rsp");
            File.WriteAllLines(rsp, new[] { "-nologo", "-target:library", "-out:\"" + dll + "\"",
                "-nowarn:CS1701,CS1702,CS0162" }.Concat(refs).Concat(new[] { "\"" + src + "\"" }));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = FindCsc(),
                Arguments = "@" + rsp,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                // Async reads on both pipes — sequential ReadToEnd can deadlock
                // if the unread pipe's buffer fills.
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(120000))
                {
                    try { proc.Kill(); } catch { }
                    throw new ToolException("Script compilation timed out (120 s).");
                }
                var output = outTask.Result + errTask.Result;
                if (proc.ExitCode != 0)
                {
                    var errs = output.Split('\n').Where(l => l.Contains(": error ")).Take(10);
                    throw new ToolException("Script compile failed:\n" + string.Join("\n", errs));
                }
            }

            var asm = System.Reflection.Assembly.LoadFrom(dll);
            var method = asm.GetType("DwsimMcpSnippet" + n)?.GetMethod("Run")
                ?? throw new ToolException("Compiled snippet did not expose Run().");
            try
            {
                var result = method.Invoke(null, new object[] { fs });
                return new JObject { ["result"] = Reflect.Render(result) };
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                throw new ToolException("Script threw: " + (tie.InnerException?.ToString() ?? tie.Message));
            }
        }

        // Mono installs csc under /opt/homebrew (Apple Silicon brew),
        // /usr/local (Intel brew), or the official framework path.
        private static string FindCsc()
        {
            var env = Environment.GetEnvironmentVariable("DWSIM_MCP_CSC");
            var candidates = new[]
            {
                env,
                "/opt/homebrew/bin/csc",
                "/usr/local/bin/csc",
                "/Library/Frameworks/Mono.framework/Versions/Current/Commands/csc",
            };
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    return c;
            throw new ToolException(
                "run_script needs mono's csc compiler and none was found. Install mono "
                + "(brew install mono) or set DWSIM_MCP_CSC to the csc path.");
        }

        private static bool IsManaged(string path)
        {
            try
            {
                using (var fh = File.OpenRead(path))
                {
                    var buf = new byte[Math.Min(fh.Length, 4 * 1024 * 1024)];
                    fh.Read(buf, 0, buf.Length);
                    for (int i = 0; i < buf.Length - 3; i++)
                        if (buf[i] == (byte)'B' && buf[i + 1] == (byte)'S' && buf[i + 2] == (byte)'J' && buf[i + 3] == (byte)'B')
                            return true;
                }
            }
            catch { }
            return false;
        }
    }
}
