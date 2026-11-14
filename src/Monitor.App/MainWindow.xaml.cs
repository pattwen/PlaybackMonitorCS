using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Monitor.Core;
using Monitor.Engine;

namespace Monitor.App;
public partial class MainWindow : Window
{
    readonly string root;
    readonly string configPath;
    readonly EventRepository store;
    readonly RecordBrowser records;
    AppSettings config;
    SourceProfile playUrl = new()
    {
        Name = "新直播",
        IsLive = true
    };
    readonly bool audioEnabled;
    readonly SemaphoreSlim operations = new(1, 1);
    PlaybackSession? session;
    readonly DispatcherTimer timer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };
    readonly List<(ProgressBar Bar, TextBlock Value)> meters = [];
    readonly CancellationTokenSource closingToken = new();
    double[] peakHolds = [];
    long[] peakUntil = [];
    bool initialized;
    bool allowClose;
    bool applyingState;
    volatile bool closing;
    string meterSignature = "";
    string tracksSignature = "";
    string lastError = "";
    string recordSession = "";
    bool recordCurrentOnly = true;
    string? selectedRecord;
    readonly AlarmNotifier notifier = new(new AlertSoundPlayer());
    readonly TimelineMarkerLayer timelineMarkers;
    readonly SeekDiagnostics seekDiagnostics;
    readonly SeekInteraction seekUi = new();
    CancellationTokenSource? seekCancellation;
    int stateQueued;
    double mediaDuration;
    PlayerState? uiState;
    string lastSeekAvailability = "";
    public bool IsShuttingDown => closing;

    bool fullscreen;
    WindowState oldState;
    WindowStyle oldStyle;
    public MainWindow(string dataRoot, bool audioOutputEnabled = true)
    {
        root = dataRoot;
        audioEnabled = audioOutputEnabled;
        configPath = Path.Combine(root, "settings.json");
        try
        {
            // 加载配置
            config = Configuration.Load(configPath);
        }
        catch (Exception ex)
        {
            File.Copy(configPath, configPath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            // 初始配置
            config = new();
            MessageBox.Show("配置读取失败，已保留备份并使用默认设置：" + ex.Message);
        }

        store = new(root);
        seekDiagnostics = new(root);
        seekDiagnostics.Write(new("startup", Detail: $"version={typeof(MainWindow).Assembly.GetName().Version} base={AppContext.BaseDirectory}"));
        InitializeComponent();
        timelineMarkers = new(Timeline);
        records = new(store, Dispatcher);
        EventsGrid.ItemsSource = records.Rows;
        records.Applying += () => selectedRecord = (EventsGrid.SelectedItem as AlarmEvent)?.Id;
        records.Changed += RecordsChanged;
        records.Failed += message => ReportAuxiliary(message);
        notifier.Failed += message => PostUi(() => ReportAuxiliary(message));
        Deactivated += (_, _) => Timeline.CancelInteraction();
        BeepCheck.IsChecked = config.Beep;
        DisplayBox.SelectedIndex = (int)config.DisplayMode;
        FieldBox.SelectedIndex = (int)config.Deinterlace;
        BottomText.Text = "记录目录：" + root;
        initialized = true;
        timer.Tick += Tick;
        timer.Start();
        notifier.Configure(config.Beep && audioEnabled, config.AlertSound);
        _ = records.ResetAsync("", "");
        ApplyBusinessState();
    }

    void SaveConfig()
    {
        Configuration.Save(configPath, config);
    }

    public Task OpenAsync(SourceProfile source) => OpenSourceAsync(source, null);
    async Task OpenSourceAsync(SourceProfile source, double? seekTarget, bool pauseAfterSeek = false, int? programId = null, bool switchingProgram = false)
    {
        if (closing)
        {
            return;
        }

        if (!seekTarget.HasValue)
        {
            CancelSeekUi();
        }

        long requestedSeek = seekTarget.HasValue ? seekUi.PendingId : 0;
        try
        {
            await operations.WaitAsync(closingToken.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (closing || (seekTarget.HasValue && requestedSeek != seekUi.PendingId))
            {
                return;
            }

            source.Validate();
            seekDiagnostics.Write(new("source.open", requestedSeek, seekTarget, Detail: Text.SafeSource(source.Location)));
            notifier.SetSource("");
            Timeline.IsEnabled = false;
            var previous = session;
            session = null;
            if (previous != null)
            {
                previous.EndReason = switchingProgram ? "source" : seekTarget.HasValue ? "seek" : "source";
                await previous.DisposeAsync();
            }

            Video.Clear();
            string? reuseSession = !switchingProgram && seekTarget.HasValue && previous?.Source.Location == source.Location && previous.Decoder.SelectedProgram?.Id == programId ? previous.SessionId : null;
            if (closing || (seekTarget.HasValue && requestedSeek != seekUi.PendingId))
            {
                return;
            }

            var opened = new PlaybackSession(Video.VideoHandle, source, config.Detection, store, reuseSession, programId)
            {
                DisplayMode = config.DisplayMode,
                Deinterlace = config.Deinterlace,
                Volume = (float)VolumeSlider.Value,
                Muted = MuteCheck.IsChecked == true,
                AudioOutputEnabled = audioEnabled
            };
            session = opened;
            notifier.SetSource(opened.Identity);
            opened.SeekDiagnostic += seekDiagnostics.Write;
            ErrorDetailsButton.IsEnabled = true;
            opened.AlarmRaised += transition =>
            {
                if (!closing && ReferenceEquals(session, opened))
                {
                    _ = notifier.NotifyAsync(opened.Identity);
                }
            };
            opened.StateChanged += () =>
            {
                if (!closing && ReferenceEquals(session, opened))
                {
                    QueueBusinessRefresh();
                }
            };
            opened.Failed += message => PostUi(() =>
            {
                if (ReferenceEquals(session, opened))
                {
                    ReportFailure(message);
                }
            });
            meterSignature = tracksSignature = "";
            programsSignature = "";
            ProgramBox.ItemsSource = null;
            mediaDuration = 0;
            SourceText.Text = Text.SafeSource(source.Location);
            StateText.Text = "正在打开";
            Meters.Children.Clear();
            meters.Clear();
            AudioTrackBox.ItemsSource = null;
            SoloBox.Items.Clear();
            SoloBox.Items.Add("全部声道下混");
            SoloBox.SelectedIndex = 0;
            if (!seekTarget.HasValue)
            {
                Timeline.Value = 0;
                timelineMarkers.UpdateEntries([], 0, false);
            }

            await opened.StartAsync(seekTarget, requestedSeek, pauseAfterSeek);
            ApplyBusinessState();
            if (seekTarget.HasValue)
            {
                await opened.Ready.WaitAsync(TimeSpan.FromSeconds(source.OpenTimeoutSeconds + 5), closingToken.Token);
                if (closing || !ReferenceEquals(session, opened))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)when (closing)
        {
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                ReportFailure(ex.ToString());
            }
            else
            {
                LogShutdownError(ex);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    void PostUi(Action action)
    {
        if (closing || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!closing)
            {
                action();
            }
        });
    }

    void QueueBusinessRefresh()
    {
        if (closing || Interlocked.Exchange(ref stateQueued, 1) != 0)
        {
            return;
        }

        PostUi(() =>
        {
            Interlocked.Exchange(ref stateQueued, 0);
            ApplyBusinessState();
        });
    }

    void ApplyBusinessState()
    {
        if (closing)
        {
            return;
        }

        var current = session;
        uiState = current?.GetState();
        var state = uiState;
        btn_PlayOrStop.Content = state == null || state.Finished || state.Paused ? "播放" : "暂停";
        btn_PlayOrStop.IsEnabled = state == null || !state.Seeking;
        StateText.Text = state?.Status ?? "就绪";
        string id = state?.SessionId ?? recordSession;
        bool currentOnly = CurrentOnly.IsChecked == true;
        if (recordSession != id || recordCurrentOnly != currentOnly)
        {
            recordSession = id;
            recordCurrentOnly = currentOnly;
            _ = records.ResetAsync(currentOnly ? id : null, id);
        }

        var info = state?.Info;
        mediaDuration = info?.Duration ?? 0;
        Timeline.IsEnabled = current?.Source.IsLive == false && double.IsFinite(mediaDuration) && mediaDuration > 0;
        SeekTimeButton.IsEnabled = Timeline.IsEnabled;
        string signatureSeek = $"source={Text.SafeSource(current?.Source.Location ?? "")} live={current?.Source.IsLive} duration={mediaDuration:F3} enabled={Timeline.IsEnabled} decoderCanSeek={state?.CanSeek}";
        if (signatureSeek != lastSeekAvailability)
        {
            lastSeekAvailability = signatureSeek;
            seekDiagnostics.Write(new("availability", Detail: signatureSeek));
        }

        Timeline.ToolTip = state?.CanSeek == true ? "红线为本次监看告警起点；点击或拖动定位" : current?.Source.IsLive == true ? "直播不提供回看" : "当前来源尚无有效时长或不支持定位";
        timelineMarkers.UpdateEntries(records.Markers, mediaDuration, current?.Source.IsLive ?? false);
        if (state == null)
        {
            UpdatePrograms(null);
            AlarmText.Text = "等待播放";
            RuntimeInfo.Text = "";
            RuntimeStatsText.Text = "";
            TimeText.Text = "00:00:00.000 / —";
            Meters.Children.Clear();
            meters.Clear();
            meterSignature = "";
            AudioTrackBox.ItemsSource = null;
            return;
        }

        UpdatePrograms(state);
        SourceText.Text = Text.SafeSource(current!.Source.Location) + (info == null ? "" : $" | {info.Width}×{info.Height} · {info.Fps:F3}fps · {info.BitDepth}bit") + (info?.Hdr == true ? " | " + state.DisplayStatus : "");
        if (current.Decoder.SelectedProgram is { } selectedProgram)
        {
            SourceText.Text += " | " + selectedProgram.Label;
        }

        AlarmText.Text = state.Active.Length > 0 ? string.Join(" · ", state.Active.Select(Text.Label)) : "未触发告警";
        AlarmText.Foreground = state.Active.Length > 0 ? System.Windows.Media.Brushes.LightCoral : System.Windows.Media.Brushes.MediumAquamarine;
        string signature = string.Join(',', state.ChannelNames);
        if (signature != meterSignature)
        {
            meterSignature = signature;
            BuildMeters(state.ChannelNames.Select(n => new ChannelLevel(n, -120, -120)).ToArray());
        }

        signature = string.Join('|', state.Tracks.Select(t => t.Label));
        applyingState = true;
        try
        {
            if (signature != tracksSignature)
            {
                tracksSignature = signature;
                AudioTrackBox.ItemsSource = state.Tracks;
            }

            AudioTrackBox.SelectedItem = state.Tracks.FirstOrDefault(t => t.Index == state.AudioIndex);
        }
        finally
        {
            applyingState = false;
        }

        FieldBox.IsEnabled = info?.Interlaced != false;
        FieldBox.ToolTip = info?.Interlaced == false ? "逐行视频，无需去隔行" : "按源场序去隔行，或观察原始上下场";
        RuntimeInfo.Text = info == null ? "正在读取媒体信息…" : $"源：{info.Width}×{info.Height} · {info.Fps:F3}fps{(info.Interlaced ? $" / {info.Fps * 2:F2}场 {(info.TopFirst ? "上场优先" : "下场优先")}" : " 逐行")}\n{info.Codec} {info.Profile} · {info.BitDepth}bit\n{info.PixelFormat}\n{info.Primaries}\n{info.Transfer}\n{info.Range}\n{state.DecodeMode}\n{state.Adapter}\n{state.DisplayStatus}\n{state.AudioStatus}";
    }

    void RecordsChanged()
    {
        if (closing)
        {
            return;
        }

        CountText.Text = records.Loading ? "告警记录 · 加载中…" : $"告警记录 · {records.Count}" + (records.Count > 2000 ? "（显示最近2000条）" : "");
        timelineMarkers.UpdateEntries(records.Markers, mediaDuration, session?.Source.IsLive ?? false);
        if (selectedRecord != null)
        {
            EventsGrid.SelectedItem = records.Rows.FirstOrDefault(r => r.Id == selectedRecord);
        }
    }

    void ReportFailure(string detail)
    {
        if (closing)
        {
            return;
        }

        lastError = detail;
        string path = Path.Combine(root, "playback-errors.log");
        _ = Task.Run(() =>
        {
            try
            {
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + Environment.NewLine + detail + Environment.NewLine);
            }
            catch
            {
            }
        });
        ErrorDetailsButton.IsEnabled = true;
        StateText.Text = session?.Status ?? "播放失败";
        BottomText.Text = "播放失败，请点击“错误详情”。日志：" + path;
        BottomText.ToolTip = path;
    }

    void ShowErrorDetails(object sender, RoutedEventArgs e)
    {
        if (closing)
        {
            return;
        }

        var details = new Window
        {
            Owner = this,
            Title = "错误 / 定位诊断",
            Width = 1000,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        details.SetResourceReference(StyleProperty, typeof(Window));
        details.Content = new TextBox
        {
            Text = (string.IsNullOrWhiteSpace(lastError) ? "当前没有未处理的异常记录。" : lastError) + "\n\n定位日志：" + seekDiagnostics.Path + "\n最近定位诊断（UTC）：\n" + seekDiagnostics.Snapshot(),
            Foreground = Brushes.Gainsboro,
            Background = new SolidColorBrush(Color.FromRgb(16, 24, 33)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12)
        };
        details.ShowDialog();
    }

    async void OpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "视频文件|*.mp4;*.mkv;*.ts;*.m2ts;*.mov;*.mxf;*.avi;*.flv;*.webm;*.mpg|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            await OpenAsync(new() { Name = Path.GetFileName(dialog.FileName), Location = dialog.FileName });
        }
    }

    async void OpenStreamUrl(object sender, RoutedEventArgs e)
    {
        var dialog = new StreamUrlWindow(playUrl, this);
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            playUrl = result;
            await OpenAsync(result);
        }
    }

    async void Sources(object sender, RoutedEventArgs e)
    {
        var dialog = new SourceWindow(config.Sources, this);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            config = config with
            {
                Sources = dialog.Profiles,
                LastSourceId = dialog.Selected?.Id ?? config.LastSourceId
            };
            SaveConfig();
            if (dialog.Selected != null)
            {
                await OpenAsync(dialog.Selected);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败");
        }
    }

    void Settings(object sender, RoutedEventArgs e)
    {
        PlaybackSession? current = session;
        bool resume = current != null && !current.Source.IsLive && !current.IsPaused && !current.Finished;
        if (resume)
        {
            current!.TogglePause();
        }

        try
        {
            var dialog = new ObjectEditor<DetectionSettings>("检测设置", config.Detection, settings => settings.Validate(), this);
            if (dialog.ShowDialog() != true || dialog.Result is not { } detection)
            {
                return;
            }

            detection.Validate();
            AppSettings updated = config with
            {
                Detection = detection
            };
            Configuration.Save(configPath, updated);
            config = updated;
            session?.Configure(detection);
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                MessageBox.Show(this, ex.Message, "配置失败");
            }
        }
        finally
        {
            if (resume && ReferenceEquals(current, session) && current!.IsPaused && !current.Finished)
            {
                current.TogglePause();
            }
        }
    }

    void Tick(object? sender, EventArgs e)
    {
        if (closing || session is not { } current)
        {
            return;
        }

        var data = current.ReadContinuous();
        string time = seekUi.Target is { } target ? $"正在跳转到 {Text.Timecode(target)}" : Text.Timecode(data.Position);
        TimeText.Text = time + " / " + (mediaDuration > 0 ? Text.Timecode(mediaDuration) : "实时/未知时长");
        if (data.FrozenPosition is { } frozen)
        {
            TimeText.Text += " · 画面冻结于 " + Text.Timecode(frozen);
        }

        if (seekUi.CanSync && !current.IsSeeking && mediaDuration > 0 && !current.Source.IsLive)
        {
            Timeline.Value = Math.Clamp(data.Position / mediaDuration * 10000, 0, 10000);
        }

        var levels = data.Levels;
        for (int i = 0;
            i < Math.Min(levels.Length, meters.Count);
            i++)
        {
            if (levels[i].PeakDb >= peakHolds[i] || Environment.TickCount64 > peakUntil[i])
            {
                peakHolds[i] = levels[i].PeakDb;
                peakUntil[i] = Environment.TickCount64 + 2000;
            }

            meters[i].Bar.Value = Math.Clamp(levels[i].RmsDb + 90, 0, 90);
            meters[i].Value.Text = $"{levels[i].RmsDb:F0}\nP {peakHolds[i]:F0}";
        }

        RuntimeStatsText.Text = $"分析 {data.AnalysisFps:F1} / 目标 {data.TargetFps:F1}Hz\n处理 {data.Processed} · 分析 {data.Analyzed} · 丢弃过期帧 {data.Dropped}";
        if (data.Video is { } video)
        {
            if (video.Visual is { } picture)
            {
                RuntimeStatsText.Text += "\n" + picture.Summary;
            }

            RuntimeStatsText.Text += $"\n静帧全局差 {video.Difference:F3}";
            if (video.LocalMotion is { } motion)
            {
                RuntimeStatsText.Text += $"\n运动块 {motion.PreviousMovingAreaPercent:F4}% / 参考 {motion.ReferenceMovingAreaPercent:F4}%";
            }
        }
    }

    void BuildMeters(ChannelLevel[] levels)
    {
        Meters.Children.Clear();
        meters.Clear();
        peakHolds = Enumerable.Repeat(-120d, levels.Length).ToArray();
        peakUntil = new long[levels.Length];
        SoloBox.Items.Clear();
        SoloBox.Items.Add("全部声道下混");
        for (int i = 0;
            i < levels.Length;
            i++)
        {
            var panel = new StackPanel
            {
                Width = 43,
                Margin = new Thickness(1)
            };
            panel.Children.Add(new TextBlock { Text = levels[i].Name, HorizontalAlignment = HorizontalAlignment.Center });
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 90,
                Height = 100,
                Width = 19,
                Orientation = Orientation.Vertical,
                Foreground = new SolidColorBrush(Color.FromRgb(59, 196, 155)),
                Background = Brushes.Black
            };
            var value = new TextBlock
            {
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            panel.Children.Add(bar);
            panel.Children.Add(value);
            Meters.Children.Add(panel);
            meters.Add((bar, value));
            SoloBox.Items.Add($"CH{i + 1} · {levels[i].Name}");
        }

        SoloBox.SelectedIndex = 0;
    }

    void Refresh(object sender, RoutedEventArgs e)
    {
        if (closing)
        {
            return;
        }

        recordCurrentOnly = CurrentOnly.IsChecked == true;
        _ = records.ResetAsync(recordCurrentOnly ? session?.SessionId ?? recordSession : null, session?.SessionId ?? recordSession);
    }

    async void Pause(object? sender = null, RoutedEventArgs? e = null)
    {
        if (closing)
        {
            return;
        }

        var current = session;
        if (current == null)
        {
            OpenFile(this, new RoutedEventArgs());
            return;
        }

        if (current.Finished)
        {
            await OpenSourceAsync(current.Source, null, programId: current.Decoder.SelectedProgram?.Id);
        }
        else
        {
            current.TogglePause();
            ApplyBusinessState();
        }
    }

    async void Stop(object? sender = null, RoutedEventArgs? e = null)
    {
        if (closing)
        {
            return;
        }

        CancelSeekUi();
        notifier.SetSource("");
        await operations.WaitAsync();
        try
        {
            var previous = session;
            session = null;
            if (previous != null)
            {
                await previous.DisposeAsync();
            }

            Timeline.Value = 0;
            ApplyBusinessState();
            StateText.Text = "已停止";
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                ReportFailure(ex.ToString());
            }
            else
            {
                LogShutdownError(ex);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    void VolumeChanged(object sender, RoutedEventArgs e)
    {
        if (session != null)
        {
            session.Volume = (float)VolumeSlider.Value;
            session.Muted = MuteCheck.IsChecked == true;
        }
    }

    void SoloChanged(object sender, SelectionChangedEventArgs e)
    {
        if (session != null)
        {
            session.SoloChannel = SoloBox.SelectedIndex - 1;
        }
    }

    void AudioTrackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!applyingState && !closing && session != null && AudioTrackBox.SelectedItem is AudioTrack track)
        {
            session.SelectAudio(track.Index);
        }
    }

    void DisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized)
        {
            return;
        }

        config = config with
        {
            DisplayMode = (DisplayMode)Math.Max(0, DisplayBox.SelectedIndex),
            Deinterlace = (DeinterlaceMode)Math.Max(0, FieldBox.SelectedIndex)
        };
        SaveConfig();
        if (session != null)
        {
            session.DisplayMode = config.DisplayMode;
            session.Deinterlace = config.Deinterlace;
        }
    }

    void SaveOptions(object sender, RoutedEventArgs e)
    {
        if (!initialized || closing)
        {
            return;
        }

        config = config with
        {
            Beep = BeepCheck.IsChecked == true
        };
        SaveConfig();
        notifier.Configure(config.Beep && audioEnabled, config.AlertSound);
    }

    void AnalyzeMedia(object sender, RoutedEventArgs e)
    {
        if (closing)
        {
            return;
        }

        string? location = session?.Source.IsLive == false ? session.Source.Location : null;
        if (location == null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择待分析文件",
                Filter = "媒体文件|*.mp4;*.mkv;*.ts;*.m2ts;*.mov;*.mxf;*.avi;*.flv;*.webm;*.mpg;*.wav;*.mp3;*.m4a|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            location = dialog.FileName;
        }

        new MediaInfoTreeWindow(this, location).Show();
    }

    void SoundSettings(object sender, RoutedEventArgs e)
    {
        if (closing)
        {
            return;
        }

        var dialog = new AlertSoundWindow(this, config.AlertSound, audioEnabled);
        if (dialog.ShowDialog() != true || dialog.Result == null || closing)
        {
            return;
        }

        try
        {
            config = config with
            {
                AlertSound = dialog.Result
            };
            SaveConfig();
            notifier.Configure(config.Beep && audioEnabled, config.AlertSound);
            BottomText.Text = "告警提示音设置已保存。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存提示音设置失败");
        }
    }

    void ReportAuxiliary(string detail)
    {
        if (closing)
        {
            return;
        }

        lastError = detail;
        ErrorDetailsButton.IsEnabled = true;
        BottomText.Text = detail;
        _ = Task.Run(() =>
        {
            try
            {
                File.AppendAllText(Path.Combine(root, "playback-errors.log"), DateTimeOffset.Now.ToString("O") + " " + detail + Environment.NewLine);
            }
            catch
            {
            }
        });
    }

    async void Screenshot(object? sender = null, RoutedEventArgs? e = null)
    {
        if (session == null || closing)
        {
            return;
        }

        try
        {
            string relative = await session.MarkAsync("", false);
            if (relative == "" || closing)
            {
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "PNG与原始帧证据|*.png",
                FileName = Path.GetFileName(relative)
            };
            if (dialog.ShowDialog() == true)
            {
                string from = Path.Combine(root, relative);
                foreach (string ext in new[]
                {
                    ".png",
                    ".raw",
                    ".json"
                }

                )
                {
                    File.Copy(Path.ChangeExtension(from, ext), Path.ChangeExtension(dialog.FileName, ext), true);
                }

                BottomText.Text = "已保存PNG预览、源帧raw和元数据json";
            }
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                MessageBox.Show(this, ex.Message, "截图失败");
            }
            else
            {
                LogShutdownError(ex);
            }
        }
    }

    async void Mark(object? sender = null, RoutedEventArgs? e = null)
    {
        if (session == null || closing)
        {
            return;
        }

        try
        {
            var marked = await session.MarkDetailedAsync("人工标记");
            if (marked.Snapshot == "" || closing)
            {
                return;
            }

            var(ok, text) = Prompt.Get(this, "人工标记备注");
            if (ok && marked.EventId != null)
            {
                await store.ReviewAsync(marked.EventId, "人工确认", text);
            }
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                MessageBox.Show(this, ex.Message, "记录失败");
            }
            else
            {
                LogShutdownError(ex);
            }
        }
    }

    async void Review(object sender, RoutedEventArgs e)
    {
        if (EventsGrid.SelectedItem is not AlarmEvent row)
        {
            return;
        }

        var dialog = new VisualReviewWindow(row, this);
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await store.ReviewAsync(row.Id, dialog.Review, dialog.Note);
            }
            catch (Exception ex)
            {
                if (!closing)
                {
                    ReportAuxiliary("复核保存失败：" + ex.Message);
                }
            }
        }
    }

    async void Export(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = "monitor-events.csv",
            Filter = "CSV|*.csv"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                string? filter = CurrentOnly.IsChecked == true ? session?.SessionId ?? recordSession : null;
                await store.ExportAsync(dialog.FileName, filter);
                if (!closing)
                {
                    BottomText.Text = "导出完成：" + dialog.FileName;
                }
            }
            catch (Exception ex)
            {
                if (!closing)
                {
                    MessageBox.Show(this, ex.Message);
                }
            }
        }
    }

    static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    void Records(object sender, RoutedEventArgs e) => OpenPath(root);
    void ViewEvidence(object sender, RoutedEventArgs e)
    {
        if (EventsGrid.SelectedItem is not AlarmEvent row)
        {
            return;
        }

        if (row.Snapshot == "" && row.PeakSnapshot == "")
        {
            MessageBox.Show(this, "该记录没有视频截图。");
            return;
        }

        try
        {
            string png = Path.Combine(root, row.Snapshot);
            if (row.Visual != null)
            {
                new VisualSnapshotWindow(root, row, this).Show();
                return;
            }

            string raw = Path.ChangeExtension(png, ".raw");
            if (File.Exists(raw) && File.Exists(Path.ChangeExtension(png, ".json")))
            {
                new EvidenceWindow(raw, this).Show();
            }
            else
            {
                OpenPath(png);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法查看证据");
        }
    }

    async void Replay(object sender, MouseButtonEventArgs e)
    {
        if (EventsGrid.SelectedItem is not AlarmEvent row || closing)
        {
            return;
        }

        if (session == null || session.Source.IsLive || row.Source != Text.SafeSource(session.Source.Location))
        {
            MessageBox.Show(this, "直播不提供回看。历史文件请先打开对应来源，再定位到记录时间。");
            return;
        }

        if (ProgramSelection.TryReadProgramId(row.Detail, out int recordedProgram))
        {
            if (!session.Decoder.Programs.Any(program => program.Id == recordedProgram && program.HasVideo))
            {
                MessageBox.Show(this, "当前来源已找不到该记录对应的节目，请核对文件或节目表。");
                return;
            }

            await SubmitSeekAsync(Math.Max(0, row.Start - 2), recordedProgram, recordedProgram != session.Decoder.SelectedProgram?.Id);
        }
        else
        {
            if (session.Decoder.Programs.Length > 1 && row.SessionId != session.SessionId)
            {
                MessageBox.Show(this, "这条旧记录没有节目身份，无法自动确认对应节目。请先手动选择节目，再使用定位按钮输入记录时间。");
            }
            else
            {
                await SubmitSeekAsync(Math.Max(0, row.Start - 2));
            }
        }
    }

    void FullScreen(object? sender = null, RoutedEventArgs? e = null)
    {
        if (!fullscreen)
        {
            oldState = WindowState;
            oldStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = oldStyle;
            WindowState = oldState;
        }

        fullscreen = !fullscreen;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.G)
        {
            GoToTime();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (e.Key == Key.F11)
        {
            FullScreen();
            e.Handled = true;
        }
        else
        {
            if (e.Key == Key.Escape && fullscreen)
            {
                FullScreen();
                e.Handled = true;
            }
            else
            {
                if (e.Key == Key.Space)
                {
                    Pause();
                    e.Handled = true;
                }
                else
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        if (e.Key == Key.O)
                        {
                            OpenFile(this, e);
                        }

                        if (e.Key == Key.S)
                        {
                            Screenshot();
                        }

                        if (e.Key == Key.M)
                        {
                            Mark();
                        }
                    }
                }
            }
        }
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (closing)
        {
            return;
        }

        closing = true;
        IsEnabled = false;
        timer.Stop();
        closingToken.Cancel();
        CancelSeekUi();
        notifier.SetSource("");
        StateText.Text = "正在退出并保存记录…";
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = FinishCloseAsync()));
    }

    async Task FinishCloseAsync()
    {
        await operations.WaitAsync();
        try
        {
            foreach (Window owned in OwnedWindows.Cast<Window>().ToArray())
            {
                try
                {
                    if (owned is EvidenceWindow evidenceWindow)
                    {
                        await evidenceWindow.RequestCloseAsync();
                    }
                    else
                    {
                        if (owned is AlertSoundWindow soundWindow)
                        {
                            await soundWindow.RequestCloseAsync();
                        }
                        else
                        {
                            owned.Close();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogShutdownError(ex);
                }
            }

            var previous = session;
            session = null;
            if (previous != null)
            {
                previous.EndReason = "closed";
                try
                {
                    await previous.DisposeAsync();
                }
                catch (Exception ex)
                {
                    LogShutdownError(ex);
                }
            }

            try
            {
                await notifier.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogShutdownError(ex);
            }

            try
            {
                timelineMarkers.Dispose();
            }
            catch (Exception ex)
            {
                LogShutdownError(ex);
            }

            try
            {
                await records.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogShutdownError(ex);
            }

            try
            {
                await store.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogShutdownError(ex);
            }

            try
            {
                await seekDiagnostics.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogShutdownError(ex);
            }

            closingToken.Dispose();
        }
        catch (Exception ex)
        {
            LogShutdownError(ex);
        }
        finally
        {
            operations.Release();
            allowClose = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
        }
    }

    void LogShutdownError(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(root, "application-errors.log"), DateTimeOffset.Now.ToString("O") + " 退出清理：" + ex + Environment.NewLine);
        }
        catch
        {
        }
    }
}
