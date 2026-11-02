namespace PixelCanvas;

/// <summary>绘制算法（纯数据操作，与界面无关）：画笔 / 橡皮 / 油漆桶 + 镜像对称。</summary>
public static class PaintOps
{
    /// <summary>
    /// 计算 (x, y) 在镜像开关下的全部落点：水平镜像 → (宽-1-x, y)，垂直镜像 → (x, 高-1-y)，
    /// 双开共 4 点；去重并仅保留界内点。
    /// </summary>
    public static List<(int X, int Y)> MirrorPoints(PixelGrid grid, int x, int y, bool mirrorH, bool mirrorV)
    {
        var points = new List<(int, int)>(4);
        void Add(int px, int py)
        {
            if (!grid.InBounds(px, py)) return;
            if (!points.Contains((px, py))) points.Add((px, py));
        }
        int mx = grid.Width - 1 - x;
        int my = grid.Height - 1 - y;
        Add(x, y);
        if (mirrorH) Add(mx, y);
        if (mirrorV) Add(x, my);
        if (mirrorH && mirrorV) Add(mx, my);
        return points;
    }

    /// <summary>
    /// 画笔/橡皮：以 (cx, cy) 为左上角的 size×size 方块（越界部分忽略），
    /// 每个被覆盖格子按镜像规则同时作用于对称位置。返回是否有像素变化。
    /// </summary>
    public static bool ApplyBrush(PixelGrid grid, int cx, int cy, int size, int argb, bool mirrorH, bool mirrorV)
    {
        bool changed = false;
        for (int dy = 0; dy < size; dy++)
        {
            for (int dx = 0; dx < size; dx++)
            {
                int px = cx + dx, py = cy + dy;
                if (!grid.InBounds(px, py)) continue;      // 越界部分忽略
                foreach (var (mx, my) in MirrorPoints(grid, px, py, mirrorH, mirrorV))
                {
                    if (grid.Get(mx, my) != argb)
                    {
                        grid.Set(mx, my, argb);
                        changed = true;
                    }
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// 油漆桶：从 (x, y) 及其各镜像起点分别做一次 4 连通泛洪填充；
    /// 透明视为一种颜色；某起点被填充颜色与目标相同时该次不执行。返回是否有像素变化。
    /// </summary>
    public static bool FloodFill(PixelGrid grid, int x, int y, int argb, bool mirrorH, bool mirrorV)
    {
        var starts = MirrorPoints(grid, x, y, mirrorH, mirrorV);
        // 先记录各起点原始颜色：前面的填充可能覆盖到后面的起点区域
        var originals = new List<int>(starts.Count);
        foreach (var (sx, sy) in starts)
            originals.Add(grid.Get(sx, sy));

        bool changed = false;
        for (int i = 0; i < starts.Count; i++)
        {
            if (FillOne(grid, starts[i].X, starts[i].Y, originals[i], argb))
                changed = true;
        }
        return changed;
    }

    /// <summary>单起点 BFS 泛洪：把与 original 相同的 4 连通区域改为 argb。</summary>
    private static bool FillOne(PixelGrid grid, int x, int y, int original, int argb)
    {
        if (original == argb) return false;                 // 目标颜色与被填充颜色相同：不执行
        var visited = new bool[grid.Width * grid.Height];
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((x, y));
        visited[y * grid.Width + x] = true;
        bool changed = false;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            grid.Set(cx, cy, argb);
            changed = true;
            TryVisit(cx + 1, cy);
            TryVisit(cx - 1, cy);
            TryVisit(cx, cy + 1);
            TryVisit(cx, cy - 1);
        }
        return changed;

        void TryVisit(int px, int py)
        {
            if (!grid.InBounds(px, py)) return;
            int idx = py * grid.Width + px;
            if (visited[idx] || grid.Get(px, py) != original) return;
            visited[idx] = true;
            queue.Enqueue((px, py));
        }
    }
}
