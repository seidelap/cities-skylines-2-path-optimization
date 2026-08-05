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
        // One writer for the whole session: trace samples accumulate here and
        // ExportCity() merges them into the written file.
        private readonly GraphExporter _writer = new GraphExporter();
        // The capture a running trace attributes its samples to. Trace ids are
        // only meaningful against this snapshot, so ExportCity() must write THIS
        // graph, not a fresh re-capture (a re-capture may renumber).
        private CityExport? _captured;

        public void OnLoad(UpdateSystem updateSystem)
        {
            // Modification5 is where network-derived systems settle (the lane
            // graph is coherent by then). The system is created disabled and only
            // does work when Capture()/BeginTrace() is called, so the phase is
            // really just where it lives, not a per-frame cost.
            updateSystem.UpdateAt<GraphExporterSystem>(SystemUpdatePhase.Modification5);

            var world = World.DefaultGameObjectInjectionWorld;
            _exporter = world?.GetOrCreateSystemManaged<GraphExporterSystem>();

            // First-boot diagnostic: the exact vanilla pathfinding system names
            // (to profile now, to disable at the engine swap). Log, don't guess.
            if (world != null)
                UnityEngine.Debug.Log($"[{Name}] {GraphExporterSystem.DumpPathfindSystems(world)}");
        }

        public void OnDispose()
        {
            _exporter = null;
        }

        /// <summary>Capture the graph and begin cadenced trace recording (traffic
        /// deltas + observed trips). Drive from a keybind/dev console; play
        /// 20-30 min at normal speed, then StopTrace + ExportCity. Do not edit
        /// roads mid-trace (ids are pinned to this capture).</summary>
        public string StartTrace(int cadenceFrames = 64)
        {
            if (_exporter == null) throw new InvalidOperationException("mod not loaded");
            _captured = _exporter.Capture();
            UnityEngine.Debug.Log($"[{Name}] capture: {_exporter.LastDiagnostics}");
            var msg = _exporter.BeginTrace(_writer, cadenceFrames);
            UnityEngine.Debug.Log($"[{Name}] {msg}");
            return msg;
        }

        public string StopTrace()
        {
            if (_exporter == null) throw new InvalidOperationException("mod not loaded");
            var msg = _exporter.EndTrace();
            UnityEngine.Debug.Log($"[{Name}] {msg}");
            return msg;
        }

        /// <summary>
        /// Capture the current city and write it next to the save data. Drive
        /// this from a keybind or the dev console — never automatically.
        /// Returns the path written, or throws with a diagnostic.
        /// </summary>
        public string ExportCity(string path)
        {
            if (_exporter == null) throw new InvalidOperationException("mod not loaded");
            // Use the trace's pinned capture when one exists — trace edge/node
            // ids are only valid against it. Fresh capture otherwise.
            var export = _captured ?? _exporter.Capture();
            _writer.Write(export, path);
            UnityEngine.Debug.Log($"[{Name}] export: {_exporter.LastDiagnostics}");
            UnityEngine.Debug.Log($"[{Name}] wrote {path} " +
                                  $"({export.NodeCount} nodes, {export.EdgeCount} edges, " +
                                  $"{export.Traffic.Count} traffic, {export.Demand.Count} demand)");
            return path;
        }
#endif
    }
}
