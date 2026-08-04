using System;
using CS2Path.Core;
#if !OUT_OF_GAME_BUILD
using Game;
using Game.Modding;
using Game.SceneFlow;
using Unity.Entities;
#endif

namespace CS2Path.Mod
{
    /// <summary>
    /// Mod entry point. Shape verified against shipped open-source CS2 mods:
    /// <c>Game.Modding.IMod</c> with <c>OnLoad(UpdateSystem)</c> /
    /// <c>OnDispose()</c>, and systems registered through the supplied
    /// <c>UpdateSystem</c> at a <c>SystemUpdatePhase</c>.
    ///
    /// Deliberately minimal, and that is the point: this is plan §5 build-order
    /// step 1 — "adapters with vanilla passthrough, proving the seam". It
    /// registers an on-demand exporter and changes NOTHING about how the game
    /// routes. No vanilla system is disabled, no path is written. A mod that
    /// only observes cannot corrupt a save, so it is the right first thing to
    /// run against a city you care about.
    ///
    /// Steps 2-3 (behavioural pieces over vanilla queries, then the dispatch
    /// super-source) add systems here; the full engine swap comes later and is
    /// where <c>World.GetOrCreateSystemManaged(...).Enabled = false</c> against
    /// the vanilla pathfinding systems belongs.
    /// </summary>
    public sealed class Mod
#if !OUT_OF_GAME_BUILD
        : IMod
#endif
    {
        public const string Name = "CS2Path";

#if !OUT_OF_GAME_BUILD
        private GraphExporterSystem? _exporter;

        public void OnLoad(UpdateSystem updateSystem)
        {
            // Modification5 is where network-derived systems settle (the lane
            // graph is coherent by then). The system is created disabled and only
            // does work when Capture() is called, so the phase is really just
            // where it lives, not a per-frame cost.
            updateSystem.UpdateAt<GraphExporterSystem>(SystemUpdatePhase.Modification5);

            var world = World.DefaultGameObjectInjectionWorld;
            _exporter = world?.GetOrCreateSystemManaged<GraphExporterSystem>();
        }

        public void OnDispose()
        {
            _exporter = null;
        }

        /// <summary>
        /// Capture the current city and write it next to the save data. Drive
        /// this from a keybind or the dev console — never automatically.
        /// Returns the path written, or throws with a diagnostic.
        /// </summary>
        public string ExportCity(string path)
        {
            if (_exporter == null) throw new InvalidOperationException("mod not loaded");
            var export = _exporter.Capture();
            var writer = new GraphExporter();
            writer.Write(export, path);
            UnityEngine.Debug.Log($"[{Name}] export: {_exporter.LastDiagnostics}");
            UnityEngine.Debug.Log($"[{Name}] wrote {path} " +
                                  $"({export.NodeCount} nodes, {export.EdgeCount} edges, " +
                                  $"{export.Traffic.Count} traffic, {export.Demand.Count} demand)");
            return path;
        }
#endif
    }
}
