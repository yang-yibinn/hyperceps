using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Cordyceps.Core.Batch
{
    /// <summary>
    /// Places new objects that were given no x/y: columns by dependency depth, data flowing left to
    /// right, the block to the right of what feeds it (or of the existing canvas), moved down if it
    /// would overlap anything already there. A component inserted into a wire sits midway along it.
    /// Works on plain boxes so it can be unit-tested; the host measures the real objects.
    /// </summary>
    public static class AutoLayout
    {
        public const float ColumnGap = 60f;
        public const float RowGap = 24f;

        public sealed class Node
        {
            public int Id { get; set; }
            public SizeF Size { get; set; }
            /// <summary>The node's box when the caller placed it explicitly; it is then left alone.</summary>
            public RectangleF? Fixed { get; set; }
            /// <summary>Boxes of existing objects wired into this node.</summary>
            public List<RectangleF> Upstream { get; } = new List<RectangleF>();
            /// <summary>When the node is inserted into a wire: the boxes at the wire's two ends.</summary>
            public RectangleF? InsertSource { get; set; }
            public RectangleF? InsertTarget { get; set; }
        }

        /// <param name="nodes">New objects, in the order the batch listed them.</param>
        /// <param name="edges">Wires between new objects, (from id, to id).</param>
        /// <param name="existing">Boxes of every object already on the canvas.</param>
        /// <returns>Top-left corner for every node without a fixed box.</returns>
        public static Dictionary<int, PointF> Place(IList<Node> nodes, IEnumerable<(int From, int To)> edges, IList<RectangleF> existing)
        {
            var result = new Dictionary<int, PointF>();
            var byId = nodes.ToDictionary(n => n.Id);
            var preds = nodes.ToDictionary(n => n.Id, n => new List<int>());
            foreach (var (from, to) in edges)
            {
                if (from != to && byId.ContainsKey(from) && byId.ContainsKey(to) && !preds[to].Contains(from))
                    preds[to].Add(from);
            }

            var placed = new List<RectangleF>(existing);

            // Inserted components go midway along the wire they were put into.
            foreach (var n in nodes.Where(n => n.Fixed == null && n.InsertSource.HasValue && n.InsertTarget.HasValue))
            {
                var src = n.InsertSource.Value;
                var tgt = n.InsertTarget.Value;
                float x = (src.Right + tgt.Left) / 2f - n.Size.Width / 2f;
                if (x < src.Right + ColumnGap / 2f) x = src.Right + ColumnGap / 2f;
                float y = src.Top;
                var box = Nudge(new RectangleF(x, y, n.Size.Width, n.Size.Height), placed);
                result[n.Id] = box.Location;
                placed.Add(box);
            }

            var auto = nodes.Where(n => n.Fixed == null && !result.ContainsKey(n.Id)).ToList();
            if (auto.Count == 0)
                return result;

            var depth = new Dictionary<int, int>();
            foreach (var n in nodes)
                Depth(n.Id, preds, depth, new HashSet<int>());

            // Anchor: right of the block's upstream objects, else right of the whole canvas.
            var upstream = auto.SelectMany(n => n.Upstream)
                .Concat(nodes.Where(n => n.Fixed.HasValue && auto.Any(a => preds[a.Id].Contains(n.Id))).Select(n => n.Fixed.Value))
                .Concat(nodes.Where(n => result.ContainsKey(n.Id) && auto.Any(a => preds[a.Id].Contains(n.Id)))
                    .Select(n => new RectangleF(result[n.Id], n.Size)))
                .ToList();

            float baseX, baseY;
            if (upstream.Count > 0)
            {
                baseX = upstream.Max(b => b.Right) + ColumnGap;
                baseY = upstream.Min(b => b.Top);
            }
            else if (existing.Count > 0)
            {
                baseX = existing.Max(b => b.Right) + ColumnGap * 2;
                baseY = existing.Min(b => b.Top);
            }
            else
            {
                baseX = 100;
                baseY = 100;
            }

            // Depths are relative to the auto nodes: the shallowest auto node starts column 0.
            int minDepth = auto.Min(n => depth[n.Id]);
            var columns = auto.GroupBy(n => depth[n.Id] - minDepth).OrderBy(g => g.Key).ToList();

            // Consecutive columns; a depth with no auto nodes (a fixed node sits there) leaves no gap.
            var boxes = new Dictionary<int, RectangleF>();
            float x0 = baseX;
            foreach (var column in columns)
            {
                float width = column.Max(n => n.Size.Width);
                float y = baseY;
                foreach (var n in column)
                {
                    boxes[n.Id] = new RectangleF(x0, y, n.Size.Width, n.Size.Height);
                    y += n.Size.Height + RowGap;
                }
                x0 += width + ColumnGap;
            }

            // Move the whole block down until it clears what is already on the canvas.
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var block = Union(boxes.Values);
                var hits = placed.Where(p => Inflate(p, RowGap / 2).IntersectsWith(block)).ToList();
                if (hits.Count == 0) break;
                float dy = hits.Max(h => h.Bottom) + RowGap * 2 - block.Top;
                foreach (var id in boxes.Keys.ToList())
                {
                    var b = boxes[id];
                    boxes[id] = new RectangleF(b.X, b.Y + dy, b.Width, b.Height);
                }
            }

            foreach (var kv in boxes)
                result[kv.Key] = kv.Value.Location;
            return result;
        }

        private static int Depth(int id, Dictionary<int, List<int>> preds, Dictionary<int, int> memo, HashSet<int> visiting)
        {
            if (memo.TryGetValue(id, out var d)) return d;
            if (!visiting.Add(id)) return 0; // a cycle: Grasshopper will report it; don't loop here
            int depth = 0;
            foreach (var p in preds[id])
                depth = Math.Max(depth, Depth(p, preds, memo, visiting) + 1);
            visiting.Remove(id);
            memo[id] = depth;
            return depth;
        }

        private static RectangleF Nudge(RectangleF box, List<RectangleF> placed)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var hit = placed.FirstOrDefault(p => Inflate(p, RowGap / 2).IntersectsWith(box));
                if (hit.IsEmpty) return box;
                box = new RectangleF(box.X, hit.Bottom + RowGap, box.Width, box.Height);
            }
            return box;
        }

        private static RectangleF Union(IEnumerable<RectangleF> boxes)
        {
            var list = boxes.ToList();
            float l = list.Min(b => b.Left), t = list.Min(b => b.Top), r = list.Max(b => b.Right), btm = list.Max(b => b.Bottom);
            return RectangleF.FromLTRB(l, t, r, btm);
        }

        private static RectangleF Inflate(RectangleF r, float by) =>
            RectangleF.FromLTRB(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);
    }
}
