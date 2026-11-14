namespace Monitor.Core;
public sealed class AlarmLatch(AlarmKind kind, double trigger, double recovery)
{
    public double? BadSince { get;
        private set;
    }
    public double? Confirmed { get;
        private set;
    }

    double? goodSince;
    Guid? evidence;
    string detail = "";
    public Guid? EvidenceId => evidence;

    public AlarmTransition? Update(bool bad, double pts, string text = "", Guid? image = null)
    {
        if (bad)
        {
            goodSince = null;
            if (BadSince is null)
            {
                BadSince = pts;
                evidence = image;
            }

            detail = text;
            if (Confirmed is null && pts - BadSince.Value + 1e-7 >= trigger)
            {
                Confirmed = pts;
                return new(kind, true, BadSince.Value, pts, Detail: detail, Evidence: evidence);
            }
        }
        else
        {
            if (Confirmed is null)
            {
                BadSince = null;
                evidence = null;
            }
            else
            {
                goodSince ??= pts;
                if (pts - goodSince.Value + 1e-7 >= recovery)
                {
                    return Close(goodSince.Value, "recovered");
                }
            }
        }

        return null;
    }

    public AlarmTransition? Close(double pts, string reason)
    {
        AlarmTransition? result = Confirmed is { } c ? new(kind, false, BadSince!.Value, c, Math.Max(BadSince.Value, pts), detail, reason, evidence) : null;
        BadSince = Confirmed = goodSince = null;
        evidence = null;
        return result;
    }

    public void SetOnset(double pts, Guid? image)
    {
        if (BadSince is null)
        {
            BadSince = pts;
        }

        if (evidence is null && image is not null)
        {
            evidence = image;
        }
    }
}

public sealed class Detector
{
    readonly Dictionary<AlarmKind, AlarmLatch> latches = [];
    readonly Dictionary<AlarmKind, VisualEventLatch> visualLatches = [];
    readonly GrayScreenLatch? grayLatch;
    readonly Dictionary<AlarmKind, double> pulses = [];
    double[]? previous;
    double[]? reference;
    double lastSample = double.NegativeInfinity;
    double lastLocalMotion = double.NegativeInfinity;
    double? audioEnd;
    public DetectionSettings Settings { get;
    }
    public VideoMetrics? Metrics { get;
        private set;
    }
    public ChannelLevel[] Levels { get;
        private set;
    } = [];
    public AlarmKind[] Active => latches.Where(p => p.Value.Confirmed is not null).Select(p => p.Key).Concat(visualLatches.Where(p => p.Value.Active).Select(p => p.Key)).Concat(grayLatch?.Active == true ? new[] { AlarmKind.Gray } : []).Distinct().ToArray();
    public Guid[] EvidenceIds => latches.Values.Select(l => l.EvidenceId).OfType<Guid>().Concat(visualLatches.Values.SelectMany(l => l.EvidenceIds)).Concat(grayLatch?.EvidenceIds ?? []).Distinct().ToArray();

    public IReadOnlyList<AlarmTransition> ResetAudio(double pts)
    {
        audioEnd = null;
        Levels = [];
        return new[]
        {
            AlarmKind.Silence,
            AlarmKind.AudioGap,
            AlarmKind.AudioLoss
        }.Where(latches.ContainsKey).Select(k => latches[k].Close(pts, "track")).OfType<AlarmTransition>().ToArray();
    }

    public Detector(DetectionSettings settings)
    {
        settings.Validate();
        Settings = settings;
        grayLatch = settings.GrayEnabled ? new GrayScreenLatch(settings) : null;
        if (settings.VisualLocalEnabled)
        {
            foreach (var(kind, enabled)in new[]
            {
                (AlarmKind.Mosaic, settings.MosaicEnabled),
                (AlarmKind.Flower, settings.FlowerEnabled),
                (AlarmKind.Green, settings.GreenEnabled)
            }

            )
                if (enabled)
                {
                    visualLatches[kind] = new(kind, settings);
                }
        }

        foreach (var(kind, enabled, duration)in new[]
        {
            (AlarmKind.Black, settings.BlackEnabled, settings.BlackSeconds),
            (AlarmKind.Freeze, settings.FreezeEnabled, settings.FreezeSeconds),
            (AlarmKind.Mosaic, settings.MosaicEnabled, settings.MosaicSeconds),
            (AlarmKind.Flower, settings.FlowerEnabled, settings.FlowerSeconds),
            (AlarmKind.Silence, settings.SilenceEnabled, settings.SilenceSeconds),
            (AlarmKind.AudioGap, settings.AudioGapEnabled, 0d),
            (AlarmKind.Decode, settings.DecodeEnabled, 0d),
            (AlarmKind.VideoLoss, settings.LossEnabled, settings.LossSeconds),
            (AlarmKind.AudioLoss, settings.LossEnabled, settings.LossSeconds)
        }

        )
            if (enabled)
            {
                latches[kind] = new(kind, duration, settings.RecoverySeconds);
            }
    }

    void Update(List<AlarmTransition> output, AlarmKind kind, bool bad, double pts, string detail = "", Guid? evidence = null)
    {
        if (latches.TryGetValue(kind, out var latch) && latch.Update(bad, pts, detail, evidence)is { } c)
        {
            output.Add(c);
        }
    }

    public IReadOnlyList<AlarmTransition> Video(double pts, VideoMetrics metrics, Guid? evidence = null)
    {
        var output = new List<AlarmTransition>();
        if (pts - lastSample > Math.Max(1, 3 / Settings.AnalysisFps))
        {
            if (grayLatch?.Close(lastSample, "sample_gap")is { } grayClosed)
            {
                output.Add(grayClosed);
            }

            previous = reference = null;
            lastLocalMotion = double.NegativeInfinity;
            foreach (var visual in visualLatches.Values)
            {
                if (visual.Close(lastSample, "sample_gap")is { } closed)
                {
                    output.Add(closed);
                }
            }

            foreach (var k in new[]
            {
                AlarmKind.Black,
                AlarmKind.Freeze,
                AlarmKind.Mosaic,
                AlarmKind.Flower
            }

            )
            {
                if (latches.TryGetValue(k, out var latch) && latch.Close(pts, "sample_gap")is { } c)
                {
                    output.Add(c);
                }
            }
        }

        lastSample = pts;
        double difference = 255;
        double drift = 255;
        if (metrics.Thumbnail.Length > 0 && previous?.Length == metrics.Thumbnail.Length && reference?.Length == metrics.Thumbnail.Length)
        {
            difference = Difference(previous, metrics.Thumbnail);
            drift = Difference(reference!, metrics.Thumbnail);
        }

        bool black = metrics.BlackRatio >= Settings.BlackRatio;
        var local = metrics.LocalMotion;
        bool localEnabled = Settings.FreezeLocalMotionProtection && local != null;
        if (localEnabled && local!.HasPrevious == false)
        {
            lastLocalMotion = double.NegativeInfinity;
        }

        bool localMoving = localEnabled && ((local!.HasPrevious && local.PreviousMovingAreaPercent >= Settings.FreezeMotionAreaPercent) || (local.HasReference && local.ReferenceMovingAreaPercent >= Settings.FreezeMotionAreaPercent));
        if (localMoving)
        {
            lastLocalMotion = pts;
        }

        bool heldMotion = localEnabled && pts - lastLocalMotion <= Settings.FreezeMotionHoldSeconds + 1e-7;
        bool freeze = !black && Math.Max(difference, drift) <= Settings.FreezeDifference && !heldMotion;
        if (!freeze || reference is null)
        {
            reference = (double[])metrics.Thumbnail.Clone();
        }

        previous = (double[])metrics.Thumbnail.Clone();
        Metrics = metrics with
        {
            Difference = difference
        };
        Update(output, AlarmKind.Black, black, pts, $"暗像素 {metrics.BlackRatio:F2}%", evidence);
        string freezeDetail = $"帧差 {difference:F3}，累计差 {drift:F3}";
        if (localEnabled)
        {
            freezeDetail += $"；运动块覆盖 {local!.PreviousMovingAreaPercent:F4}% / 参考 {local.ReferenceMovingAreaPercent:F4}%；像素帧差 {local.PreviousPixelDifference:F3} / {local.ReferencePixelDifference:F3}";
        }
        else
        {
            freezeDetail += Settings.FreezeLocalMotionProtection ? "；局部统计未提供" : "；局部保护关闭";
        }

        Update(output, AlarmKind.Freeze, freeze, pts, freezeDetail, evidence);
        if (grayLatch?.Update(black ? null : metrics.Gray, pts, evidence)is { } grayChange)
        {
            output.Add(grayChange);
        }

        if (Settings.VisualLocalEnabled && metrics.Visual is { } picture)
        {
            foreach (var(kind, latch)in visualLatches)
                if (latch.Update(black ? null : picture.For(kind), pts, evidence)is { } c)
                {
                    output.Add(c);
                }
        }
        else
        {
            Update(output, AlarmKind.Mosaic, !black && metrics.BlockScore >= Settings.MosaicScore, pts, $"块边界 {metrics.BlockScore:F2}；人工复核", evidence);
            Update(output, AlarmKind.Flower, !black && metrics.NoiseScore >= Settings.FlowerScore, pts, $"空间噪声 {metrics.NoiseScore:F2}；人工复核", evidence);
        }

        return output;
    }

    static double Difference(double[] a, double[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average() * 255;
    public IReadOnlyList<AlarmTransition> Audio(double pts, ReadOnlySpan<float> pcm, int channels, int rate, string[] names)
    {
        var output = new List<AlarmTransition>();
        if (channels < 1 || pcm.Length == 0)
        {
            return output;
        }

        var levels = new ChannelLevel[channels];
        for (int c = 0;
            c < channels;
            c++)
        {
            double sum = 0;
            double peak = 0;
            int count = 0;
            for (int i = c;
                i < pcm.Length;
                i += channels)
            {
                double v = pcm[i];
                sum += v * v;
                peak = Math.Max(peak, Math.Abs(v));
                count++;
            }

            levels[c] = new(c < names.Length ? names[c] : $"CH{c + 1}", 20 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sum / Math.Max(1, count)))), 20 * Math.Log10(Math.Max(1e-6, peak)));
        }

        Levels = levels;
        if (audioEnd is { } end && (pts - end > Settings.AudioGapSeconds || pts - end < -.05))
        {
            output.AddRange(Pulse(AlarmKind.AudioGap, pts, $"PCM 时间间断 {pts - end:+0.000;-0.000}s；人工复核"));
            if (latches.TryGetValue(AlarmKind.Silence, out var latch) && latch.Close(end, "sample_gap")is { } closed)
            {
                output.Add(closed);
            }
        }

        audioEnd = pts + pcm.Length / (double)(channels * rate);
        var selected = Settings.SilentChannels.Where(c => c < channels).ToArray();
        bool bad = selected.Length == 0 ? levels.All(l => l.RmsDb <= Settings.SilenceDb) : selected.Any(c => levels[c].RmsDb <= Settings.SilenceDb);
        Update(output, AlarmKind.Silence, bad, pts, string.Join(" / ", levels.Select(l => $"{l.Name} {l.RmsDb:F1}dBFS")));
        return output;
    }

    public IReadOnlyList<AlarmTransition> Pulse(AlarmKind kind, double pts, string detail, Guid? image = null)
    {
        pulses[kind] = pts;
        var output = new List<AlarmTransition>();
        Update(output, kind, true, pts, detail, image);
        return output;
    }

    public IReadOnlyList<AlarmTransition> Tick(double pts, double lastVideoEnd, double? lastAudioEnd, Guid? lastImage = null)
    {
        var output = new List<AlarmTransition>();
        if (double.IsFinite(lastSample) && pts - lastSample > Math.Max(1, 3 / Settings.AnalysisFps))
        {
            foreach (var visualLatch in visualLatches.Values)
            {
                if (visualLatch.Close(lastSample, "sample_gap")is { } c)
                {
                    output.Add(c);
                }
            }

            foreach (var k in new[]
            {
                AlarmKind.Black,
                AlarmKind.Freeze,
                AlarmKind.Mosaic,
                AlarmKind.Flower
            }

            )
            {
                if (latches.TryGetValue(k, out var visual) && visual.Close(lastSample, "sample_gap")is { } closed)
                {
                    output.Add(closed);
                }
            }

            if (grayLatch?.Close(lastSample, "sample_gap")is { } grayClosed)
            {
                output.Add(grayClosed);
            }

            previous = reference = null;
            lastLocalMotion = double.NegativeInfinity;
            lastSample = double.NegativeInfinity;
        }

        foreach (var p in pulses.ToArray())
        {
            if (pts > p.Value + .1)
            {
                Update(output, p.Key, false, pts);
            }
        }

        foreach (var(kind, end)in new[]
        {
            (AlarmKind.VideoLoss, (double? )lastVideoEnd),
            (AlarmKind.AudioLoss, lastAudioEnd)
        }

        )
            if (end is { } e && latches.TryGetValue(kind, out var latch))
            {
                bool bad = pts - e > .3;
                if (bad)
                {
                    latch.SetOnset(e, lastImage);
                }

                if (latch.Update(bad, pts, "连续未收到可播放帧；截图为最后有效画面。", kind == AlarmKind.VideoLoss ? lastImage : null)is { } c)
                {
                    output.Add(c);
                }
            }

        return output;
    }

    public IReadOnlyList<AlarmTransition> Reset(double pts, string reason)
    {
        var output = latches.Values.Select(l => l.Close(pts, reason)).Concat(visualLatches.Values.Select(l => l.Close(pts, reason))).Concat(new[] { grayLatch?.Close(pts, reason) }).OfType<AlarmTransition>().ToArray();
        previous = reference = null;
        lastLocalMotion = double.NegativeInfinity;
        lastSample = double.NegativeInfinity;
        audioEnd = null;
        pulses.Clear();
        Metrics = null;
        Levels = [];
        return output;
    }
}

public sealed class PlaybackClock
{
    readonly object gate = new();
    readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
    double basis;
    double anchor;
    bool paused = true;
    public double Position
    {
        get
        {
            lock (gate)
            {
                return basis + (paused ? 0 : stopwatch.Elapsed.TotalSeconds - anchor);
            }
        }
    }

    public bool Paused
    {
        get
        {
            lock (gate)
            {
                return paused;
            }
        }
    }

    public void Seek(double value)
    {
        lock (gate)
        {
            basis = Math.Max(0, value);
            anchor = stopwatch.Elapsed.TotalSeconds;
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            basis = Position;
            paused = true;
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (!paused)
            {
                return;
            }

            anchor = stopwatch.Elapsed.TotalSeconds;
            paused = false;
        }
    }

    public async Task WaitAsync(double pts, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double delay = pts - Position;
            if (!Paused && delay <= .002)
            {
                return;
            }

            await Task.Delay(Paused ? 15 : (int)Math.Clamp(delay * 1000, 1, 20), token);
        }
    }
}
