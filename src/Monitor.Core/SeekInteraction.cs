using System;
using System.IO;
namespace Monitor.Core;

public enum SeekOutcome
{
    Success, Failed, Superseded, ReopenRequired, Cancelled
}
public sealed record SeekResult(long RequestId, SeekOutcome Outcome, double Position, string Detail = "");

public sealed class SeekInteraction
{
    long sequence;
    public bool Interacting
    {
        get; private set;
    }
    public long PendingId
    {
        get; private set;
    }
    public double? Target
    {
        get; private set;
    }
    public bool CanSync => !Interacting && PendingId == 0;
    public void BeginInteraction() => Interacting = true;
    public void CancelInteraction() => Interacting = false;
    public long Request(double target)
    {
        if (!double.IsFinite(target) || target < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        Interacting = false;
        Target = target;
        return PendingId = ++sequence;
    }
    public bool Complete(SeekResult result)
    {
        if (PendingId == 0 || PendingId != result.RequestId)
        {
            return false;
        }
        PendingId = 0;
        Target = null;
        return true;
    }
    public void Reset()
    {
        sequence++;
        PendingId = 0;
        Target = null;
        Interacting = false;
    }
}
