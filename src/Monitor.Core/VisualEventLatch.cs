namespace Monitor.Core;
public sealed class VisualEventLatch(AlarmKind kind, DetectionSettings settings)
{
    sealed record Sample(VisualEvidence Evidence, Guid? Image);
    sealed class RegionTrack
    {
        public readonly Queue<double> Hits = [];
        public Sample? First;
        public Sample? Peak;
        public VisualRegion Last = null!;
        public bool Confirmed;
        public double Start;
        public double? GoodSince;
        public void Trim(double cutoff)
        {
            while (Hits.TryPeek(out double old) && old < cutoff)
            {
                Hits.Dequeue();
            }

            if (!Confirmed && First?.Evidence.FrameTime < cutoff)
            {
                First = null;
            }

            if (!Confirmed && Peak?.Evidence.FrameTime < cutoff)
            {
                Peak = null;
            }
        }
    }

    readonly List<RegionTrack> tracks = [];
    readonly double window = Math.Max(settings.VisualConfirmWindowSeconds, (settings.VisualMinimumHits - 1) / settings.AnalysisFps + 1e-7);
    double? start;
    double? confirmed;
    double? goodSince;
    double previousPts = double.NegativeInfinity;
    int width;
    int height;
    Sample? first;
    Sample? peak;
    public bool Active => confirmed.HasValue;
    public IEnumerable<Guid> EvidenceIds => tracks.SelectMany(track => new[] { track.First?.Image, track.Peak?.Image }).Concat([first?.Image, peak?.Image]).OfType<Guid>().Distinct();

    public AlarmTransition? Update(VisualEvidence? observation, double pts, Guid? image)
    {
        if (!double.IsFinite(pts) || pts <= previousPts)
        {
            return null;
        }

        if (observation != null && width > 0 && (observation.Width != width || observation.Height != height))
        {
            if (Active)
            {
                return Close(pts, "format");
            }

            Clear();
        }

        previousPts = pts;
        if (observation != null)
        {
            width = observation.Width;
            height = observation.Height;
        }

        double cutoff = pts - window - 1e-7;
        foreach (var track in tracks)
        {
            track.Trim(cutoff);
        }

        tracks.RemoveAll(track => !track.Confirmed && track.Hits.Count == 0);
        var matched = new HashSet<RegionTrack>();
        foreach (var region in observation?.Regions.Where(region => region.Kind == kind).OrderByDescending(region => region.Score).ToArray() ?? [])
        {
            var track = tracks.Where(candidate => !matched.Contains(candidate)).Select(candidate => (Track: candidate, Overlap: Overlap(candidate.Last, region))).Where(candidate => candidate.Overlap >= .4).OrderByDescending(candidate => candidate.Overlap).Select(candidate => candidate.Track).FirstOrDefault();
            if (track == null)
            {
                if (tracks.Count >= 8)
                {
                    var expired = tracks.Where(candidate => !candidate.Confirmed && !matched.Contains(candidate)).OrderBy(candidate => candidate.Hits.Count).FirstOrDefault();
                    if (expired == null)
                    {
                        continue;
                    }

                    tracks.Remove(expired);
                }

                track = new RegionTrack();
                tracks.Add(track);
            }

            matched.Add(track);
            track.Last = region;
            track.GoodSince = null;
            var sample = new Sample(new(observation!.Width, observation.Height, pts, [region]), image);
            track.First ??= sample;
            if (Prefer(sample, track.Peak))
            {
                track.Peak = sample;
            }

            if (!track.Confirmed)
            {
                track.Hits.Enqueue(pts);
                if (track.Hits.Count >= settings.VisualMinimumHits || settings.VisualRecordBrief && sample.Evidence.Strong)
                {
                    track.Start = track.Hits.Peek();
                    track.Confirmed = true;
                    track.Hits.Clear();
                }
            }
        }

        foreach (var track in tracks.Where(track => track.Confirmed && !matched.Contains(track)))
        {
            track.GoodSince ??= pts;
        }

        tracks.RemoveAll(track => track.Confirmed && track.GoodSince.HasValue && pts - track.GoodSince.Value + 1e-7 >= settings.RecoverySeconds);
        var observed = tracks.Where(track => track.Confirmed && matched.Contains(track)).ToArray();
        if (observed.Length > 0)
        {
            goodSince = null;
            if (!Active)
            {
                var winner = observed.OrderBy(track => track.Start).ThenByDescending(track => track.Peak!.Evidence.Score).First();
                start = winner.Start;
                confirmed = pts;
                first = winner.First;
                peak = winner.Peak;
                return Transition(true, null, "active");
            }

            var sample = new Sample(new(observation!.Width, observation.Height, pts, observed.Select(track => track.Last).ToArray()), image);
            if (Prefer(sample, peak))
            {
                peak = sample;
            }
        }
        else
        {
            if (Active)
            {
                goodSince ??= pts;
                if (pts - goodSince.Value + 1e-7 >= settings.RecoverySeconds)
                {
                    return Close(goodSince.Value, "recovered");
                }
            }
        }

        return null;
    }

    static bool Prefer(Sample current, Sample? previous)
    {
        return previous == null || current.Image.HasValue && (!previous.Image.HasValue || current.Evidence.Score > previous.Evidence.Score + .5 && (current.Evidence.FrameTime - previous.Evidence.FrameTime >= .2 || current.Evidence.Score > previous.Evidence.Score + 20));
    }

    static double Overlap(VisualRegion first, VisualRegion second)
    {
        double firstArea = (double)first.Width * first.Height;
        double secondArea = (double)second.Width * second.Height;
        if (firstArea <= 0 || secondArea <= 0 || Math.Min(firstArea, secondArea) / Math.Max(firstArea, secondArea) < .2)
        {
            return 0;
        }

        double intersection = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X)) * Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        return intersection / Math.Min(firstArea, secondArea);
    }

    public AlarmTransition? Close(double pts, string reason)
    {
        var result = Active ? Transition(false, Math.Max(start!.Value, pts), reason) : null;
        Clear();
        return result;
    }

    AlarmTransition Transition(bool enter, double? end, string reason)
    {
        var best = peak ?? first!;
        string regions = string.Join("；", best.Evidence.Regions.Select(region => $"({region.X},{region.Y},{region.Width}×{region.Height}) {region.Detail}"));
        string firstText = first!.Image.HasValue ? $"截图 {Text.Timecode(first.Evidence.FrameTime)}" : $"首次发现 {Text.Timecode(first.Evidence.FrameTime)}（未保存截图）";
        string peakText = best.Image.HasValue ? $"最明显帧 {Text.Timecode(best.Evidence.FrameTime)}" : $"最明显帧 {Text.Timecode(best.Evidence.FrameTime)}（未保存截图）";
        string detail = $"{(best.Evidence.Strong ? "较强视觉证据" : "疑似画面变化，待人工复核")}；局部分数 {best.Evidence.Score:F1}；" + $"{firstText}，{peakText}；同区域确认；{regions}";
        return new(kind, enter, start!.Value, confirmed!.Value, end, detail, reason, first.Image)
        {
            Visual = first.Evidence,
            PeakEvidence = best.Image,
            PeakVisual = best.Evidence
        };
    }

    void Clear()
    {
        tracks.Clear();
        start = null;
        confirmed = null;
        goodSince = null;
        first = null;
        peak = null;
        previousPts = double.NegativeInfinity;
        width = 0;
        height = 0;
    }
}
