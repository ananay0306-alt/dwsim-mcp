using System;
using System.Collections.Generic;
using System.Linq;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;

namespace DwsimMcp
{
    // Owns the Automation3 instance and the single live flowsheet.
    // Construction is expensive (compound/property-package DB load), which is
    // why McpServer defers it to the first tool call.
    public sealed class Engine
    {
        private readonly Logger _log;
        public DWSIM.Automation.Automation3 Interf { get; }
        public IFlowsheet Flowsheet { get; private set; }

        public Engine(Logger log)
        {
            _log = log;
            Interf = new DWSIM.Automation.Automation3();
        }

        public IFlowsheet RequireFlowsheet()
        {
            if (Flowsheet == null)
                throw new ToolException("No flowsheet is open. Call create_flowsheet or load_flowsheet first.");
            return Flowsheet;
        }

        public void NewFlowsheet()
        {
            Flowsheet = Interf.CreateFlowsheet();
        }

        public void Load(string path)
        {
            if (!System.IO.File.Exists(path))
                throw new ToolException("File not found: " + path);
            Flowsheet = Interf.LoadFlowsheet(path);
        }

        public void Save(string path)
        {
            var fs = RequireFlowsheet();
            // Automation3 routes by file extension; 'compressed' is ignored there.
            Interf.SaveFlowsheet(fs, path, path.EndsWith("z", StringComparison.OrdinalIgnoreCase));
        }

        public List<Exception> Solve(int? timeoutSeconds)
        {
            var fs = RequireFlowsheet();
            var iface = (DWSIM.Automation.AutomationInterface)Interf;
            return timeoutSeconds.HasValue
                ? iface.CalculateFlowsheet3(fs, timeoutSeconds.Value)
                : iface.CalculateFlowsheet2(fs);
        }

        public ISimulationObject GetByTag(string tag)
        {
            var fs = RequireFlowsheet();
            var obj = fs.GetFlowsheetSimulationObject(tag);
            if (obj != null) return obj;

            var known = fs.SimulationObjects.Values
                .Select(o => o.GraphicObject?.Tag)
                .Where(t => !string.IsNullOrEmpty(t))
                .OrderBy(t => t)
                .ToList();
            throw new ToolException("No object with tag '" + tag + "'. Existing tags: "
                + (known.Count > 0 ? string.Join(", ", known) : "(none)"));
        }

        // Curated type-name map for add_object. Keys are what the model types;
        // values are the ObjectType enum members (Mixer/Splitter are NodeIn/NodeOut
        // in DWSIM's enum, which nobody would guess).
        public static readonly Dictionary<string, ObjectType> TypeMap =
            new Dictionary<string, ObjectType>(StringComparer.OrdinalIgnoreCase)
            {
                ["MaterialStream"] = ObjectType.MaterialStream,
                ["EnergyStream"] = ObjectType.EnergyStream,
                ["Mixer"] = ObjectType.NodeIn,
                ["Splitter"] = ObjectType.NodeOut,
                ["Pump"] = ObjectType.Pump,
                ["Compressor"] = ObjectType.Compressor,
                ["Expander"] = ObjectType.Expander,
                ["Heater"] = ObjectType.Heater,
                ["Cooler"] = ObjectType.Cooler,
                ["Valve"] = ObjectType.Valve,
                ["Pipe"] = ObjectType.Pipe,
                ["Separator"] = ObjectType.Vessel,
                ["Tank"] = ObjectType.Tank,
                ["HeatExchanger"] = ObjectType.HeatExchanger,
                ["ComponentSeparator"] = ObjectType.ComponentSeparator,
                ["SolidSeparator"] = ObjectType.SolidSeparator,
                ["Filter"] = ObjectType.Filter,
                ["OrificePlate"] = ObjectType.OrificePlate,
                ["ConversionReactor"] = ObjectType.RCT_Conversion,
                ["EquilibriumReactor"] = ObjectType.RCT_Equilibrium,
                ["GibbsReactor"] = ObjectType.RCT_Gibbs,
                ["CSTR"] = ObjectType.RCT_CSTR,
                ["PFR"] = ObjectType.RCT_PFR,
                ["DistillationColumn"] = ObjectType.DistillationColumn,
                ["AbsorptionColumn"] = ObjectType.AbsorptionColumn,
                ["ShortcutColumn"] = ObjectType.ShortcutColumn,
                ["Recycle"] = ObjectType.OT_Recycle,
                ["EnergyRecycle"] = ObjectType.OT_EnergyRecycle,
                ["Adjust"] = ObjectType.OT_Adjust,
                ["Spec"] = ObjectType.OT_Spec,
            };
    }
}
