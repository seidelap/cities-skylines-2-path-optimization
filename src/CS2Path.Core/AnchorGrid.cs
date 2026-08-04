using System;
using System.Collections.Generic;

namespace CS2Path.Core
{
    public enum Scenario : byte { FreeFlow = 0, Typical = 1, Live = 2 }

    /// <summary>
    /// Layer 1 (part 1): the anchor grid — preference profiles × traffic
    /// scenarios (plan §4 Layer 1, §4.8). The profile set always contains the
    /// coordinate axes (pure-time / pure-money / pure-comfort) so it positively
    /// spans the preference cone — the requirement of the §4.7 exactness
    /// certificate: any citizen alpha decomposes conically over anchors.
    /// Remaining profiles are trip-frequency-weighted k-means centroids of the
    /// empirical alpha distribution.
    /// </summary>
    public sealed class AnchorGrid
    {
        public Preference[] Profiles = null!;   // Profiles[0..2] are the axes
        public Scenario[] Scenarios = null!;
        public int ProfileCount => Profiles.Length;
        public int MetricCount => Profiles.Length * Scenarios.Length;

        public Preference PrefOf(int metric) => Profiles[metric % Profiles.Length];
        public Scenario ScenarioOf(int metric) => Scenarios[metric / Profiles.Length];
        public int MetricIndex(int profileIdx, Scenario s)
        {
            int si = Array.IndexOf(Scenarios, s);
            if (si < 0) throw new ArgumentException($"scenario {s} not in grid");
            return si * Profiles.Length + profileIdx;
        }

        /// <summary>First metric index of a scenario's contiguous lane block.</summary>
        public int ScenarioBlockStart(Scenario s) => Array.IndexOf(Scenarios, s) * Profiles.Length;

        public static AnchorGrid Build(IReadOnlyList<Preference> populationSample, IReadOnlyList<float>? tripWeights,
                                       int profileCount, Scenario[] scenarios, ulong seed = 12345)
        {
            if (profileCount < 3) throw new ArgumentException("need at least the 3 axis profiles");
            var profiles = new List<Preference>
            {
                Preference.AxisTime, Preference.AxisMoney, Preference.AxisComfort
            };
            int extra = profileCount - 3;
            if (extra > 0 && populationSample.Count > 0)
                profiles.AddRange(WeightedKMeans(populationSample, tripWeights, extra, seed));
            while (profiles.Count < profileCount)
                profiles.Add(Normalize(new Preference(1f, 1f, 1f)));
            return new AnchorGrid { Profiles = profiles.ToArray(), Scenarios = scenarios };
        }

        private static Preference Normalize(Preference p)
        {
            float s = p.Time + p.Money + p.Comfort;
            return s <= 0 ? p : new Preference(p.Time / s, p.Money / s, p.Comfort / s);
        }

        private static List<Preference> WeightedKMeans(IReadOnlyList<Preference> sample, IReadOnlyList<float>? w, int k, ulong seed)
        {
            var rng = new SplitMix64(seed);
            var centers = new List<Preference>(k);
            for (int i = 0; i < k; i++) centers.Add(Normalize(sample[(int)(rng.Next() % (ulong)sample.Count)]));
            var assign = new int[sample.Count];
            for (int iter = 0; iter < 12; iter++)
            {
                for (int i = 0; i < sample.Count; i++)
                {
                    var p = Normalize(sample[i]);
                    int best = 0; float bestD = float.MaxValue;
                    for (int c = 0; c < k; c++)
                    {
                        float dt = p.Time - centers[c].Time, dm = p.Money - centers[c].Money, dc = p.Comfort - centers[c].Comfort;
                        float d = dt * dt + dm * dm + dc * dc;
                        if (d < bestD) { bestD = d; best = c; }
                    }
                    assign[i] = best;
                }
                var sum = new (float t, float m, float c, float w)[k];
                for (int i = 0; i < sample.Count; i++)
                {
                    var p = Normalize(sample[i]);
                    float wi = w != null ? w[i] : 1f;
                    ref var s = ref sum[assign[i]];
                    s.t += p.Time * wi; s.m += p.Money * wi; s.c += p.Comfort * wi; s.w += wi;
                }
                for (int c = 0; c < k; c++)
                    if (sum[c].w > 0)
                        centers[c] = Normalize(new Preference(sum[c].t / sum[c].w, sum[c].m / sum[c].w, sum[c].c / sum[c].w));
            }
            return centers;
        }

        /// <summary>Weight of an original edge under metric m. The graded
        /// soft-closure multiplier (Layer 1) scales the WHOLE live planning cost
        /// (so it bites for every preference profile); hard closures (+inf)
        /// block every scenario. The live components stay linear in the
        /// preference vector, which the §4.7 conic decomposition requires.</summary>
        public float EdgeWeight(Graph g, int edge, int metric)
        {
            float mult = g.ClosureMult[edge];
            if (float.IsPositiveInfinity(mult)) return float.PositiveInfinity;
            var pref = PrefOf(metric);
            var scen = ScenarioOf(metric);
            switch (scen)
            {
                case Scenario.FreeFlow: return pref.Dot(g.TimeFree[edge], g.Money[edge], g.Comfort[edge]);
                case Scenario.Typical: return pref.Dot(g.TimeTypical[edge], g.Money[edge], g.Comfort[edge]);
                default: return pref.Dot(g.TimeLive[edge], g.Money[edge], g.Comfort[edge]) * mult;
            }
        }

        /// <summary>Exact edge weight under an arbitrary true alpha on the live
        /// components — the SAME component basis as live-scenario anchor metrics,
        /// which is what makes the §4.7 conic decomposition exact.</summary>
        public static float AlphaWeightLive(Graph g, int edge, in Preference alpha)
        {
            float mult = g.ClosureMult[edge];
            if (float.IsPositiveInfinity(mult)) return float.PositiveInfinity;
            return alpha.Dot(g.TimeLive[edge], g.Money[edge], g.Comfort[edge]) * mult;
        }

        /// <summary>Nearest live-scenario anchor profile for alpha (direction distance).</summary>
        public int NearestProfile(in Preference alpha)
        {
            float s = alpha.Time + alpha.Money + alpha.Comfort;
            float at = alpha.Time / s, am = alpha.Money / s, ac = alpha.Comfort / s;
            int best = 0; float bestD = float.MaxValue;
            for (int c = 0; c < Profiles.Length; c++)
            {
                float ps = Profiles[c].Time + Profiles[c].Money + Profiles[c].Comfort;
                float dt = at - Profiles[c].Time / ps, dm = am - Profiles[c].Money / ps, dc = ac - Profiles[c].Comfort / ps;
                float d = dt * dt + dm * dm + dc * dc;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        /// <summary>
        /// The §4.7 "tiny LP": choose the conic decomposition
        /// alpha = sum(lambda_i * a_i), lambda >= 0, that MAXIMIZES the lower
        /// bound sum(lambda_i * dStar_i). In 3-component preference space the
        /// LP optimum lies on a basis of at most 3 anchors, so we enumerate all
        /// anchor triples (C(P,3) 3x3 solves via Cramer's rule — microseconds)
        /// plus the always-feasible axes decomposition. A tight bound is what
        /// makes natural certification common and keeps the repair potential
        /// narrow. Returns the best LB; lambdaOut gets (profile, lambda) pairs.
        /// </summary>
        public float BestLowerBound(in Preference alpha, float[] dStar, List<(int profile, float lambda)> lambdaOut)
        {
            lambdaOut.Clear();
            int P = Profiles.Length;
            // axes baseline (profiles 0..2 are unit vectors: always exact)
            float best = 0f;
            bool ok = true;
            float bAxes = 0f;
            if (float.IsPositiveInfinity(dStar[0]) && alpha.Time > 0) ok = false;
            if (float.IsPositiveInfinity(dStar[1]) && alpha.Money > 0) ok = false;
            if (float.IsPositiveInfinity(dStar[2]) && alpha.Comfort > 0) ok = false;
            if (ok)
            {
                bAxes = alpha.Time * (alpha.Time > 0 ? dStar[0] : 0)
                      + alpha.Money * (alpha.Money > 0 ? dStar[1] : 0)
                      + alpha.Comfort * (alpha.Comfort > 0 ? dStar[2] : 0);
                best = bAxes;
                lambdaOut.Add((0, alpha.Time)); lambdaOut.Add((1, alpha.Money)); lambdaOut.Add((2, alpha.Comfort));
            }

            for (int i = 0; i < P; i++)
            {
                if (float.IsPositiveInfinity(dStar[i])) continue;
                for (int j = i + 1; j < P; j++)
                {
                    if (float.IsPositiveInfinity(dStar[j])) continue;
                    for (int k2 = j + 1; k2 < P; k2++)
                    {
                        if (float.IsPositiveInfinity(dStar[k2])) continue;
                        var a = Profiles[i]; var b = Profiles[j]; var c = Profiles[k2];
                        // Cramer: [a b c] * lambda = alpha
                        double det = Det(a.Time, b.Time, c.Time, a.Money, b.Money, c.Money, a.Comfort, b.Comfort, c.Comfort);
                        if (Math.Abs(det) < 1e-9) continue;
                        double l1 = Det(alpha.Time, b.Time, c.Time, alpha.Money, b.Money, c.Money, alpha.Comfort, b.Comfort, c.Comfort) / det;
                        double l2 = Det(a.Time, alpha.Time, c.Time, a.Money, alpha.Money, c.Money, a.Comfort, alpha.Comfort, c.Comfort) / det;
                        double l3 = Det(a.Time, b.Time, alpha.Time, a.Money, b.Money, alpha.Money, a.Comfort, b.Comfort, alpha.Comfort) / det;
                        if (l1 < -1e-5 || l2 < -1e-5 || l3 < -1e-5) continue; // outside the cone
                        double lb = Math.Max(0, l1) * dStar[i] + Math.Max(0, l2) * dStar[j] + Math.Max(0, l3) * dStar[k2];
                        if (lb > best)
                        {
                            best = (float)lb;
                            lambdaOut.Clear();
                            if (l1 > 1e-7) lambdaOut.Add((i, (float)l1));
                            if (l2 > 1e-7) lambdaOut.Add((j, (float)l2));
                            if (l3 > 1e-7) lambdaOut.Add((k2, (float)l3));
                        }
                    }
                }
            }
            return best;
        }

        private static double Det(double a11, double a12, double a13, double a21, double a22, double a23, double a31, double a32, double a33)
            => a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
    }

    /// <summary>Deterministic seeded RNG (plan §3: seeded noise, reproducible).</summary>
    public struct SplitMix64
    {
        private ulong _state;
        public SplitMix64(ulong seed) { _state = seed; }
        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
        public float NextFloat() => (float)NextDouble();
        /// <summary>Standard Gumbel draw for logit choice noise.</summary>
        public float NextGumbel()
        {
            double u = NextDouble();
            if (u < 1e-12) u = 1e-12;
            return (float)(-Math.Log(-Math.Log(u)));
        }
        public int NextInt(int maxExclusive) => (int)(Next() % (ulong)maxExclusive);
    }
}
