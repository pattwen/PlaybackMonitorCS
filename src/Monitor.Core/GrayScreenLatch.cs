namespace Monitor.Core;

public sealed class GrayScreenLatch(DetectionSettings settings)
{
    sealed record Sample(VisualEvidence Evidence, Guid? Image);
    Sample? first;
    Sample? peak;
    VisualRegion? lastRegion;
    double? start;
    double? confirmed;
    double? goodSince;
    double previousPts = double.NegativeInfinity;
    public bool Active => confirmed.HasValue;
    public IEnumerable<Guid> EvidenceIds => new[]
    {
        first?.Image,
        peak?.Image
    }.OfType<Guid>().Distinct();

    public AlarmTransition? Update(VisualEvidence? observation, double pts, Guid? image)
    {
        if (!double.IsFinite(pts) || pts <= previousPts)
        {
            return null;
        }

        previousPts = pts;
        var region = observation?.Regions.Where(candidate => candidate.Kind == AlarmKind.Gray).OrderByDescending(candidate => candidate.AreaPercent).FirstOrDefault();
        if (region != null && first != null && (observation!.Width != first.Evidence.Width || observation.Height != first.Evidence.Height))
        {
            if (Active)
            {
                return Close(pts, "format");
            }

            Clear();
            previousPts = pts;
        }

        if (region == null)
        {
            if (!Active)
            {
                ClearPending();
            }
            else
            {
                goodSince ??= pts;
                if (pts - goodSince.Value + 1e-7 >= settings.RecoverySeconds)
                {
                    return Close(goodSince.Value, "recovered");
                }
            }

            return null;
        }

        if (!Active && lastRegion != null && !SameRegion(lastRegion, region))
        {
            ClearPending();
        }

        lastRegion = region;
        goodSince = null;
        start ??= pts;
        var sample = new Sample(new(observation!.Width, observation.Height, pts, [region]), image);
        if (first == null || !first.Image.HasValue && image.HasValue)
        {
            first = sample;
        }

        if (peak == null || image.HasValue && (!peak.Image.HasValue || sample.Evidence.Score > peak.Evidence.Score + .5 && (pts - peak.Evidence.FrameTime >= .2 || sample.Evidence.Score > peak.Evidence.Score + 20)))
        {
            peak = sample;
        }

        if (!Active && (pts - start.Value + 1e-7 >= settings.GraySeconds || settings.GrayRecordBrief && sample.Evidence.Strong))
        {
            confirmed = pts;
            return Transition(true, null, "active");
        }

        return null;
    }

    static bool SameRegion(VisualRegion first, VisualRegion second)
    {
        double intersection = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X)) * Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        double smaller = Math.Min((double)first.Width * first.Height, (double)second.Width * second.Height);
        return smaller > 0 && intersection / smaller >= .4;
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
        string detail = $"{(best.Evidence.Strong ? "突发灰色填充，待人工复核" : "持续低色度均匀画面，待人工复核")}；" + $"首次候选 {Text.Timecode(start!.Value)}，首次可保留帧 {Text.Timecode(first!.Evidence.FrameTime)}，" + $"最明显帧 {Text.Timecode(best.Evidence.FrameTime)}；" + string.Join("；", best.Evidence.Regions.Select(region => region.Detail));
        return new(AlarmKind.Gray, enter, start!.Value, confirmed!.Value, end, detail, reason, first.Image)
        {
            Visual = first.Evidence,
            PeakEvidence = best.Image,
            PeakVisual = best.Evidence
        };
    }

    void ClearPending()
    {
        first = null;
        peak = null;
        lastRegion = null;
        start = null;
        goodSince = null;
    }

    void Clear()
    {
        ClearPending();
        confirmed = null;
        previousPts = double.NegativeInfinity;
    }
}
