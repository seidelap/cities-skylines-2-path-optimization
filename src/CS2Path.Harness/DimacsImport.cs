using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CS2Path.Core;

namespace CS2Path.Harness
{
    /// <summary>
    /// Reads a real-world road network in DIMACS / PACE edge-list form into a
    /// <see cref="CityExport"/>, so assumption A1 can be measured on real
    /// topology instead of a synthetic grid.
    ///
    /// WHY THIS IS A VALID A1 TEST DESPITE CARRYING NO TRAVEL TIMES:
    /// the CCH skeleton is metric-INDEPENDENT by construction — nested
    /// dissection, the contraction order, the shortcut set, the elimination tree
    /// and its height are all computed from topology alone (plan §4 Layer 0:
    /// "computed once and remains valid for every metric"). Edge weights only
    /// enter at customization, which does not change the structure. So a graph
    /// with real topology and synthesised weights answers "do real road networks
    /// have small separators?" exactly, and only leaves cost-model realism —
    /// which is an A-nothing question — unanswered.
    ///
    /// Two honest caveats are reported alongside the numbers:
    ///   * these files carry no coordinates, so nested dissection falls back to
    ///     BFS level-set bisection instead of the geometric min-crossing cut.
    ///     That is the WEAKER path, so the measured result is a pessimistic
    ///     bound: with geometry it can only improve.
    ///   * weights are synthesised, so travel times are not meaningful; query
    ///     LATENCY still is, because it is driven by structure.
    ///
    /// Format (PACE 2016 / DIMACS-derived):
    ///   c  &lt;comment&gt;
    ///   p tw &lt;nodes&gt; &lt;edges&gt;      (or "p edge n m")
    ///   &lt;u&gt; &lt;v&gt;                  1-indexed, undirected, one per line
    /// Also tolerates DIMACS shortest-path form: "a &lt;u&gt; &lt;v&gt; &lt;w&gt;".
    /// </summary>
    public static class DimacsImport
    {
        /// <summary>
        /// Load a metro-sized subgraph with REAL coordinates from a continental
        /// graph plus its DIMACS .co coordinate file (`v id lon lat`, in
        /// microdegrees). This is what makes a genuine A1 test possible: the
        /// geometric nested-dissection path needs geometry, and cutting a
        /// bounding box out of a continental network yields a real metropolitan
        /// road network at city scale.
        ///
        /// Keeps only the largest connected component inside the box, so a
        /// clipped-off fragment cannot distort the structure measurements.
        /// </summary>
        public static CityExport LoadWithCoords(string grPath, string coPath,
                                                double lonMin, double latMin, double lonMax, double latMax,
                                                ulong seed = 7)
        {
            // --- pass 1: coordinates, keeping only nodes inside the box ---
            var keep = new Dictionary<int, int>();          // original id -> dense id
            var xs = new List<float>();
            var ys = new List<float>();
            const double Micro = 1e6;
            using (var sr = new StreamReader(coPath))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] != 'v') continue;
                    var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 4) continue;
                    if (!int.TryParse(f[1], out int id)) continue;
                    if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lonU)) continue;
                    if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double latU)) continue;
                    double lon = lonU / Micro, lat = latU / Micro;
                    if (lon < lonMin || lon > lonMax || lat < latMin || lat > latMax) continue;
                    keep[id] = xs.Count;
                    // Local equirectangular projection to metres — nested dissection
                    // needs a metric plane, not degrees.
                    double latRad = lat * Math.PI / 180.0;
                    xs.Add((float)(lon * 111_320.0 * Math.Cos(latRad)));
                    ys.Add((float)(lat * 110_540.0));
                }
            }
            if (keep.Count == 0) throw new InvalidDataException("bounding box contains no nodes");

            // --- pass 2: edges with both endpoints inside the box ---
            var eu = new List<int>();
            var ev = new List<int>();
            using (var sr = new StreamReader(grPath))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    char c0 = line[0];
                    if (c0 == 'c' || c0 == 'p' || c0 == '%') continue;
                    var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    int a, b;
                    if (c0 == 'a')
                    {
                        if (f.Length < 3 || !int.TryParse(f[1], out a) || !int.TryParse(f[2], out b)) continue;
                    }
                    else
                    {
                        if (f.Length < 2 || !int.TryParse(f[0], out a) || !int.TryParse(f[1], out b)) continue;
                    }
                    if (!keep.TryGetValue(a, out int da) || !keep.TryGetValue(b, out int db)) continue;
                    if (da == db) continue;
                    eu.Add(da); ev.Add(db);
                }
            }
            if (eu.Count == 0) throw new InvalidDataException("bounding box contains no edges");

            return BuildLargestComponent(xs, ys, eu, ev, seed);
        }

        /// <summary>Restrict to the largest connected component and emit real
        /// great-circle-ish edge lengths from the projected coordinates.</summary>
        private static CityExport BuildLargestComponent(List<float> xs, List<float> ys,
                                                        List<int> eu, List<int> ev, ulong seed)
        {
            int n = xs.Count;
            var adjHead = new int[n];
            var adjNext = new int[eu.Count * 2];
            var adjTo = new int[eu.Count * 2];
            Array.Fill(adjHead, -1);
            int ac = 0;
            void AddAdj(int a, int b) { adjTo[ac] = b; adjNext[ac] = adjHead[a]; adjHead[a] = ac++; }
            for (int i = 0; i < eu.Count; i++) { AddAdj(eu[i], ev[i]); AddAdj(ev[i], eu[i]); }

            var comp = new int[n];
            Array.Fill(comp, -1);
            int best = -1, bestSize = 0, nc = 0;
            var stack = new Stack<int>();
            for (int s = 0; s < n; s++)
            {
                if (comp[s] >= 0) continue;
                int size = 0;
                stack.Push(s); comp[s] = nc;
                while (stack.Count > 0)
                {
                    int v = stack.Pop(); size++;
                    for (int e = adjHead[v]; e >= 0; e = adjNext[e])
                        if (comp[adjTo[e]] < 0) { comp[adjTo[e]] = nc; stack.Push(adjTo[e]); }
                }
                if (size > bestSize) { bestSize = size; best = nc; }
                nc++;
            }

            var remap = new int[n];
            Array.Fill(remap, -1);
            var nx = new List<float>(bestSize);
            var ny = new List<float>(bestSize);
            for (int v = 0; v < n; v++)
                if (comp[v] == best) { remap[v] = nx.Count; nx.Add(xs[v]); ny.Add(ys[v]); }

            var rng = new SplitMix64(seed);
            var tail = new List<int>(eu.Count * 2);
            var head = new List<int>(eu.Count * 2);
            var timeFree = new List<float>(eu.Count * 2);
            var money = new List<float>(eu.Count * 2);
            var comfort = new List<float>(eu.Count * 2);
            var capacity = new List<float>(eu.Count * 2);
            var jam = new List<float>(eu.Count * 2);

            for (int i = 0; i < eu.Count; i++)
            {
                int a = remap[eu[i]], b = remap[ev[i]];
                if (a < 0 || b < 0) continue;
                float dx = nx[a] - nx[b], dy = ny[a] - ny[b];
                float len = Math.Max(5f, (float)Math.Sqrt(dx * dx + dy * dy));  // real metres
                float speed = 11f + 14f * rng.NextFloat();                      // synthesised
                float sec = len / speed;
                for (int d = 0; d < 2; d++)
                {
                    tail.Add(d == 0 ? a : b); head.Add(d == 0 ? b : a);
                    timeFree.Add(sec); money.Add(len * 0.001f); comfort.Add(sec * 0.3f);
                    capacity.Add(0.5f); jam.Add(Math.Max(1f, len / 8f));
                }
            }

            return new CityExport
            {
                NodeCount = nx.Count,
                X = nx.ToArray(), Y = ny.ToArray(),
                Tail = tail.ToArray(), Head = head.ToArray(),
                TimeFree = timeFree.ToArray(), Money = money.ToArray(), Comfort = comfort.ToArray(),
                Capacity = capacity.ToArray(), JamCapacity = jam.ToArray(),
            };
        }

        public static CityExport Load(string path, ulong seed = 7)
        {
            int n = 0;
            var us = new List<int>();
            var vs = new List<int>();
            var ws = new List<float>();   // present only for "a u v w" lines

            using (var sr = new StreamReader(path))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    char c0 = line[0];
                    if (c0 == 'c' || c0 == '%') continue;
                    if (c0 == 'p')
                    {
                        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        // "p tw N M" | "p edge N M" | "p sp N M"
                        for (int i = 1; i < parts.Length; i++)
                            if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                            { n = val; break; }
                        continue;
                    }
                    var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    int a, b;
                    if (c0 == 'a')
                    {
                        if (f.Length < 3) continue;
                        if (!int.TryParse(f[1], out a) || !int.TryParse(f[2], out b)) continue;
                        float w = f.Length > 3 && float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var pw) ? pw : 0f;
                        us.Add(a); vs.Add(b); ws.Add(w);
                    }
                    else
                    {
                        if (f.Length < 2) continue;
                        if (!int.TryParse(f[0], out a) || !int.TryParse(f[1], out b)) continue;
                        us.Add(a); vs.Add(b); ws.Add(0f);
                    }
                }
            }

            if (us.Count == 0) throw new InvalidDataException("no edges parsed — is this a DIMACS/PACE edge list?");
            // Node ids are 1-indexed in these files.
            int maxId = 0;
            for (int i = 0; i < us.Count; i++) { if (us[i] > maxId) maxId = us[i]; if (vs[i] > maxId) maxId = vs[i]; }
            if (n < maxId) n = maxId;

            var rng = new SplitMix64(seed);
            var tail = new List<int>(us.Count * 2);
            var head = new List<int>(us.Count * 2);
            var timeFree = new List<float>(us.Count * 2);
            var money = new List<float>(us.Count * 2);
            var comfort = new List<float>(us.Count * 2);
            var capacity = new List<float>(us.Count * 2);
            var jam = new List<float>(us.Count * 2);

            void Emit(int a, int b, float seconds, float len)
            {
                tail.Add(a); head.Add(b);
                timeFree.Add(seconds);
                money.Add(len * 0.001f);
                comfort.Add(seconds * 0.3f);
                capacity.Add(0.5f);
                jam.Add(Math.Max(1f, len / 8f));
            }

            for (int i = 0; i < us.Count; i++)
            {
                int a = us[i] - 1, b = vs[i] - 1;
                if (a < 0 || b < 0 || a >= n || b >= n || a == b) continue;
                // Synthesised geometry: segment length 40-260 m, speed 11-25 m/s.
                // Only structure is real; this exists so the metric layer has
                // something plausible to customize.
                float len = ws[i] > 0 ? ws[i] : 40f + 220f * rng.NextFloat();
                float speed = 11f + 14f * rng.NextFloat();
                float sec = len / speed;
                Emit(a, b, sec, len);
                Emit(b, a, sec, len);   // these files are undirected
            }

            return new CityExport
            {
                NodeCount = n,
                X = new float[n],   // no coordinates in this format — see class docs
                Y = new float[n],
                Tail = tail.ToArray(),
                Head = head.ToArray(),
                TimeFree = timeFree.ToArray(),
                Money = money.ToArray(),
                Comfort = comfort.ToArray(),
                Capacity = capacity.ToArray(),
                JamCapacity = jam.ToArray(),
            };
        }
    }
}
