using System;
using System.IO;

namespace Monitor.Core;
/// <summary>
/// Conservative 保守
/// Normal 普通/常规
/// Sensitive 灵敏
/// </summary>
public enum VisualSensitivity
{
    Conservative,
    Normal,
    Sensitive
}

/// <summary>
/// PendingReview 待复合异常
/// StrongEvidence 较强特征异常
/// </summary>
public enum VisualConfidence
{
    PendingReview,
    StrongEvidence
}

public sealed record VisualRegion(AlarmKind Kind, int X, int Y, int Width, int Height, double Score, double AreaPercent, VisualConfidence Confidence, string Detail);
public sealed record VisualEvidence(int Width, int Height, double FrameTime, VisualRegion[] Regions)
{
    public double Score => Regions.Length == 0 ? 0 : Regions.Max(r => r.Score);
    public bool Strong => Regions.Any(r => r.Confidence == VisualConfidence.StrongEvidence);
}

public sealed record VisualFrameMetrics(int Width, int Height, double Pts, bool SceneCut, VisualRegion[] Regions)
{
    public VisualEvidence? For(AlarmKind kind)
    {
        var regions = Regions.Where(r => r.Kind == kind).ToArray();
        return regions.Length == 0 ? null : new(Width, Height, Pts, regions);
    }

    public string Summary => Regions.Length == 0 ? "局部画面：未发现异常" : "局部画面：" + string.Join(" / ", Regions.GroupBy(r => r.Kind).Select(g => $"{Text.Label(g.Key)} {g.Max(r => r.Score):F0}"));
}

public sealed class VisualAnalyzer
{
    public const int TileFloats = 80;
    double[] previous = [];
    double[] previousVariances = [];
    bool[] previousGreen = [];
    double previousPts = double.NegativeInfinity;
    int previousWidth;
    int previousHeight;
    readonly Dictionary<(int X, int Y, int Width, int Height), WindowHistory> windows = [];
    DetectionSettings? lastSettings;
    readonly record struct Grid(double Ratio, double Coverage, double Strength, int Period, int Support, int Phase, double Repetition);
    readonly record struct CrossGrid(double Fraction, int Intersections, int Columns, int Rows);
    sealed record WindowHistory(Grid X, Grid Y, double Cross, double NormalSince, double DirectionalUntil);
    public void Reset()
    {
        previous = [];
        previousVariances = [];
        previousGreen = [];
        windows.Clear();
        lastSettings = null;
        previousPts = double.NegativeInfinity;
    }

    public VisualFrameMetrics Analyze(ReadOnlySpan<float> data, int width, int height, double pts, DetectionSettings settings, bool interlaced = false)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(pts))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int nx = (width + 15) / 16;
        int ny = (height + 15) / 16;
        int count = checked(nx * ny);
        if (data.Length < checked(count * TileFloats))
        {
            throw new ArgumentException("局部检测统计缓冲区长度不匹配。", nameof(data));
        }

        var means = new double[count];
        var variances = new double[count];
        var changed = new bool[count];
        var greenTiles = new bool[count];
        bool temporal = previous.Length == count && width == previousWidth && height == previousHeight && pts > previousPts && pts - previousPts <= Math.Max(1, 3 / settings.AnalysisFps);
        if (!temporal || lastSettings != settings)
        {
            windows.Clear();
        }

        lastSettings = settings;
        double[] oldVariances = previousVariances;
        bool[] oldGreen = previousGreen;
        int validTiles = 0;
        int changedTiles = 0;
        double changeSum = 0;
        double roiPixels = 0;
        for (int i = 0;
            i < count;
            i++)
        {
            var t = data.Slice(i * TileFloats, TileFloats);
            double n = t[2];
            if (n <= 0)
            {
                continue;
            }

            roiPixels += n;
            means[i] = t[0] / n;
            variances[i] = Math.Max(0, t[75] / n - means[i] * means[i]);
            greenTiles[i] = t[76] / n >= .9 && variances[i] < .025 * .025;
            validTiles++;
            if (temporal)
            {
                double delta = Math.Abs(means[i] - previous[i]);
                bool meanChanged = delta > .035;
                changed[i] = meanChanged || t[68] / n > .035;
                if (meanChanged)
                {
                    changedTiles++;
                }

                changeSum += delta;
            }
        }

        bool sceneCut = temporal && changedTiles > validTiles * .65 && changeSum / Math.Max(1, validTiles) > .12;
        if (sceneCut)
        {
            windows.Clear();
        }

        previous = means;
        previousVariances = variances;
        previousGreen = greenTiles;
        previousPts = pts;
        previousWidth = width;
        previousHeight = height;
        if (roiPixels <= 0 || !settings.VisualLocalEnabled)
        {
            return new(width, height, pts, sceneCut, []);
        }

        int roiX = (int)Math.Ceiling(width * settings.RoiX / 100);
        int roiY = (int)Math.Ceiling(height * settings.RoiY / 100);
        int roiRight = Math.Min(width, (int)Math.Ceiling(width * (settings.RoiX + settings.RoiWidth) / 100));
        int roiBottom = Math.Min(height, (int)Math.Ceiling(height * (settings.RoiY + settings.RoiHeight) / 100));
        int tileLeft = roiX / 16;
        int tileTop = roiY / 16;
        int roiNx = (roiRight + 15) / 16 - tileLeft;
        int roiNy = (roiBottom + 15) / 16 - tileTop;
        if (roiNx <= 0 || roiNy <= 0)
        {
            return new(width, height, pts, sceneCut, []);
        }

        var candidates = new List<VisualRegion>();
        if (settings.GreenEnabled)
        {
            AddGreenRegions(candidates, data, greenTiles, changed, oldGreen, oldVariances, nx, ny, width, height, roiPixels, temporal, settings);
        }

        double ratioMinimum = settings.VisualSensitivity switch
        {
            VisualSensitivity.Conservative => 4,
            VisualSensitivity.Sensitive => 2.5,
            _ => 3
        };
        double crossMinimum = settings.VisualSensitivity switch
        {
            VisualSensitivity.Conservative => 2.2,
            VisualSensitivity.Sensitive => 1.55,
            _ => 1.8
        };
        double minimumCoverage = settings.VisualSensitivity switch
        {
            VisualSensitivity.Conservative => .16,
            VisualSensitivity.Sensitive => .07,
            _ => .10
        };
        double minimumCross = settings.VisualSensitivity switch
        {
            VisualSensitivity.Conservative => .50,
            VisualSensitivity.Sensitive => .25,
            _ => .35
        };
        double noiseLimit = settings.VisualSensitivity switch
        {
            VisualSensitivity.Conservative => 40,
            VisualSensitivity.Sensitive => 24,
            _ => 32
        };
        Span<double> xEdges = stackalloc double[256];
        Span<double> yEdges = stackalloc double[256];
        Span<double> xCounts = stackalloc double[256];
        Span<double> yCounts = stackalloc double[256];
        foreach (int nominalTiles in new[]
        {
            8,
            16
        }

        )
        {
            if (!settings.MosaicEnabled && !settings.FlowerEnabled)
            {
                break;
            }

            int wx = Math.Min(roiNx, nominalTiles);
            int wy = Math.Min(roiNy, nominalTiles);
            if (nominalTiles == 16 && roiNx <= 8 && roiNy <= 8)
            {
                continue;
            }

            foreach (int relativeY in Starts(roiNy, wy, Math.Max(1, nominalTiles / 2)))
            {
                foreach (int relativeX in Starts(roiNx, wx, Math.Max(1, nominalTiles / 2)))
                {
                    int ty = tileTop + relativeY;
                    int tx = tileLeft + relativeX;
                    xEdges.Clear();
                    yEdges.Clear();
                    xCounts.Clear();
                    yCounts.Clear();
                    double pixels = 0;
                    double flatPixels = 0;
                    double noise = 0;
                    double colourNoise = 0;
                    double changedPixels = 0;
                    double collapsePixels = 0;
                    double noisyPixels = 0;
                    for (int yy = 0;
                        yy < wy;
                        yy++)
                    {
                        for (int xx = 0;
                            xx < wx;
                            xx++)
                        {
                            int index = (ty + yy) * nx + tx + xx;
                            var t = data.Slice(index * TileFloats, TileFloats);
                            double n = t[2];
                            if (n <= 0)
                            {
                                continue;
                            }

                            pixels += n;
                            noise += t[3];
                            colourNoise += t[77];
                            if (changed[index])
                            {
                                changedPixels += n;
                            }

                            double variance = variances[index];
                            bool flat = variance < .025 * .025;
                            if (flat)
                            {
                                flatPixels += n;
                            }

                            if (temporal && changed[index] && flat && oldVariances[index] > Math.Max(.0015, variance * 4))
                            {
                                collapsePixels += n;
                            }

                            if (t[77] / n * 127.5 >= noiseLimit * .75)
                            {
                                noisyPixels += n;
                            }

                            for (int k = 0;
                                k < 16;
                                k++)
                            {
                                xEdges[xx * 16 + k] += t[4 + k];
                                xCounts[xx * 16 + k] += t[36 + k];
                                int first = ((ty + yy) * 8) % 16;
                                if (!interlaced || k >= first && k < first + 8)
                                {
                                    int row = interlaced ? yy * 8 + k - first : yy * 16 + k;
                                    yEdges[row] += t[20 + k];
                                    yCounts[row] += t[52 + k];
                                }
                            }
                        }
                    }

                    int x = Math.Max(roiX, tx * 16);
                    int y = Math.Max(roiY, ty * 16);
                    int startX = x - tx * 16;
                    int startY = y - ty * 16;
                    int rw = Math.Min(roiRight, (tx + wx) * 16) - x;
                    int rh = Math.Min(roiBottom, (ty + wy) * 16) - y;
                    double area = pixels / roiPixels * 100;
                    if (pixels < rw * rh * .65 || area < settings.VisualMinAreaPercent)
                    {
                        continue;
                    }

                    if (noise / pixels < .0005 && colourNoise / pixels < .001)
                    {
                        continue;
                    }

                    var a = GridEvidence(xEdges.Slice(startX, rw), xCounts.Slice(startX, rw), rh);
                    int fieldRows = interlaced ? (rh + 1) / 2 : rh;
                    int fieldStart = interlaced ? startY / 2 : startY;
                    var b = GridEvidence(yEdges.Slice(fieldStart, fieldRows), yCounts.Slice(fieldStart, fieldRows), interlaced ? rw * 2 : rw);
                    if (interlaced)
                    {
                        b = b with
                        {
                            Period = b.Period * 2,
                            Phase = b.Phase * 2
                        };
                    }

                    double high = Math.Max(a.Ratio, b.Ratio);
                    double low = Math.Min(a.Ratio, b.Ratio);
                    double flatFraction = flatPixels / pixels;
                    double movement = changedPixels / pixels;
                    var cross = CrossEvidence(data, nx, x, y, rw, rh, a, b, interlaced);
                    bool denseBlocks = flatFraction >= .8 && low >= ratioMinimum * 1.5 && a.Coverage >= Math.Max(.15, minimumCoverage) && b.Coverage >= Math.Max(.15, minimumCoverage) && cross.Fraction >= .12;
                    bool crossed = high >= ratioMinimum && low >= crossMinimum && a.Coverage >= minimumCoverage && b.Coverage >= minimumCoverage && a.Repetition >= .6 && b.Repetition >= .6 && cross.Intersections >= 4 && cross.Columns >= 2 && cross.Rows >= 2 && (cross.Fraction >= minimumCross || denseBlocks) && (flatFraction >= .55 || low >= Math.Max(6, crossMinimum * 3)) && (low >= ratioMinimum || flatFraction >= .7 && cross.Fraction >= .6);
                    var key = (tx, ty, wx, wy);
                    windows.TryGetValue(key, out var history);
                    bool stable = history != null && pts - history.NormalSince >= .4 && SameStructure(a, history.X) && SameStructure(b, history.Y) && Math.Abs(cross.Fraction - history.Cross) <= .08;
                    Grid dominant = a.Ratio >= b.Ratio ? a : b;
                    bool directionalShape = high >= ratioMinimum * 2.5 && flatFraction >= .65 && dominant.Coverage >= .25 && dominant.Repetition >= .75 && dominant.Support >= 4 && dominant.Strength >= 6;
                    double directionalUntil = history?.DirectionalUntil ?? double.NegativeInfinity;
                    if (directionalShape && temporal && !sceneCut && collapsePixels / pixels >= .25)
                    {
                        directionalUntil = pts + Math.Max(.15, settings.VisualConfirmWindowSeconds);
                    }

                    bool strongOneDirection = directionalShape && temporal && !sceneCut && pts <= directionalUntil && !stable;
                    if (stable)
                    {
                        crossed = false;
                    }

                    bool blocking = crossed || strongOneDirection;
                    double normalSince = !blocking && history != null && SameStructure(a, history.X) && SameStructure(b, history.Y) ? history.NormalSince : pts;
                    windows[key] = new(a, b, cross.Fraction, normalSince, directionalUntil);
                    if (settings.MosaicEnabled && blocking)
                    {
                        double score = Math.Clamp(45 + 10 * Math.Log2(Math.Max(1, high / ratioMinimum)) + (crossed ? 10 * Math.Log2(Math.Max(1, low / crossMinimum)) : 0) + flatFraction * 10 + (temporal && !sceneCut && movement > .08 ? 6 : 0), 0, 100);
                        bool strong = score >= 90 && temporal && !sceneCut && movement > .08 && crossed && low >= Math.Max(6, crossMinimum * 3) && (cross.Fraction >= .4 || denseBlocks) && a.Coverage >= .2 && b.Coverage >= .2;
                        candidates.Add(new(AlarmKind.Mosaic, x, y, rw, rh, score, area, strong ? VisualConfidence.StrongEvidence : VisualConfidence.PendingReview, $"{(crossed ? "双向重复块边界" : "单向边界伴随纹理突然丢失")}；周期 {a.Period}/{b.Period}px，边界比 {a.Ratio:F2}/{b.Ratio:F2}，边缘覆盖 {a.Coverage:P0}/{b.Coverage:P0}，交叉支持 {cross.Fraction:P0}，平滑块 {flatFraction:P0}；分数不是故障概率，需人工复核"));
                    }

                    double localNoise = noise / pixels * 127.5;
                    double localColour = colourNoise / pixels * 127.5;
                    if (settings.FlowerEnabled && !blocking && localColour >= noiseLimit && noisyPixels / pixels >= .3 && (localColour >= Math.Max(4, localNoise) * 1.4 || temporal && !sceneCut && movement >= .35))
                    {
                        double score = Math.Clamp(45 + (localColour - noiseLimit) * .8 + (temporal && !sceneCut ? movement * 15 : 0), 0, 100);
                        candidates.Add(new(AlarmKind.Flower, x, y, rw, rh, score, area, score >= 85 && temporal && !sceneCut && movement >= .5 ? VisualConfidence.StrongEvidence : VisualConfidence.PendingReview, $"局部纹理/色彩紊乱；色彩边缘 {localColour:F1}，亮度边缘 {localNoise:F1}；节目效果需人工复核"));
                    }
                }
            }
        }

        candidates = candidates.Select(c => c with { X = Math.Max(c.X, roiX), Y = Math.Max(c.Y, roiY), Width = Math.Max(0, Math.Min(c.X + c.Width, roiRight) - Math.Max(c.X, roiX)), Height = Math.Max(0, Math.Min(c.Y + c.Height, roiBottom) - Math.Max(c.Y, roiY)) }).Where(c => c.Width > 0 && c.Height > 0).ToList();
        return new(width, height, pts, sceneCut, Merge(candidates, width, height));
    }

    static IEnumerable<int> Starts(int length, int size, int stride)
    {
        int last = -1;
        for (int p = 0;
            p + size <= length;
            p += stride)
        {
            last = p;
            yield return p;
        }

        if (last != length - size)
        {
            yield return length - size;
        }
    }

    static void AddGreenRegions(List<VisualRegion> output, ReadOnlySpan<float> data, bool[] green, bool[] changed, bool[] oldGreen, double[] oldVariances, int nx, int ny, int width, int height, double roiPixels, bool temporal, DetectionSettings settings)
    {
        var visited = new bool[green.Length];
        var pending = new Queue<int>();
        for (int i = 0;
            i < green.Length;
            i++)
        {
            if (!green[i] || visited[i])
            {
                continue;
            }

            pending.Enqueue(i);
            visited[i] = true;
            double pixels = 0;
            double moving = 0;
            double samples = 0;
            double red = 0;
            double greenSum = 0;
            double blue = 0;
            double colourSquares = 0;
            double newGreen = 0;
            double textured = 0;
            int left = nx;
            int top = ny;
            int right = 0;
            int bottom = 0;
            while (pending.TryDequeue(out int index))
            {
                int x = index % nx;
                int y = index / nx;
                double n = data[index * TileFloats + 76];
                pixels += n;
                var tile = data.Slice(index * TileFloats, TileFloats);
                samples += tile[2];
                red += tile[72];
                greenSum += tile[73];
                blue += tile[74];
                colourSquares += tile[79];
                if (temporal && !oldGreen[index])
                {
                    newGreen += n;
                }

                if (temporal && oldVariances[index] > .0015)
                {
                    textured += n;
                }

                if (changed[index])
                {
                    moving += n;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
                if (x > 0)
                {
                    Visit(index - 1);
                }

                if (x + 1 < nx)
                {
                    Visit(index + 1);
                }

                if (y > 0)
                {
                    Visit(index - nx);
                }

                if (y + 1 < ny)
                {
                    Visit(index + nx);
                }
            }

            double area = pixels / roiPixels * 100;
            if (area < Math.Max(settings.VisualMinAreaPercent, settings.VisualGreenAreaPercent))
            {
                continue;
            }

            double colourVariance = Math.Max(0, colourSquares / Math.Max(1, samples) - (red * red + greenSum * greenSum + blue * blue) / Math.Max(1, samples * samples));
            if (colourVariance > .005)
            {
                continue;
            }

            bool strong = temporal && area >= 10 && newGreen / pixels >= .8 && moving / pixels >= .5 && textured / pixels >= .35;
            output.Add(new(AlarmKind.Green, left * 16, top * 16, Math.Min(width, right * 16) - left * 16, Math.Min(height, bottom * 16) - top * 16, Math.Clamp(50 + pixels / roiPixels * 30 + (temporal && moving / pixels > .15 ? 10 : 0), 0, 100), area, strong ? VisualConfidence.StrongEvidence : VisualConfidence.PendingReview, $"绿色集中且区域内色彩接近，色彩离散度 {Math.Sqrt(colourVariance):F3}；{(strong ? "原有纹理突然被绿色填充；" : "")}可能为正常绿幕、灯光或节目效果，需人工复核"));
        }

        void Visit(int next)
        {
            if (green[next] && !visited[next])
            {
                visited[next] = true;
                pending.Enqueue(next);
            }
        }
    }

    static bool SameStructure(Grid current, Grid reference)
    {
        if (current.Period != reference.Period)
        {
            return false;
        }

        if (current.Period == 0)
        {
            return true;
        }

        int phaseDistance = Math.Abs(current.Phase - reference.Phase);
        phaseDistance = Math.Min(phaseDistance, current.Period - phaseDistance);
        return phaseDistance <= 1 && Math.Abs(current.Coverage - reference.Coverage) <= .05 && Math.Abs(current.Strength - reference.Strength) <= Math.Max(2, reference.Strength * .25);
    }

    static CrossGrid CrossEvidence(ReadOnlySpan<float> data, int nx, int x, int y, int width, int height, Grid horizontal, Grid vertical, bool interlaced)
    {
        if (horizontal.Period == 0 || vertical.Period == 0)
        {
            return default;
        }

        Span<bool> columns = stackalloc bool[64];
        Span<bool> rows = stackalloc bool[64];
        columns.Clear();
        rows.Clear();
        int possible = 0;
        int supported = 0;
        int rowIndex = 0;
        for (int localY = vertical.Phase;
            localY < height;
            localY += vertical.Period)
        {
            int columnIndex = 0;
            for (int localX = horizontal.Phase;
                localX < width;
                localX += horizontal.Period)
            {
                int absoluteX = x + localX;
                int absoluteY = y + localY;
                int tileY = absoluteY / 16;
                int offset = (tileY * nx + absoluteX / 16) * TileFloats;
                var tile = data.Slice(offset, TileFloats);
                int column = absoluteX % 16;
                int row = interlaced ? tileY * 8 % 16 + absoluteY % 16 / 2 : absoluteY % 16;
                double rowPixels = interlaced ? 32 : 16;
                possible++;
                if (tile[36 + column] >= 3 && tile[52 + row] >= rowPixels * .18 && tile[4 + column] >= 16 * 3d / 255 && tile[20 + row] >= rowPixels * 3d / 255)
                {
                    supported++;
                    columns[columnIndex] = true;
                    rows[rowIndex] = true;
                }

                columnIndex++;
            }

            rowIndex++;
        }

        int supportedColumns = 0;
        int supportedRows = 0;
        foreach (bool present in columns)
        {
            if (present)
            {
                supportedColumns++;
            }
        }

        foreach (bool present in rows)
        {
            if (present)
            {
                supportedRows++;
            }
        }

        return new(supported / (double)Math.Max(1, possible), supported, supportedColumns, supportedRows);
    }

    static Grid GridEvidence(ReadOnlySpan<double> edges, ReadOnlySpan<double> counts, int crossSize)
    {
        double total = 0;
        foreach (double value in edges)
        {
            total += value;
        }

        Grid best = default;
        Span<double> phase = stackalloc double[64];
        ReadOnlySpan<int> periods = [4, 8, 16, 32, 64];
        foreach (int period in periods)
        {
            if (edges.Length / period < 3)
            {
                continue;
            }

            phase.Clear();
            for (int i = 0;
                i < edges.Length;
                i++)
            {
                phase[i % period] += edges[i];
            }

            int peak = 0;
            for (int k = 1;
                k < period;
                k++)
            {
                if (phase[k] > phase[peak])
                {
                    peak = k;
                }
            }

            int lines = 0;
            int support = 0;
            double strong = 0;
            for (int k = peak;
                k < edges.Length;
                k += period)
            {
                lines++;
                strong += counts[k];
                if (edges[k] / crossSize > 2d / 255 && counts[k] / crossSize >= .04)
                {
                    support++;
                }
            }

            double boundary = phase[peak] / Math.Max(1, lines * crossSize);
            double inside = (total - phase[peak]) / Math.Max(1, (edges.Length - lines) * crossSize);
            double ratio = boundary / (inside + .0008);
            double coverage = strong / Math.Max(1, lines * crossSize);
            if (support >= 3 && coverage >= .04 && boundary > 3d / 255 && ratio > best.Ratio)
            {
                best = new(ratio, coverage, boundary * 255, period, support, peak, support / (double)lines);
            }
        }

        return best;
    }

    static VisualRegion[] Merge(List<VisualRegion> candidates, int width, int height)
    {
        var result = new List<VisualRegion>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
        {
            if (result.Any(r => r.Kind == candidate.Kind && Overlap(r, candidate) > .45))
            {
                continue;
            }

            if (result.Count(r => r.Kind == candidate.Kind) >= 4)
            {
                continue;
            }

            result.Add(candidate with { X = Math.Clamp(candidate.X, 0, width - 1), Y = Math.Clamp(candidate.Y, 0, height - 1) });
        }

        return result.ToArray();
    }

    static double Overlap(VisualRegion a, VisualRegion b)
    {
        double intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / Math.Max(1, Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height));
    }
}

public sealed class AnalysisSchedule
{
    double next = double.NaN;
    double last = double.NegativeInfinity;
    public void Reset()
    {
        next = double.NaN;
        last = double.NegativeInfinity;
    }

    public bool Due(double pts, double fps)
    {
        if (!double.IsFinite(pts) || !double.IsFinite(fps) || fps <= 0)
        {
            return false;
        }

        double interval = 1 / fps;
        if (double.IsNaN(next) || pts < last || pts - last > Math.Max(1, interval * 3))
        {
            next = pts;
        }

        last = pts;
        if (pts + 1e-7 < next)
        {
            return false;
        }

        next += Math.Max(1, Math.Floor((pts + 1e-7 - next) / interval) + 1) * interval;
        if (next <= pts)
        {
            next = Math.BitIncrement(pts);
        }

        return true;
    }
}
