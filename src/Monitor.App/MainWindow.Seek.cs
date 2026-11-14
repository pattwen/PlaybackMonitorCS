using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Monitor.Core;

namespace Monitor.App;
public partial class MainWindow
{
    bool seekPaused;
    void TimelineInteractionStarted(object? sender, EventArgs e)
    {
        seekUi.BeginInteraction();
        seekDiagnostics.Write(new("input.begin", Detail: $"value={Timeline.Value:F1} duration={mediaDuration:F3}"));
    }

    void TimelineInteractionCancelled(object? sender, EventArgs e)
    {
        seekUi.CancelInteraction();
        seekDiagnostics.Write(new("input.cancel"));
    }

    async void TimelineSeekRequested(object? sender, SeekSliderEventArgs e)
    {
        seekUi.CancelInteraction();
        seekDiagnostics.Write(new("input.commit", Target: e.Value / 10000 * mediaDuration, Detail: e.Kind.ToString()));
        await SubmitSeekAsync(e.Value / 10000 * mediaDuration);
    }

    void CancelSeekUi()
    {
        seekCancellation?.Cancel();
        seekCancellation = null;
        Timeline.CancelInteraction();
        seekUi.Reset();
    }

    async void GoToTime(object? sender = null, RoutedEventArgs? e = null)
    {
        if (closing)
        {
            return;
        }

        var(ok, text) = Prompt.Get(this, "跳转至时间-输入秒数或 时:分:秒.毫秒，例如 120 或 00:02:00.000", "00:00:00.000");
        if (!ok)
        {
            return;
        }

        if (SeekTime.TryParse(text, out double target))
        {
            await SubmitSeekAsync(target);
        }
        else
        {
            ReportAuxiliary("时间格式无效，请输入秒数或 时:分:秒.毫秒。");
        }
    }

    async Task SubmitSeekAsync(double target, int? programId = null, bool switchProgram = false)
    {
        var current = session;
        if (closing)
        {
            return;
        }

        if (current == null || current.Source.IsLive || !double.IsFinite(mediaDuration) || mediaDuration <= 0)
        {
            string reason = current == null ? "尚未打开视频。" : current.Source.IsLive ? "当前来源按直播处理，不提供回看；收录文件应使用文件模式。" : "尚未读取到有效时长，暂时无法按时间定位。";
            seekDiagnostics.Write(new("rejected", Target: target, Detail: reason));
            ReportAuxiliary(reason);
            return;
        }

        if (seekUi.PendingId == 0)
        {
            seekPaused = current.IsPaused && !current.Finished;
        }

        long id = seekUi.Request(Math.Clamp(target, 0, Math.Max(0, mediaDuration - .1)));
        target = seekUi.Target!.Value;
        seekDiagnostics.Write(new("requested", id, target, current.Clock.Position));
        ErrorDetailsButton.IsEnabled = true;
        var previousCancel = seekCancellation;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(closingToken.Token);
        seekCancellation = cancel;
        cancel.CancelAfter(TimeSpan.FromSeconds(current.Source.OpenTimeoutSeconds + 10));
        var source = current.Source;
        int? requestedProgram = switchProgram ? programId : current.Decoder.SelectedProgram?.Id;
        Timeline.Value = target / mediaDuration * 10000;
        TimeText.Text = "正在跳转到 " + Text.Timecode(target);
        try
        {
            SeekResult result;
            current.SetPauseForNextSeek(seekPaused);
            if (switchProgram || current.RequiresReopenForSeek)
            {
                previousCancel?.Cancel();
                result = new(id, SeekOutcome.ReopenRequired, target);
            }
            else
            {
                var seeking = current.SeekAsync(target, id, cancel.Token);
                previousCancel?.Cancel();
                result = await seeking;
            }

            if (closing || seekUi.PendingId != id)
            {
                return;
            }

            if (result.Outcome == SeekOutcome.ReopenRequired)
            {
                await OpenSourceAsync(source, target, seekPaused, requestedProgram, switchProgram);
                if (closing || seekUi.PendingId != id)
                {
                    return;
                }

                var reopened = session;
                if (reopened == null || reopened.Source != source)
                {
                    result = new(id, SeekOutcome.Failed, 0, "文件未能重新打开，请查看错误详情。");
                }
                else
                {
                    result = await reopened.AwaitInitialSeekAsync(cancel.Token);
                }
            }

            seekDiagnostics.Write(new("result", result.RequestId, target, result.Position, Detail: $"{result.Outcome}: {result.Detail}"));
            if (closing || !seekUi.Complete(result))
            {
                return;
            }

            if (result.Outcome == SeekOutcome.Success)
            {
                Timeline.Value = Math.Clamp(result.Position / mediaDuration * 10000, 0, 10000);
                BottomText.Text = "已定位到 " + Text.Timecode(result.Position) + (seekPaused ? "（保持暂停）" : "");
            }
            else
            {
                if (result.Outcome != SeekOutcome.Superseded)
                {
                    ReportAuxiliary(result.Outcome == SeekOutcome.Cancelled ? "跳转超时或已取消，请重新定位或播放。" : "跳转失败：" + result.Detail);
                }
            }

            ApplyBusinessState();
        }
        catch (Exception ex)
        {
            seekDiagnostics.Write(new("exception", id, target, Detail: ex.ToString()));
            if (!closing && seekUi.Complete(new(id, SeekOutcome.Failed, 0)))
            {
                ReportAuxiliary("跳转失败：" + ex.Message);
                ApplyBusinessState();
            }
        }
        finally
        {
            if (ReferenceEquals(seekCancellation, cancel))
            {
                seekCancellation = null;
            }
        }
    }
}
