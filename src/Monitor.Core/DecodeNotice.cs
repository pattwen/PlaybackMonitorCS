namespace Monitor.Core;

public enum DecodeTimeSource
{
    Unknown, Packet, Frame
}
public sealed record DecodeNotice(string Message, double? MediaTime, int Generation, int StreamIndex, DecodeTimeSource TimeSource, bool IsVideo)
{
    public string Detail => Message + (MediaTime is not { } time || !double.IsFinite(time) || time < 0 ? "；缺少可靠的媒体时间" : TimeSource switch
    {
        DecodeTimeSource.Frame => "(Note:解码帧PTS)",
        DecodeTimeSource.Packet => "(Note:相关媒体包时间)",
        _ => "；缺少可靠的媒体时间"
    });
}


public sealed class DecodeNoticeBuffer(int capacity = 512)
{
    readonly PriorityQueue<DecodeNotice, double> queue = new();
    public int Dropped
    {
        get; private set;
    }
    public void Add(DecodeNotice notice, double fallback)
    {
        if (queue.Count >= capacity)
        {
            Dropped++;
            return;
        }
        double time = notice.MediaTime is { } t && double.IsFinite(t) && t >= 0 ? t : fallback;
        queue.Enqueue(notice, time);
    }
    public IEnumerable<(DecodeNotice Notice, double Time)> Take(double through, int generation)
    {
        while (queue.TryPeek(out var notice, out double time) && (notice.Generation != generation || time <= through + 1e-7))
        {
            queue.Dequeue();
            if (notice.Generation == generation)
            {
                yield return (notice, time);
            }
        }
    }
    public void Clear()
    {
        queue.Clear();
        Dropped = 0;
    }
}
