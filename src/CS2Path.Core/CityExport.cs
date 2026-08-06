using System;
using System.Collections.Generic;
using System.IO;

namespace CS2Path.Core
{
    /// <summary>
    /// Plan §5 build-order step 1: the serialized city-export format. The
    /// in-game reader adapter writes one of these from live ECS state; the
    /// standalone harness reads it and replays the real graph, the real demand,
    /// and the real congestion trace. This is the artifact that closes the gap
    /// between "tuned against a synthetic city" and "tuned against a real one".
    ///
    /// Lives in Core (not the harness) so both sides share exactly one
    /// definition — a format drift between exporter and importer is the single
    /// most likely way this loop silently lies.
    ///
    /// Layout (little-endian), all sections length-prefixed, FNV-1a footer so a
    /// truncated or corrupted cross-machine copy fails loudly instead of
    /// importing a plausible-looking half city:
    ///
    ///   "CS2CITY\0"  u32 version  u32 flags
    ///   u32 nodeCount, u32 edgeCount
    ///   nodes:  f32 x, f32 y
    ///   edges:  u32 tail, u32 head, f32 timeFree, f32 money, f32 comfort,
    ///           f32 capacity, f32 jamCapacity
    ///   [flag 1] traffic: u32 count, then u32 tick, u32 edge, f32 liveSeconds
    ///   [flag 2] demand:  u32 count, then u32 tick, u32 origin, u32 dest,
    ///                     f32 aTime, f32 aMoney, f32 aComfort
    ///   u32 checksum
    /// </summary>
    public sealed class CityExport
    {
        public const uint Magic0 = 0x43325343; // "CS2C"
        public const uint Magic1 = 0x00595449; // "ITY\0"
        public const uint CurrentVersion = 1;

        public struct TrafficSample { public int Tick; public int Edge; public float LiveSeconds; }
        public struct DemandSample { public int Tick; public int Origin, Dest; public Preference Alpha; }

        public int NodeCount;
        public float[] X = Array.Empty<float>();
        public float[] Y = Array.Empty<float>();

        public int[] Tail = Array.Empty<int>();
        public int[] Head = Array.Empty<int>();
        public float[] TimeFree = Array.Empty<float>();
        public float[] Money = Array.Empty<float>();
        public float[] Comfort = Array.Empty<float>();
        public float[] Capacity = Array.Empty<float>();
        public float[] JamCapacity = Array.Empty<float>();

        public List<TrafficSample> Traffic = new List<TrafficSample>();
        public List<DemandSample> Demand = new List<DemandSample>();

        public int EdgeCount => Tail.Length;

        // ------------------------------------------------------------------
        public void Write(Stream stream)
        {
            var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                uint flags = 0;
                if (Traffic.Count > 0) flags |= 1;
                if (Demand.Count > 0) flags |= 2;
                w.Write(CurrentVersion);
                w.Write(flags);
                w.Write(NodeCount);
                w.Write(EdgeCount);
                for (int v = 0; v < NodeCount; v++) { w.Write(X[v]); w.Write(Y[v]); }
                for (int e = 0; e < EdgeCount; e++)
                {
                    w.Write(Tail[e]); w.Write(Head[e]);
                    w.Write(TimeFree[e]); w.Write(Money[e]); w.Write(Comfort[e]);
                    w.Write(Capacity[e]); w.Write(JamCapacity[e]);
                }
                if ((flags & 1) != 0)
                {
                    w.Write(Traffic.Count);
                    foreach (var s in Traffic) { w.Write(s.Tick); w.Write(s.Edge); w.Write(s.LiveSeconds); }
                }
                if ((flags & 2) != 0)
                {
                    w.Write(Demand.Count);
                    foreach (var d in Demand)
                    {
                        w.Write(d.Tick); w.Write(d.Origin); w.Write(d.Dest);
                        w.Write(d.Alpha.Time); w.Write(d.Alpha.Money); w.Write(d.Alpha.Comfort);
                    }
                }
            }
            var bytes = payload.ToArray();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic0); w.Write(Magic1);
                w.Write(bytes);
                w.Write(Fnv1a(bytes));
            }
        }

        public static CityExport Read(Stream stream)
        {
            using (var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                if (r.ReadUInt32() != Magic0 || r.ReadUInt32() != Magic1)
                    throw new InvalidDataException("not a CS2CITY export");
                var rest = new MemoryStream();
                stream.CopyTo(rest);
                var all = rest.ToArray();
                if (all.Length < 4) throw new InvalidDataException("truncated export");
                int payloadLen = all.Length - 4;
                uint stored = (uint)(all[payloadLen] | (all[payloadLen + 1] << 8)
                                   | (all[payloadLen + 2] << 16) | (all[payloadLen + 3] << 24));
                var payload = new byte[payloadLen];
                Array.Copy(all, payload, payloadLen);
                uint actual = Fnv1a(payload);
                if (stored != actual)
                    throw new InvalidDataException($"export checksum mismatch (file corrupt or truncated): {stored:X8} != {actual:X8}");

                var c = new CityExport();
                using (var pr = new BinaryReader(new MemoryStream(payload)))
                {
                    uint version = pr.ReadUInt32();
                    if (version != CurrentVersion)
                        throw new InvalidDataException($"export version {version} != supported {CurrentVersion}");
                    uint flags = pr.ReadUInt32();
                    c.NodeCount = pr.ReadInt32();
                    int m = pr.ReadInt32();
                    c.X = new float[c.NodeCount]; c.Y = new float[c.NodeCount];
                    for (int v = 0; v < c.NodeCount; v++) { c.X[v] = pr.ReadSingle(); c.Y[v] = pr.ReadSingle(); }
                    c.Tail = new int[m]; c.Head = new int[m];
                    c.TimeFree = new float[m]; c.Money = new float[m]; c.Comfort = new float[m];
                    c.Capacity = new float[m]; c.JamCapacity = new float[m];
                    for (int e = 0; e < m; e++)
                    {
                        c.Tail[e] = pr.ReadInt32(); c.Head[e] = pr.ReadInt32();
                        c.TimeFree[e] = pr.ReadSingle(); c.Money[e] = pr.ReadSingle(); c.Comfort[e] = pr.ReadSingle();
                        c.Capacity[e] = pr.ReadSingle(); c.JamCapacity[e] = pr.ReadSingle();
                    }
                    if ((flags & 1) != 0)
                    {
                        int n = pr.ReadInt32();
                        c.Traffic.Capacity = n;
                        for (int i = 0; i < n; i++)
                            c.Traffic.Add(new TrafficSample { Tick = pr.ReadInt32(), Edge = pr.ReadInt32(), LiveSeconds = pr.ReadSingle() });
                    }
                    if ((flags & 2) != 0)
                    {
                        int n = pr.ReadInt32();
                        c.Demand.Capacity = n;
                        for (int i = 0; i < n; i++)
                            c.Demand.Add(new DemandSample
                            {
                                Tick = pr.ReadInt32(), Origin = pr.ReadInt32(), Dest = pr.ReadInt32(),
                                Alpha = new Preference(pr.ReadSingle(), pr.ReadSingle(), pr.ReadSingle()),
                            });
                    }
                }
                c.Validate();
                return c;
            }
        }

        /// <summary>Structural validation — an exporter bug must fail here, not
        /// surface later as a mysterious routing result.</summary>
        public void Validate()
        {
            if (NodeCount <= 0) throw new InvalidDataException("export has no nodes");
            if (X.Length != NodeCount || Y.Length != NodeCount) throw new InvalidDataException("coordinate arrays disagree with node count");
            int m = EdgeCount;
            if (Head.Length != m || TimeFree.Length != m || Money.Length != m
                || Comfort.Length != m || Capacity.Length != m || JamCapacity.Length != m)
                throw new InvalidDataException("edge arrays disagree in length");
            for (int e = 0; e < m; e++)
            {
                if (Tail[e] < 0 || Tail[e] >= NodeCount || Head[e] < 0 || Head[e] >= NodeCount)
                    throw new InvalidDataException($"edge {e} references node out of range");
                if (!(TimeFree[e] > 0) || !float.IsFinite(TimeFree[e]))
                    throw new InvalidDataException($"edge {e} has non-positive or non-finite free-flow time");
            }
            foreach (var s in Traffic)
                if (s.Edge < 0 || s.Edge >= m) throw new InvalidDataException("traffic sample references unknown edge");
            foreach (var d in Demand)
                if (d.Origin < 0 || d.Origin >= NodeCount || d.Dest < 0 || d.Dest >= NodeCount)
                    throw new InvalidDataException("demand sample references unknown node");
        }

        public Graph ToGraph()
        {
            var edges = new List<(int, int, float, float, float, float)>(EdgeCount);
            for (int e = 0; e < EdgeCount; e++)
                edges.Add((Tail[e], Head[e], TimeFree[e], Money[e], Comfort[e], Capacity[e]));
            return Graph.Build(NodeCount, edges, X, Y);
        }

        public static CityExport FromGraph(Graph g, float[] jamCapacity)
        {
            var c = new CityExport
            {
                NodeCount = g.NodeCount,
                X = g.X ?? new float[g.NodeCount],
                Y = g.Y ?? new float[g.NodeCount],
                Tail = (int[])g.Tail.Clone(),
                Head = (int[])g.Head.Clone(),
                TimeFree = (float[])g.TimeFree.Clone(),
                Money = (float[])g.Money.Clone(),
                Comfort = (float[])g.Comfort.Clone(),
                Capacity = (float[])g.Capacity.Clone(),
                JamCapacity = (float[])jamCapacity.Clone(),
            };
            return c;
        }

        private static uint Fnv1a(byte[] data)
        {
            uint h = 2166136261u;
            foreach (var b in data) { h ^= b; h *= 16777619u; }
            return h;
        }
    }
}
