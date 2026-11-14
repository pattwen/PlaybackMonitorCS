using System.Windows.Controls;
using Monitor.Core;
using Monitor.Engine;

namespace Monitor.App;
public partial class MainWindow
{
    private bool applyingPrograms;
    private bool programSwitching;
    private string programsSignature = "";
    private void UpdatePrograms(PlayerState? state)
    {
        applyingPrograms = true;
        try
        {
            string signature = state == null ? "" : string.Join('|', state.Programs.Select(item => $"{item.Id}:{item.Number}:{item.Label}:{string.Join(',', item.StreamIndexes)}"));
            if (signature != programsSignature || state == null)
            {
                programsSignature = signature;
                ProgramBox.ItemsSource = state?.Programs;
            }

            ProgramBox.SelectedItem = state?.Programs.FirstOrDefault(item => item.Id == state.ProgramId);
            ProgramBox.IsEnabled = !programSwitching && state != null && state.Programs.Count(item => item.HasVideo) > 1 && !state.Seeking;
            AudioTrackBox.IsEnabled = !programSwitching && state != null && state.Tracks.Length > 0 && !state.Seeking;
            ProgramBox.ToolTip = state == null ? "打开媒体后显示节目列表。" : state.Programs.Length == 0 ? "该来源没有Program节目表，按普通单节目媒体播放。" : "文件切换节目从头播放；直播切换节目重新连接，自动匹配对应视频与音轨。无视频节目不可选。";
        }
        finally
        {
            applyingPrograms = false;
        }
    }

    private async void ProgramChanged(object sender, SelectionChangedEventArgs e)
    {
        if (closing || applyingPrograms || programSwitching || session is not { } current || ProgramBox.SelectedItem is not PlaybackProgram chosen || chosen.Id == current.Decoder.SelectedProgram?.Id)
        {
            return;
        }

        programSwitching = true;
        ProgramBox.IsEnabled = false;
        AudioTrackBox.IsEnabled = false;
        try
        {
            if (current.Source.IsLive || !current.Decoder.CanSeek)
            {
                await OpenSourceAsync(current.Source, null, programId: chosen.Id, switchingProgram: true);
            }
            else
            {
                await SubmitSeekAsync(0, chosen.Id, true);
            }
        }
        catch (Exception error)
        {
            if (!closing)
            {
                ReportFailure(error.ToString());
            }
        }
        finally
        {
            programSwitching = false;
            if (!closing)
            {
                ApplyBusinessState();
            }
        }
    }
}
