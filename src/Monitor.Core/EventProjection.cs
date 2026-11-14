namespace Monitor.Core;

public sealed record EventChange(long Sequence, AlarmEvent Row, bool Added);
public sealed record TimelineEntry(string Id, AlarmKind Kind, double Start);
public sealed record EventPage(long Watermark, long Count, AlarmEvent[] Rows, TimelineEntry[] Markers);

public sealed class EventProjection(int limit = 2000)
{
    readonly Dictionary<string, AlarmEvent> visible = [];
    readonly Dictionary<string, TimelineEntry> markers = [];
    string? filter; string markerSession = "";
    public long Watermark
    {
        get; private set;
    }
    public long Count
    {
        get; private set;
    }
    public AlarmEvent[] Rows => visible.Values.OrderByDescending(e => e.DetectedAt).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToArray();
    public TimelineEntry[] Markers => markers.Values.ToArray();
    public void Reset(EventPage page, string? sessionFilter, string currentSession)
    {
        filter = sessionFilter;
        markerSession = currentSession;
        Watermark = page.Watermark;
        Count = page.Count;
        visible.Clear();
        foreach (var row in page.Rows.Take(limit))
        {
            visible[row.Id] = row;
        }
        markers.Clear();
        foreach (var marker in page.Markers)
        {
            markers[marker.Id] = marker;
        }
    }
    public bool Apply(IEnumerable<EventChange> changes)
    {
        bool changed = false;
        foreach (var change in changes.OrderBy(c => c.Sequence))
        {
            if (change.Sequence <= Watermark)
            {
                continue;
            }
            Watermark = change.Sequence;
            var row = change.Row;
            if (row.SessionId == markerSession && markerSession.Length > 0)
            {
                markers[row.Id] = new(row.Id, row.Kind, row.Start);
                changed = true;
            }
            if (filter != null && row.SessionId != filter)
            {
                continue;
            }
            if (change.Added)
            {
                Count++;
            }
            visible[row.Id] = row;
            changed = true;
        }
        if (visible.Count > limit)
            foreach (var row in Rows.Skip(limit))
            {
                visible.Remove(row.Id);
            }
        return changed;
    }
}
