using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    public sealed class DestinationSet
    {
        public int[] Node = Array.Empty<int>();
        public int[] Category = Array.Empty<int>();
        public float[] AttractionSeconds = Array.Empty<float>(); // stock/quality, in generalized-time units
        public float[] Price = Array.Empty<float>();             // money units, weighted by alpha.Money
        public int Count => Node.Length;
        public int CategoryCount;
    }

    public struct DestinationCandidate
    {
        public int Dest;
        public int ViaNode;       // bucket meet node — the (destination, via) pair of plan §4 Layer 3
        public float ScanCost;    // reference-metric access-cost minus attraction offset
    }

    /// <summary>
    /// Layer 3: flexible-destination search (plan §4 Layer 3). Every candidate
    /// destination posts distance-only bucket entries at the hierarchy nodes its
    /// backward upward search touches, partitioned by category and sorted by
    /// distance. A shopper runs ONE forward upward search and scans the buckets
    /// with early termination, receiving a menu of (destination, route) pairs in
    /// commensurable units — attraction and access cost are summed inside one
    /// search and cannot be mis-weighted independently.
    ///
    /// Entries are distance-only, so stock/price changes are scalar updates that
    /// never re-run a backward search; buckets are shared across all shoppers.
    /// Refresh is staggered: a rotating slice of destinations re-runs its
    /// backward search per tick, and buckets are compacted periodically.
    /// </summary>
    public sealed class DestinationBuckets
    {
        public DestinationSet Dests = null!;
        public int RefMetric;                 // scan metric (live pure-time by default)

        private CchQuery _q = null!;
        // labels per destination (nodes touched + distances)
        private int[][] _labNodes = null!;
        private float[][] _labDists = null!;
        // per-category, per-node buckets: (dest, dist) sorted by dist
        private List<(int dest, float dist)>?[][] _buckets = null!;
        private float[] _maxAttract = null!;
        private int _refreshCursor;
        // event-driven refresh state: a destination's labels depend exactly on
        // the up-arcs whose tails lie on its ancestor chain, so the customizer's
        // per-node change epochs give an exact staleness test (memoized
        // max-epoch along shared chain suffixes).
        private int[] _lastRefreshEpoch = null!;
        private int[] _sinceForced = null!;
        private int[] _chainMax = null!, _chainStamp = null!, _chainBuf = null!;
        private int _memoStamp;

        /// <summary>Force-refresh a destination after this many event-driven
        /// scans without a refresh — the slow drift sweep of plan §4.9 v3.</summary>
        public int DriftSweepScans = 40;

        public long EntriesTotal;             // telemetry
        public long LastScanEntries;
        public long ScannedForRefresh, RefreshedDirty, RefreshedDrift; // telemetry

        public static DestinationBuckets Build(CchQuery q, QueryContext ctx, DestinationSet dests, int refMetric)
        {
            var b = new DestinationBuckets { Dests = dests, RefMetric = refMetric, _q = q };
            int n = q.C.NodeCount;
            b._labNodes = new int[dests.Count][];
            b._labDists = new float[dests.Count][];
            b._buckets = new List<(int, float)>?[dests.CategoryCount][];
            for (int c = 0; c < dests.CategoryCount; c++) b._buckets[c] = new List<(int, float)>?[n];
            b._maxAttract = new float[dests.CategoryCount];
            for (int d = 0; d < dests.Count; d++)
                b._maxAttract[dests.Category[d]] = Math.Max(b._maxAttract[dests.Category[d]], dests.AttractionSeconds[d]);
            b._lastRefreshEpoch = new int[dests.Count];
            b._sinceForced = new int[dests.Count];
            b._chainMax = new int[n]; b._chainStamp = new int[n]; b._chainBuf = new int[n];
            for (int d = 0; d < dests.Count; d++) b.RefreshDestination(ctx, d);
            int epoch0 = q.M.ChangeEpoch;
            for (int d = 0; d < dests.Count; d++) b._lastRefreshEpoch[d] = epoch0;
            b.RebuildBuckets();
            return b;
        }

        /// <summary>One backward upward search: post "reachable at cost d" at the
        /// hierarchy nodes it touches.</summary>
        public void RefreshDestination(QueryContext ctx, int d)
        {
            var nodes = new List<int>(256);
            var dists = new List<float>(256);
            _q.BackwardUpwardLabels(ctx, Dests.Node[d], RefMetric, (node, dist) =>
            {
                nodes.Add(node); dists.Add(dist);
            });
            _labNodes[d] = nodes.ToArray();
            _labDists[d] = dists.ToArray();
        }

        /// <summary>Blind staggered refresh (v2 behavior, kept for A/B): re-runs
        /// backward searches for `count` destinations regardless of change —
        /// linear in destination count.</summary>
        public void RefreshSlice(QueryContext ctx, int count)
        {
            for (int i = 0; i < count && Dests.Count > 0; i++)
            {
                RefreshDestination(ctx, _refreshCursor);
                _lastRefreshEpoch[_refreshCursor] = _q.M.ChangeEpoch;
                _sinceForced[_refreshCursor] = 0;
                _refreshCursor = (_refreshCursor + 1) % Dests.Count;
            }
        }

        /// <summary>Event-driven refresh (v3): scan up to scanCount destinations
        /// in rotation, re-running a backward search ONLY for those whose
        /// reachability cone was touched by a metric change since their last
        /// refresh (exact test, see field docs) — plus a slow drift sweep.
        /// Cost scales with actual change, not destination count.</summary>
        public (int scanned, int refreshed) RefreshSliceEventDriven(QueryContext ctx, int scanCount, int maxRefreshes)
        {
            _memoStamp++;
            int scanned = 0, refreshed = 0;
            for (; scanned < scanCount && Dests.Count > 0 && refreshed < maxRefreshes; scanned++)
            {
                int d = _refreshCursor;
                _refreshCursor = (_refreshCursor + 1) % Dests.Count;
                bool drift = ++_sinceForced[d] >= DriftSweepScans;
                bool dirty = drift || ChainMaxEpoch(Dests.Node[d]) > _lastRefreshEpoch[d];
                if (!dirty) continue;
                RefreshDestination(ctx, d);
                _lastRefreshEpoch[d] = _q.M.ChangeEpoch;
                _sinceForced[d] = 0;
                refreshed++;
                if (drift) RefreshedDrift++; else RefreshedDirty++;
            }
            ScannedForRefresh += scanned;
            return (scanned, refreshed);
        }

        /// <summary>Max NodeArcChangeEpoch over the node's elimination-tree
        /// ancestor chain, memoized per call batch (chains share suffixes).</summary>
        private int ChainMaxEpoch(int v)
        {
            var c = _q.C; var epochs = _q.M.NodeArcChangeEpoch;
            int len = 0, u = v;
            while (u >= 0 && _chainStamp[u] != _memoStamp) { _chainBuf[len++] = u; u = c.EtParent[u]; }
            int inherit = u >= 0 ? _chainMax[u] : 0;
            for (int i = len - 1; i >= 0; i--)
            {
                int x = _chainBuf[i];
                if (epochs[x] > inherit) inherit = epochs[x];
                _chainMax[x] = inherit; _chainStamp[x] = _memoStamp;
            }
            return _chainMax[v];
        }

        /// <summary>Rebuild the shared per-node buckets from destination labels.
        /// Also re-derives the per-category attraction upper bounds, so scalar
        /// stock/price updates (which never re-run a backward search) keep the
        /// scan's early-termination bound sound.</summary>
        public void RebuildBuckets()
        {
            for (int c = 0; c < Dests.CategoryCount; c++) Array.Clear(_buckets[c], 0, _buckets[c].Length);
            Array.Clear(_maxAttract, 0, _maxAttract.Length);
            for (int d = 0; d < Dests.Count; d++)
                _maxAttract[Dests.Category[d]] = Math.Max(_maxAttract[Dests.Category[d]], Dests.AttractionSeconds[d]);
            EntriesTotal = 0;
            for (int d = 0; d < Dests.Count; d++)
            {
                int cat = Dests.Category[d];
                var nodes = _labNodes[d]; var dists = _labDists[d];
                for (int i = 0; i < nodes.Length; i++)
                {
                    var list = _buckets[cat][nodes[i]];
                    if (list == null) { list = new List<(int, float)>(4); _buckets[cat][nodes[i]] = list; }
                    list.Add((d, dists[i]));
                    EntriesTotal++;
                }
            }
            for (int c = 0; c < Dests.CategoryCount; c++)
                foreach (var l in _buckets[c])
                    l?.Sort((a, b) => a.dist.CompareTo(b.dist));
        }

        /// <summary>One forward upward search + early-terminating bucket scan.
        /// Returns up to topM candidates, best scan-cost first. The scan keeps
        /// an exact per-destination top-M list so the termination bound
        /// ("distance alone exceeds the best attraction-adjusted cost so far")
        /// is sound.</summary>
        public int Scan(QueryContext ctx, int origin, int category, DestinationCandidate[] outBuf, int topM)
        {
            var buckets = _buckets[category];
            float maxAttract = _maxAttract[category];
            var top = new List<DestinationCandidate>(topM + 1); // sorted ascending by ScanCost, deduped by Dest
            LastScanEntries = 0;

            _q.ForwardUpwardLabels(ctx, origin, RefMetric, (node, df) =>
            {
                var list = buckets[node];
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var (dest, dist) = list[i];
                    LastScanEntries++;
                    // Early termination: entries are dist-sorted, so once even the
                    // maximum attraction offset cannot beat the current M-th best,
                    // nothing later in this bucket can either.
                    if (top.Count >= topM && df + dist - maxAttract >= top[top.Count - 1].ScanCost) break;
                    float cost = df + dist - Dests.AttractionSeconds[dest];
                    int existing = -1;
                    for (int j = 0; j < top.Count; j++) if (top[j].Dest == dest) { existing = j; break; }
                    if (existing >= 0)
                    {
                        if (cost >= top[existing].ScanCost) continue;
                        top.RemoveAt(existing);
                    }
                    else if (top.Count >= topM && cost >= top[top.Count - 1].ScanCost) continue;
                    int ins = top.Count;
                    while (ins > 0 && top[ins - 1].ScanCost > cost) ins--;
                    top.Insert(ins, new DestinationCandidate { Dest = dest, ViaNode = node, ScanCost = cost });
                    if (top.Count > topM) top.RemoveAt(top.Count - 1);
                }
            });

            for (int i = 0; i < top.Count; i++) outBuf[i] = top[i];
            return top.Count;
        }
    }

    /// <summary>
    /// Layer 3 mirror construction for service dispatch (plan: taxis, hearses,
    /// garbage — measured top query producers). The fleet posts forward upward
    /// labels into shared buckets; each request runs ONE backward upward search
    /// and scans, instead of per-vehicle queries. Vehicle moves re-post lazily
    /// (stale entries are version-checked at scan time).
    /// </summary>
    public sealed class FleetIndex
    {
        private CchQuery _q = null!;
        public int RefMetric;
        private List<(int veh, int version, float dist)>?[] _buckets = null!;
        private int[] _vehicleVersion = null!;
        private int[] _vehicleNode = null!;
        private long[] _vehicleEntryCount = null!;
        private long _entriesTotal, _entriesLive;

        public static FleetIndex Create(CchQuery q, int fleetSize, int refMetric)
        {
            return new FleetIndex
            {
                _q = q, RefMetric = refMetric,
                _buckets = new List<(int, int, float)>?[q.C.NodeCount],
                _vehicleVersion = new int[fleetSize],
                _vehicleNode = new int[fleetSize],
                _vehicleEntryCount = new long[fleetSize],
            };
        }

        /// <summary>(Re-)post a vehicle's forward labels after it moves. Stale
        /// entries are version-skipped at scan time and swept automatically once
        /// they outnumber live ones (bounded memory without an explicit
        /// maintenance schedule).</summary>
        public void PostVehicle(QueryContext ctx, int veh, int node)
        {
            _entriesLive -= _vehicleEntryCount[veh];
            _vehicleVersion[veh]++;
            _vehicleNode[veh] = node;
            int ver = _vehicleVersion[veh];
            long added = 0;
            _q.ForwardUpwardLabels(ctx, node, RefMetric, (u, df) =>
            {
                var list = _buckets[u];
                if (list == null) { list = new List<(int, int, float)>(4); _buckets[u] = list; }
                list.Add((veh, ver, df));
                added++;
            });
            _vehicleEntryCount[veh] = added;
            _entriesTotal += added;
            _entriesLive += added;
            if (_entriesTotal > 2 * _entriesLive + 1024) { Compact(); _entriesTotal = _entriesLive; }
        }

        /// <summary>One search per request: nearest vehicle by live cost.</summary>
        public (int veh, float dist) Dispatch(QueryContext ctx, int requestNode)
        {
            int bestVeh = -1; float best = float.PositiveInfinity;
            _q.BackwardUpwardLabels(ctx, requestNode, RefMetric, (u, db) =>
            {
                var list = _buckets[u];
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var (veh, ver, df) = list[i];
                    if (_vehicleVersion[veh] != ver) continue; // stale post
                    float cand = df + db;
                    if (cand < best) { best = cand; bestVeh = veh; }
                }
            });
            return (bestVeh, best);
        }

        /// <summary>Drop stale entries (amortized cleanup).</summary>
        public void Compact()
        {
            foreach (var list in _buckets)
                list?.RemoveAll(e => _vehicleVersion[e.veh] != e.version);
        }
    }
}
