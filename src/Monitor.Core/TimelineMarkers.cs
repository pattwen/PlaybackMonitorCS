using System;
using System.IO;
namespace Monitor.Core;

public sealed record TimelineMarker(string EventId, AlarmKind Kind, double Seconds, double Fraction, string Description);

public static class TimelineMarkers
{
    public static TimelineMarker[] Create(IEnumerable<AlarmEvent> events, string sessionId, double duration, bool isLive = false)
    {
        if (isLive || string.IsNullOrEmpty(sessionId) || !double.IsFinite(duration) || duration <= 0)
        {
            return [];
        }
        return events.Where(e => e.SessionId == sessionId && double.IsFinite(e.Start) && e.Start >= 0 && e.Start <= duration)
            .OrderBy(e => e.Start)
            .Select(e => new TimelineMarker(e.Id, e.Kind, e.Start, e.Start / duration, $"{e.Label} · {Text.Timecode(e.Start)} · {e.Status}"))
            .ToArray();
    }
}
