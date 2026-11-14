namespace Monitor.Core;

public sealed class GrayScreenAnalyzer
{
    bool[] previousGray = [];
    bool[] previousTexture = [];
    double previousPts = double.NegativeInfinity;
    int previousWidth;
    int previousHeight;
    DetectionSettings? previousSettings;
    public void Reset()
    {
        previousGray = [];
        previousTexture = [];
        previousPts = double.NegativeInfinity;
        previousWidth = 0;
        previousHeight = 0;
        previousSettings = null;
    }

    public VisualEvidence? Analyze(ReadOnlySpan<float> data, int width, int height, double pts, DetectionSettings settings)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(pts))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int nx = checked((width + 15) / 16);
        int ny = checked((height + 15) / 16);
        int count = checked(nx * ny);
        if (data.Length < checked(count * VisualAnalyzer.TileFloats))
        {
            throw new ArgumentException("灰屏检测统计缓冲区长度不匹配。", nameof(data));
        }

        if (!settings.GrayEnabled)
        {
            Reset();
            return null;
        }

        bool temporal = previousGray.Length == count && previousWidth == width && previousHeight == height && previousSettings == settings && pts > previousPts && pts - previousPts <= Math.Max(1, 3 / settings.AnalysisFps);
        var neutral = new bool[count];
        var texture = new bool[count];
        double roiPixels = 0;
        double chromaLimit = settings.GrayChromaLimit / 255;
        double deviation = settings.GrayUniformity / 255;
        double varianceLimit = deviation * deviation;
        for (int index = 0;
            index < count;
            index++)
        {
            var tile = data.Slice(index * VisualAnalyzer.TileFloats, VisualAnalyzer.TileFloats);
            double n = tile[2];
            if (n <= 0)
            {
                continue;
            }

            roiPixels += n;
            double red = tile[72] / n;
            double green = tile[73] / n;
            double blue = tile[74] / n;
            double luma = tile[0] / n;
            double lumaVariance = Math.Max(0, tile[75] / n - luma * luma);
            double rgbVariance = Math.Max(0, (tile[79] / n - red * red - green * green - blue * blue) / 3);
            double chroma = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
            neutral[index] = luma * 255 >= settings.GrayMinLevel && luma * 255 <= settings.GrayMaxLevel && chroma <= chromaLimit && lumaVariance <= varianceLimit && rgbVariance <= varianceLimit;
            texture[index] = Math.Max(lumaVariance, rgbVariance) > Math.Max(.0015, varianceLimit * 4);
        }

        var regions = new List<VisualRegion>();
        var visited = new bool[count];
        var pending = new Queue<int>();
        for (int seed = 0;
            seed < count && roiPixels > 0;
            seed++)
        {
            if (!neutral[seed] || visited[seed])
            {
                continue;
            }

            pending.Enqueue(seed);
            visited[seed] = true;
            double pixels = 0;
            double red = 0;
            double green = 0;
            double blue = 0;
            double luma = 0;
            double lumaSquares = 0;
            double colourSquares = 0;
            double newArea = 0;
            double oldTexture = 0;
            double changedArea = 0;
            int left = nx;
            int top = ny;
            int right = 0;
            int bottom = 0;
            while (pending.TryDequeue(out int index))
            {
                var tile = data.Slice(index * VisualAnalyzer.TileFloats, VisualAnalyzer.TileFloats);
                double n = tile[2];
                pixels += n;
                luma += tile[0];
                red += tile[72];
                green += tile[73];
                blue += tile[74];
                lumaSquares += tile[75];
                colourSquares += tile[79];
                if (temporal)
                {
                    if (!previousGray[index])
                    {
                        newArea += n;
                    }

                    if (previousTexture[index])
                    {
                        oldTexture += n;
                    }

                    if (tile[68] / n >= .04)
                    {
                        changedArea += n;
                    }
                }

                int x = index % nx;
                int y = index / nx;
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
            if (area + 1e-7 < settings.GrayMinAreaPercent)
            {
                continue;
            }

            double lumaVariance = Math.Max(0, lumaSquares / pixels - luma * luma / (pixels * pixels));
            double rgbVariance = Math.Max(0, (colourSquares / pixels - (red * red + green * green + blue * blue) / (pixels * pixels)) / 3);
            double chroma = (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue))) / pixels;
            if (lumaVariance > varianceLimit || rgbVariance > varianceLimit || chroma > chromaLimit)
            {
                continue;
            }

            bool strong = temporal && area >= 20 && newArea / pixels >= .8 && oldTexture / pixels >= .35 && changedArea / pixels >= .6;
            double sigma = Math.Sqrt(Math.Max(lumaVariance, rgbVariance)) * 255;
            int roiLeft = (int)Math.Ceiling(width * settings.RoiX / 100);
            int roiTop = (int)Math.Ceiling(height * settings.RoiY / 100);
            int roiRight = Math.Min(width, (int)Math.Ceiling(width * (settings.RoiX + settings.RoiWidth) / 100));
            int roiBottom = Math.Min(height, (int)Math.Ceiling(height * (settings.RoiY + settings.RoiHeight) / 100));
            int x0 = Math.Max(roiLeft, left * 16);
            int y0 = Math.Max(roiTop, top * 16);
            int x1 = Math.Min(roiRight, right * 16);
            int y1 = Math.Min(roiBottom, bottom * 16);
            if (x1 <= x0 || y1 <= y0)
            {
                continue;
            }

            regions.Add(new(AlarmKind.Gray, x0, y0, x1 - x0, y1 - y0, Math.Clamp(50 + area * .25 + (strong ? 20 : 0) - sigma / settings.GrayUniformity * 5, 0, 100), area, strong ? VisualConfidence.StrongEvidence : VisualConfidence.PendingReview, $"低色度均匀灰色区域，覆盖ROI {area:F2}%，亮度 {luma / pixels * 255:F1}，中性色差 {chroma * 255:F2}，区域离散度 {sigma:F2}（0～255归一化）；{(strong ? "原纹理突然被灰色填充；" : "")}正常灰色背景也可能满足条件，需人工复核"));
        }

        previousGray = neutral;
        previousTexture = texture;
        previousPts = pts;
        previousWidth = width;
        previousHeight = height;
        previousSettings = settings;
        var selected = regions.OrderByDescending(region => region.AreaPercent).Take(4).ToArray();
        return selected.Length == 0 ? null : new(width, height, pts, selected);
        void Visit(int index)
        {
            if (neutral[index] && !visited[index])
            {
                visited[index] = true;
                pending.Enqueue(index);
            }
        }
    }
}
