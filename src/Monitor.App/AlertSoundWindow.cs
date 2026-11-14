using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Monitor.Core;
using Monitor.Engine;

namespace Monitor.App;

public sealed class AlertSoundWindow : Window
{
    readonly TextBox path = new() { IsReadOnly = true };
    readonly Slider volume = new() { Minimum = 0, Maximum = 1, TickFrequency = .1 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly AlertSoundPlayer preview = new();
    readonly AlertSoundSettings original;
    readonly Button play = new() { Content = "试听" };
    readonly Button save = new() { Content = "保存" };
    bool closed;
    Task cleanup = Task.CompletedTask;
    public AlertSoundSettings? Result
    {
        get; private set;
    }
    public AlertSoundWindow(Window owner, AlertSoundSettings settings, bool allowPreview = true)
    {
        original = settings;
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "告警提示音";
        Width = 650;
        Height = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;
        root.Children.Add(new TextBlock { Text = "选择 WAV / MP3，时长不超过10秒。留空使用内置短双音。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(path);
        path.Text = settings.FilePath;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
        root.Children.Add(tools);
        var browse = new Button { Content = "选择音频文件" };
        var reset = new Button { Content = "使用内置声音" };
        tools.Children.Add(browse);
        tools.Children.Add(reset);
        tools.Children.Add(play);
        play.IsEnabled = allowPreview;
        if (!allowPreview)
        {
            play.ToolTip = "本次启动已禁用本机声音输出。";
        }
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "告警音频|*.wav;*.mp3|WAV|*.wav|MP3|*.mp3", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true)
            {
                path.Text = dialog.FileName;
                status.Text = "请保留该原始文件；移动或删除后需重新选择。";
            }
        };
        reset.Click += (_, _) => { path.Text = ""; status.Text = "使用内置短双音。"; };
        root.Children.Add(new TextBlock { Text = "提示音音量（独立于节目监听音量）" });
        volume.Value = settings.Volume;
        root.Children.Add(volume);
        status.Margin = new Thickness(0, 10, 0, 10);
        root.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消" };
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        root.Children.Add(actions);
        cancel.Click += (_, _) => DialogResult = false;
        play.Click += async (_, _) =>
        {
            play.IsEnabled = false;

            try
            {
                var result = await preview.PlayAsync(Selected(), true);
                if (!closed)
                {
                    status.Text = result.State == AlertSoundState.Played ? "试听完成。" : result.Detail.Length > 0 ? result.Detail : "试听已停止。";
                }
            }
            catch (Exception ex)
            {
                if (!closed)
                {
                    status.Text = ex.Message;
                }
            }
            finally
            {
                if (!closed)
                {
                    play.IsEnabled = true;
                }
            }
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var selected = Selected();
                await AlertSoundPlayer.ValidateFileAsync(selected);
                if (closed)
                {
                    return;
                }
                Result = selected;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                if (!closed)
                {
                    status.Text = "无法保存：" + ex.Message;
                }
            }
            finally
            {
                if (!closed)
                {
                    save.IsEnabled = true;
                }
            }
        };
        Closed += (_, _) => { closed = true; cleanup = preview.DisposeAsync().AsTask(); };
    }
    internal Task RequestCloseAsync()
    {
        Dispatcher.VerifyAccess();
        if (!closed)
        {
            Close();
        }
        return cleanup;
    }
    AlertSoundSettings Selected() => original with { FilePath = path.Text, Volume = volume.Value };
}
