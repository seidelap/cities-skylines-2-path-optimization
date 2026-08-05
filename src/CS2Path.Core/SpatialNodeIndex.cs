using System;

namespace CS2Path.Core
{
    /// <summary>
    /// Uniform-grid nearest-node lookup over node coordinates. Built once per
    /// graph capture; used by the mod's trip sampler to map a trip endpoint's
    /// world position (from Game.Objects.Transform) onto the nearest exported
    /// graph node. Game-free so the harness can verify it against brute force.
    ///
    /// Queries are radius-bounded: an endpoint farther than maxRadius from any
    /// exported node returns -1 and the sample is dropped — a building on an
    /// unexported network (e.g. a pedestrian-only plaza when only car lanes
    /// were exported) must not be snapped onto an arbitrary distant road.
    /// </summary>
    public sealed class SpatialNodeIndex
    {
        private readonly float[] _x, _y;
        private readonly float _minX, _minY, _cell;
        private readonly int _cols, _rows;
        // CSR-style buckets: node ids grouped by cell.
        private readonly int[] _cellStart;
        private readonly int[] _nodesByCell;

        public SpatialNodeIndex(float[] x, float[] y, float cellSize = 64f)
        {
            _x = x ?? throw new ArgumentNullException(nameof(x));
            _y = y ?? throw new ArgumentNullException(nameof(y));
            if (x.Length != y.Length) throw new ArgumentException("coordinate arrays differ in length");
            _cell = cellSize > 0.01f ? cellSize : 64f;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] < minX) minX = x[i];
                if (x[i] > maxX) maxX = x[i];
                if (y[i] < minY) minY = y[i];
                if (y[i] > maxY) maxY = y[i];
            }
            if (x.Length == 0) { minX = minY = 0; maxX = maxY = 0; }
            _minX = minX; _minY = minY;
            _cols = Math.Max(1, (int)((maxX - minX) / _cell) + 1);
            _rows = Math.Max(1, (int)((maxY - minY) / _cell) + 1);

            // counting sort into cells
            _cellStart = new int[_cols * _rows + 1];
            for (int i = 0; i < x.Length; i++) _cellStart[CellOf(x[i], y[i]) + 1]++;
            for (int c = 0; c < _cols * _rows; c++) _cellStart[c + 1] += _cellStart[c];
            _nodesByCell = new int[x.Length];
            var cursor = (int[])_cellStart.Clone();
            for (int i = 0; i < x.Length; i++) _nodesByCell[cursor[CellOf(x[i], y[i])]++] = i;
        }

        private int CellOf(float px, float py)
        {
            int cx = (int)((px - _minX) / _cell), cy = (int)((py - _minY) / _cell);
            if (cx < 0) cx = 0; else if (cx >= _cols) cx = _cols - 1;
            if (cy < 0) cy = 0; else if (cy >= _rows) cy = _rows - 1;
            return cy * _cols + cx;
        }

        /// <summary>Nearest node id within maxRadius of (px, py), or -1.</summary>
        public int NearestWithin(float px, float py, float maxRadius)
        {
            if (_x.Length == 0 || !(maxRadius > 0f)) return -1;
            int qx = (int)((px - _minX) / _cell), qy = (int)((py - _minY) / _cell);
            int maxRing = (int)(maxRadius / _cell) + 1;
            int best = -1;
            float bestSq = maxRadius * maxRadius;

            for (int ring = 0; ring <= maxRing; ring++)
            {
                // Once a candidate is held, any node in a farther ring is at
                // least (ring-1)*cell away — stop when that exceeds the best.
                if (best >= 0)
                {
                    float lo = (ring - 1) * _cell;
                    if (lo > 0 && lo * lo > bestSq) break;
                }
                int x0 = qx - ring, x1 = qx + ring, y0 = qy - ring, y1 = qy + ring;
                for (int cy = y0; cy <= y1; cy++)
                {
                    if (cy < 0 || cy >= _rows) continue;
                    bool edgeRow = cy == y0 || cy == y1;
                    for (int cx = x0; cx <= x1; cx += edgeRow ? 1 : (x1 - x0 > 0 ? x1 - x0 : 1))
                    {
                        if (cx < 0 || cx >= _cols) continue;
                        int c = cy * _cols + cx;
                        for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                        {
                            int v = _nodesByCell[k];
                            float dx = _x[v] - px, dy = _y[v] - py;
                            float d2 = dx * dx + dy * dy;
                            if (d2 <= bestSq) { bestSq = d2; best = v; }
                        }
                        if (x1 == x0) break; // ring 0: single column
                    }
                }
            }
            return best;
        }
    }
}
