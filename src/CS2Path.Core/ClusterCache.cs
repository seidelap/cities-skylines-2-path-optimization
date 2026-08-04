using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    /// <summary>
    /// §4.9: one shared route-knowledge entry for an (origin-cell,
    /// destination-cell) pair. Route knowledge (via candidates, coverage
    /// statistics, exploration demand) lives HERE, shared by every trip on the
    /// corridor; agents privately hold only a cursor into it (held branch ids +
    /// bound stamps — tens of bytes). Entries are populated by real traffic:
    /// every trip's batched anchor sweep harvests its meeting nodes, direct
    /// generation seeds cold entries, and exploration donates repairs once,
    /// globally.
    /// </summary>
    public sealed class ClusterEntry
    {
        public struct ViaCandidate
        {
            public int Via;
            public short MetricId;   // the metric it was optimal under ("via the bridge, per pure-time")
        }

        public List<ViaCandidate> Vias = new List<ViaCandidate>(6);
        // Good–Turing over the arrival stream: P(next arrival discovers an
        // unseen route) ≈ singletons/arrivals.
        public Dictionary<int, int> ViaSeen = new Dictionary<int, int>(12);
        public long Arrivals;
        public int Singletons;
        public long LastUsedStamp;
        public byte Level;
        // exploration demand (§4.7 async repair): logged certificate gaps
        public float GapSum;
        public int GapCount;
        public bool Urgent;              // quarantined: below coverage while serving demand
        public int GapSampleS = -1, GapSampleT = -1;
        public Preference GapSampleAlpha;

        /// <summary>Covered ⇔ the discovery probability of an unseen route has
        /// fallen below ~2% (plan §4.9), with a minimum arrival mass.</summary>
        public bool Covered => Arrivals >= 20 && Singletons <= Math.Max(1.0, Arrivals) * 0.02;

        // ---- realized travel-time telemetry (register opportunity #2) ----
        // Jacobson/Karels-style EMA of mean and absolute deviation of REALIZED
        // trip seconds, per time-of-day bucket, plus a realized/predicted ratio
        // EMA (prediction bias under congestion dynamics — the confidence input
        // for predictive pricing and adaptive departures). Edit-reset semantics
        // inherited from the cache (entries remap/evict with topology).
        public const int TodBuckets = 4;
        public float[] RealizedMean = new float[TodBuckets];
        public float[] RealizedDev = new float[TodBuckets];
        public int[] RealizedCount = new int[TodBuckets];
        public float RatioEma = 1f;      // realized / predicted-at-plan-time
        public int RatioCount;

        // representative OD pair: cell-key remap after re-dissection (gap #3)
        public int RepS = -1, RepT = -1;

        public void RecordRealized(int bucket, float realizedSec, float predictedSec)
        {
            const float A = 0.15f;       // slow EMA: learning must lag the swings it damps
            bucket = Math.Max(0, Math.Min(TodBuckets - 1, bucket));
            if (RealizedCount[bucket] == 0)
            {
                RealizedMean[bucket] = realizedSec;
                RealizedDev[bucket] = realizedSec * 0.1f;
            }
            else
            {
                float err = realizedSec - RealizedMean[bucket];
                RealizedMean[bucket] += A * err;
                RealizedDev[bucket] += A * (Math.Abs(err) - RealizedDev[bucket]);
            }
            RealizedCount[bucket]++;
            if (predictedSec > 1f && float.IsFinite(realizedSec))
            {
                float r = Math.Max(0.25f, Math.Min(4f, realizedSec / predictedSec));
                RatioEma = RatioCount == 0 ? r : RatioEma + A * (r - RatioEma);
                RatioCount++;
            }
        }

        /// <summary>Confidence that this entry's typical/predicted costs are
        /// trustworthy: high when realized deviation is small relative to mean
        /// and there is sample support. Feeds the horizon blend weight.</summary>
        public float PredictionConfidence(int bucket)
        {
            bucket = Math.Max(0, Math.Min(TodBuckets - 1, bucket));
            if (RealizedCount[bucket] < 5) return 0f;
            float cv = RealizedDev[bucket] / Math.Max(1f, RealizedMean[bucket]);
            return 1f / (1f + 4f * cv);
        }
    }

    /// <summary>
    /// §4.9 cluster hierarchy: entries keyed by dissection-cell pairs where the
    /// cells are nodes of the Layer-0 nested-dissection tree itself. A trip keys
    /// into the level at which its endpoints are well-separated — implemented
    /// coordinate-free as (common cell-path prefix length + 2), so short trips
    /// use fine neighborhood pairs and long trips coarse district pairs.
    /// Entries materialize lazily on first demand and are LRU-evicted.
    /// Thread-safe via a coarse lock (operations are short).
    /// </summary>
    public sealed class ClusterCache
    {
        private readonly NestedDissection.CellPath[] _cells;
        private readonly Dictionary<(ulong a, ulong b), ClusterEntry> _entries
            = new Dictionary<(ulong, ulong), ClusterEntry>();
        private readonly object _sync = new object();
        private long _stamp;

        public int MaxEntries = 200_000;
        public int EntryViaCap = 12;

        /// <summary>Warm-up governor (gap #2): bounds simultaneous direct
        /// generations so a cold cache (fresh load / post-rebuild) degrades to
        /// thin-service + urgent exploration instead of a synchronized
        /// generation storm — the failure mode this system exists to kill.
        /// Refill each tick via RefillDirectGenBudget; &lt;0 = unlimited.</summary>
        public int DirectGenBudget = -1;
        private int _directGenTokens = int.MaxValue;

        public long Hits, Misses, Evictions, GovernorDenials;

        public void RefillDirectGenBudget()
        {
            _directGenTokens = DirectGenBudget < 0 ? int.MaxValue : DirectGenBudget;
        }

        /// <summary>Consume one direct-generation token; false = governor says
        /// serve thin from held knowledge this tick.</summary>
        public bool TryAcquireDirectGen()
        {
            lock (_sync)
            {
                if (_directGenTokens <= 0) { GovernorDenials++; return false; }
                _directGenTokens--;
                return true;
            }
        }

        public ClusterCache(NestedDissection.CellPath[] cells)
        {
            _cells = cells;
        }

        public int EntryCount { get { lock (_sync) return _entries.Count; } }

        private static ulong CellCode(in NestedDissection.CellPath p, int level)
        {
            int l = Math.Min(level, p.Depth);
            ulong prefix = l >= 64 ? p.PathBits : p.PathBits & ((1UL << l) - 1);
            return ((ulong)(uint)l << 58) | prefix;
        }

        /// <summary>Well-separated level for an OD pair: two levels below the
        /// lowest common dissection cell (cell diameter ≈ ¼ trip scale).</summary>
        public int PairLevel(int s, int t)
        {
            var ps = _cells[s]; var pt = _cells[t];
            int maxCp = Math.Min(ps.Depth, pt.Depth);
            int cp = 0;
            ulong diff = ps.PathBits ^ pt.PathBits;
            while (cp < maxCp && (diff & (1UL << cp)) == 0) cp++;
            return Math.Min(maxCp, cp + 2);
        }

        /// <summary>Corridor identity of a via node at an entry's scale (§4.9:
        /// candidates group by shared high-ranked via-nodes): the via's
        /// dissection cell at the pair level. Used for nested logit and the
        /// quarantine diversity requirement.</summary>
        public ulong CorridorOf(int via, int level) => CellCode(in _cells[via], level);

        public ClusterEntry GetOrCreate(int s, int t, out bool created)
        {
            int level = PairLevel(s, t);
            var key = (CellCode(in _cells[s], level), CellCode(in _cells[t], level));
            lock (_sync)
            {
                _stamp++;
                if (_entries.TryGetValue(key, out var e))
                {
                    e.LastUsedStamp = _stamp;
                    created = false;
                    Hits++;
                    return e;
                }
                if (_entries.Count >= MaxEntries) EvictOldestLocked();
                e = new ClusterEntry { Level = (byte)level, LastUsedStamp = _stamp, RepS = s, RepT = t };
                _entries[key] = e;
                created = true;
                Misses++;
                return e;
            }
        }

        private void EvictOldestLocked()
        {
            // amortized LRU: drop the oldest ~1/8 of entries
            var stamps = new List<long>(_entries.Count);
            foreach (var e in _entries.Values) stamps.Add(e.LastUsedStamp);
            stamps.Sort();
            long cutoff = stamps[stamps.Count / 8];
            var dead = new List<(ulong, ulong)>();
            foreach (var kv in _entries)
                if (kv.Value.LastUsedStamp <= cutoff) dead.Add(kv.Key);
            foreach (var k in dead) { _entries.Remove(k); Evictions++; }
        }

        /// <summary>Record one arrival's harvested via (§4.9 accretive choice-set
        /// generation + Good–Turing accounting). Returns true if the via is new
        /// to the entry.</summary>
        public bool Harvest(ClusterEntry e, int via, short metricId)
        {
            lock (_sync)
            {
                e.ViaSeen.TryGetValue(via, out int seen);
                e.ViaSeen[via] = seen + 1;
                if (seen == 0)
                {
                    e.Singletons++;
                    if (e.Vias.Count < EntryViaCap)
                        e.Vias.Add(new ClusterEntry.ViaCandidate { Via = via, MetricId = metricId });
                    return true;
                }
                if (seen == 1) e.Singletons--;
                return false;
            }
        }

        public void Arrive(ClusterEntry e)
        {
            lock (_sync) e.Arrivals++;
        }

        /// <summary>§4.7: a material certificate gap becomes exploration demand
        /// on the entry — the trip proceeds on its held branch.</summary>
        public void LogGap(ClusterEntry e, float gap, int s, int t, in Preference alpha)
        {
            lock (_sync)
            {
                e.GapSum += gap; e.GapCount++;
                e.GapSampleS = s; e.GapSampleT = t; e.GapSampleAlpha = alpha;
            }
        }

        /// <summary>Thread-safe realized-travel recording (register #2).</summary>
        public void RecordRealized(ClusterEntry e, int bucket, float realizedSec, float predictedSec)
        {
            lock (_sync) e.RecordRealized(bucket, realizedSec, predictedSec);
        }

        /// <summary>Clear an entry's exploration demand after a task served it
        /// (demand re-accumulates from live gaps if the hole persists).</summary>
        public void DrainGap(ClusterEntry e)
        {
            lock (_sync) { e.GapSum = 0f; e.GapCount = 0; }
        }

        /// <summary>Copy the entry's held-branch candidates out under the lock.</summary>
        public int CopyVias(ClusterEntry e, ClusterEntry.ViaCandidate[] buf)
        {
            lock (_sync)
            {
                int n = Math.Min(buf.Length, e.Vias.Count);
                for (int i = 0; i < n; i++) buf[i] = e.Vias[i];
                return n;
            }
        }

        /// <summary>Entries wanting exploration, urgent (quarantined) first,
        /// then by demand mass. Snapshot; bounded count.</summary>
        public List<ClusterEntry> TopExplorationDemand(int count)
        {
            lock (_sync)
            {
                var all = new List<ClusterEntry>();
                foreach (var e in _entries.Values)
                    if (e.Urgent || e.GapCount > 0) all.Add(e);
                all.Sort((x, y) =>
                {
                    if (x.Urgent != y.Urgent) return x.Urgent ? -1 : 1;
                    return y.GapSum.CompareTo(x.GapSum);
                });
                if (all.Count > count) all.RemoveRange(count, all.Count - count);
                return all;
            }
        }

        /// <summary>Gap #3: re-key every entry after a Layer-0 re-dissection —
        /// background rebuilds renumber cells, and without matching, every major
        /// road project would orphan the cache and reproduce the cold-start
        /// storm. Entries re-key by their representative OD pair under the NEW
        /// cell paths; vias that no longer exist are dropped; entries whose
        /// endpoints vanished are evicted. Call with the rebuilt engine's
        /// CellPaths and node count.</summary>
        public ClusterCache RemapAfterRebuild(NestedDissection.CellPath[] newCells, int newNodeCount)
        {
            var fresh = new ClusterCache(newCells)
            {
                MaxEntries = MaxEntries, EntryViaCap = EntryViaCap, DirectGenBudget = DirectGenBudget,
            };
            lock (_sync)
            {
                foreach (var e in _entries.Values)
                {
                    if (e.RepS < 0 || e.RepS >= newNodeCount || e.RepT < 0 || e.RepT >= newNodeCount) continue;
                    var moved = fresh.GetOrCreate(e.RepS, e.RepT, out bool created);
                    if (!created) continue; // key collision: keep the first
                    // carry route knowledge + telemetry; drop out-of-range vias
                    moved.Vias.Clear();
                    foreach (var v in e.Vias)
                        if (v.Via >= 0 && v.Via < newNodeCount) moved.Vias.Add(v);
                    foreach (var kv in e.ViaSeen)
                        if (kv.Key >= 0 && kv.Key < newNodeCount) moved.ViaSeen[kv.Key] = kv.Value;
                    moved.Arrivals = e.Arrivals; moved.Singletons = e.Singletons;
                    moved.GapSum = e.GapSum; moved.GapCount = e.GapCount;
                    Array.Copy(e.RealizedMean, moved.RealizedMean, ClusterEntry.TodBuckets);
                    Array.Copy(e.RealizedDev, moved.RealizedDev, ClusterEntry.TodBuckets);
                    Array.Copy(e.RealizedCount, moved.RealizedCount, ClusterEntry.TodBuckets);
                    moved.RatioEma = e.RatioEma; moved.RatioCount = e.RatioCount;
                }
                fresh.Hits = Hits; fresh.Misses = Misses;
            }
            return fresh;
        }

        public long EstimatedBytes()
        {
            lock (_sync)
            {
                long b = 0;
                foreach (var e in _entries.Values)
                    b += 96 + e.Vias.Count * 8 + e.ViaSeen.Count * 16;
                return b;
            }
        }
    }
}
