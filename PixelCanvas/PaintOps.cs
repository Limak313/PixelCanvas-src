namespace PixelCanvas;

/// <summary>绘制算法（纯数据操作，与界面无关）：画笔 / 橡皮 / 油漆桶 + 镜像对称。</summary>
public static class PaintOps
{
    /// <summary>在起点所在的连续同色区域中绘制线性渐变；镜像使用原始像素判定区域。</summary>
    public static bool GradientFill(PixelGrid grid, int x0, int y0, int x1, int y1,
        int startColor, int endColor, bool mirrorH, bool mirrorV)
    {
        int width = grid.Width, height = grid.Height;
        int[] original = grid.Snapshot();
        var visited = new bool[original.Length];
        var bestDistance = new int[original.Length];
        Array.Fill(bestDistance, int.MaxValue);
        var queue = new Queue<int>();
        bool changed = false;
        var mirrors = new List<(bool H, bool V)> { (false, false) };
        if (mirrorH) mirrors.Add((true, false));
        if (mirrorV) mirrors.Add((false, true));
        if (mirrorH && mirrorV) mirrors.Add((true, true));

        foreach (var (horizontal, vertical) in mirrors)
        {
            Array.Clear(visited);
            int sx = horizontal ? width - 1 - x0 : x0, sy = vertical ? height - 1 - y0 : y0;
            int ex = horizontal ? width - 1 - x1 : x1, ey = vertical ? height - 1 - y1 : y1;
            int originIndex = sy * width + sx;
            int target = original[originIndex];
            double vx = ex - sx, vy = ey - sy, lengthSquared = vx * vx + vy * vy;
            queue.Enqueue(originIndex);
            visited[originIndex] = true;
            while (queue.Count > 0)
            {
                int index = queue.Dequeue(), x = index % width, y = index / width;
                int distance = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                if (distance < bestDistance[index])
                {
                    bestDistance[index] = distance;
                    double t = lengthSquared == 0 ? 0 : Math.Clamp(((x - sx) * vx + (y - sy) * vy) / lengthSquared, 0, 1);
                    int Blend(int shift)
                    {
                        int a = (int)((uint)startColor >> shift) & 255, b = (int)((uint)endColor >> shift) & 255;
                        return (int)Math.Round(a + (b - a) * t, MidpointRounding.AwayFromZero);
                    }
                    int color = Argb.FromRgb((Blend(16) << 16) | (Blend(8) << 8) | Blend(0));
                    grid.Set(x, y, color);
                }
                void Visit(int nx, int ny)
                {
                    if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) return;
                    int neighbor = ny * width + nx;
                    if (visited[neighbor] || original[neighbor] != target) return;
                    visited[neighbor] = true;
                    queue.Enqueue(neighbor);
                }
                Visit(x + 1, y); Visit(x - 1, y); Visit(x, y + 1); Visit(x, y - 1);
            }
        }
        for (int i = 0; i < original.Length; i++)
            if (original[i] != grid.RawPixels[i]) { changed = true; break; }
        return changed;
    }
    /// <summary>绘制线段或空心形状，端点包含在内。</summary>
    public static bool DrawShape(PixelGrid grid, ToolKind tool, int x0, int y0, int x1, int y1,
        int size, int color, bool mirrorH, bool mirrorV)
    {
        bool changed = false;
        void Plot(int x, int y) => changed |= ApplyBrush(grid, x, y, size, color, mirrorH, mirrorV);
        void Line(int ax, int ay, int bx, int by)
        {
            int dx = Math.Abs(bx - ax), dy = Math.Abs(by - ay);
            int sx = ax < bx ? 1 : -1, sy = ay < by ? 1 : -1, error = dx - dy;
            while (true)
            {
                Plot(ax, ay);
                if (ax == bx && ay == by) break;
                int twice = 2 * error;
                if (twice > -dy) { error -= dy; ax += sx; }
                if (twice < dx) { error += dx; ay += sy; }
            }
        }

        if (tool == ToolKind.Line) Line(x0, y0, x1, y1);
        else if (tool == ToolKind.Rectangle)
        {
            Line(x0, y0, x1, y0); Line(x1, y0, x1, y1);
            Line(x1, y1, x0, y1); Line(x0, y1, x0, y0);
        }
        else if (tool == ToolKind.Ellipse)
        {
            if (x0 == x1 || y0 == y1) Line(x0, y0, x1, y1);
            else
            {
                double cx = (x0 + x1) / 2.0, cy = (y0 + y1) / 2.0;
                double rx = Math.Abs(x1 - x0) / 2.0, ry = Math.Abs(y1 - y0) / 2.0;
                int steps = Math.Max(16, 8 * Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
                for (int i = 0; i < steps; i++)
                {
                    double angle = 2 * Math.PI * i / steps;
                    Plot((int)Math.Round(cx + rx * Math.Cos(angle), MidpointRounding.AwayFromZero),
                         (int)Math.Round(cy + ry * Math.Sin(angle), MidpointRounding.AwayFromZero));
                }
            }
        }
        return changed;
    }
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
