using Monitor.Core;
using System.Threading.Channels;

namespace Monitor.Engine;
public sealed record PlayerState(string SessionId, VideoInfo? Info, AudioTrack[] Tracks, int AudioIndex, string[] ChannelNames, AlarmKind[] Active, string Status, string DecodeMode, string Adapter, string DisplayStatus, string AudioStatus, bool Paused, bool Finished, bool Seeking, bool CanSeek)
{
    public PlaybackProgram[] Programs
    {
        get;
        init;
    } = [];
    public int? ProgramId
    {
        get;
        init;
    }
}

public sealed record PlayerContinuous(double Position, double? FrozenPosition, ChannelLevel[] Levels, int Processed, int Analyzed, int Dropped, double AnalysisFps, double TargetFps)
{
    public VideoMetrics? Video
    {
        get;
        init;
    }
}

public sealed class PlaybackSession : IAsyncDisposable
{
    readonly object gate = new();
    readonly nint hwnd;
    readonly EventRepository store;
    readonly CancellationTokenSource stop = new();
    readonly Evidence evidence = new();
    readonly AudioOutput output = new();
    readonly LevelWindow meter = new();
    readonly Channel<Action> writes = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    readonly Task writer;
    readonly HashSet<Guid> requested = [];
    int captures;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Detector detector;
    GpuRenderer? renderer;
    FrameLease? lastFrame;
    FrameLease? displayedFrame;
    Task running = Task.CompletedTask;
    volatile bool displayPaused;
    volatile bool seeking;
    volatile bool switchingAudio;
    int disposed;
    double lastVideo;
    double lastAudio;
    double previousAnalysis = -1;
    AudioTrack[] tracks = [];
    long videoArrival = System.Diagnostics.Stopwatch.GetTimestamp();
    long audioArrival = System.Diagnostics.Stopwatch.GetTimestamp();
    string? publishedKey;
    VideoInfo? publishedInfo;
    sealed class SeekTicket(long id, double target, bool paused)
    {
        public long Id = id;
        public double Target = target;
        public bool Paused = paused;
        public volatile int Generation = -1;
        public TaskCompletionSource<SeekResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    volatile SeekTicket? pending;
    Task<SeekResult>? lastSeekTask;
    bool? nextSeekPause;
    readonly TaskCompletionSource firstVideoPresented = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool videoStarted;
    volatile bool videoCompleted;
    double audioResumePosition;
    long metadataArrival;
    public string Identity
    {
        get;
    } = Guid.NewGuid().ToString("N");

    readonly DecodeNoticeBuffer decodeNotices = new();
    public string SessionId
    {
        get;
        private set;
    } = "";
    public FfmpegDecoder Decoder
    {
        get;
    }
    public SourceProfile Source
    {
        get;
    }
    public PlaybackClock Clock
    {
        get;
    } = new();
    public VideoInfo? Info => Decoder.Info;
    public Task Ready => ready.Task;
    public DetectionSettings Settings
    {
        get;
        private set;
    }
    public DisplayMode DisplayMode
    {
        get;
        set;
    }
    public DeinterlaceMode Deinterlace
    {
        get;
        set;
    }
    public bool Finished
    {
        get;
        private set;
    }
    public bool IsPaused => Source.IsLive ? displayPaused : Clock.Paused;

    public bool IsSeeking
    {
        get
        {
            lock (gate)
            {
                return pending != null || seeking;
            }
        }
    }

    public bool RequiresReopenForSeek => Finished || Decoder.Ended || Decoder.Completion.IsCompleted;
    public string EndReason
    {
        get;
        set;
    } = "stopped";
    public int DroppedFrames
    {
        get;
        private set;
    }
    public int ProcessedFrames
    {
        get;
        private set;
    }
    public int AnalyzedFrames
    {
        get;
        private set;
    }
    public double ActualAnalysisFps
    {
        get;
        private set;
    }
    public bool AudioOutputEnabled
    {
        get;
        set;
    } = true;
    public bool NativeHdrOutput => renderer?.HdrOutput ?? false;
    public string DisplayStatus => renderer?.Status ?? "等待视频";
    public double? FrozenDisplayPosition => displayPaused ? displayedFrame?.Pts : null;
    public string Status
    {
        get;
        private set;
    } = "正在打开";

    public event Action? Updated;
    public event Action? StateChanged;
    public event Action<SeekDiagnostic>? SeekDiagnostic;
    public event Action<AlarmTransition>? AlarmRaised;
    public event Action<AlarmEvent>? Recorded;
    public event Action<string>? Failed;
    public ChannelLevel[] Levels
    {
        get
        {
            lock (gate)
            {
                return detector.Levels;
            }
        }
    }

    public AlarmKind[] Active
    {
        get
        {
            lock (gate)
            {
                return detector.Active;
            }
        }
    }

    public VideoMetrics? Metrics
    {
        get
        {
            lock (gate)
            {
                return detector.Metrics;
            }
        }
    }

    public string TechnicalStatus => $"{Decoder.DecodeMode}\n{renderer?.Adapter}\n{renderer?.Status}\n{output.Status}\n分析 {ActualAnalysisFps:F1} / 目标 {Settings.AnalysisFps:F1}Hz\n本机丢弃过期帧：{DroppedFrames}";
    public float Volume { get => output.Volume;
        set => output.Volume = value;
    }
    public bool Muted { get => output.Muted;
        set => output.Muted = value;
    }
    public int SoloChannel { get => output.SoloChannel;
        set => output.SoloChannel = value;
    }

    public PlayerState GetState()
    {
        lock (gate)
        {
            return new(SessionId, ready.Task.IsCompletedSuccessfully ? Info : null, tracks, Decoder.CurrentAudioIndex, detector.Levels.Select(l => l.Name).ToArray(), detector.Active, Status, Decoder.DecodeMode, renderer?.Adapter ?? "", DisplayStatus, AudioOutputEnabled ? output.Status : "声音输出禁用，源音频仍检测", IsPaused, Finished, pending != null || seeking, Decoder.CanSeek)
            {
                Programs = Decoder.Programs,
                ProgramId = Decoder.SelectedProgram?.Id
            };
        }
    }

    public PlayerContinuous ReadContinuous() => new(Clock.Position, FrozenDisplayPosition, meter.ReadAndReset(), ProcessedFrames, AnalyzedFrames, DroppedFrames, ActualAnalysisFps, Settings.AnalysisFps)
    {
        Video = Metrics
    };
    void PublishState()
    {
        bool changed;
        lock (gate)
        {
            string key = $"{SessionId}|{Status}|{IsPaused}|{Finished}|{seeking}|{pending?.Id}|{Decoder.DecodeMode}|{renderer?.Status}|{output.Status}|{switchingAudio}|{Decoder.CurrentAudioIndex}|{string.Join(',', detector.Active)}|{string.Join(',', detector.Levels.Select(l => l.Name))}";
            changed = key != publishedKey || !Equals(publishedInfo, Info);
            if (changed)
            {
                publishedKey = key;
                publishedInfo = Info;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    public PlaybackSession(nint hwnd, SourceProfile source, DetectionSettings settings, EventRepository store, string? reuseSession = null, int? programId = null)
    {
        SessionId = reuseSession ?? "";
        this.hwnd = hwnd;
        Source = source;
        Settings = settings;
        this.store = store;
        detector = new(settings);
        Decoder = new(source, programId);
        writer = Task.Run(WriteLoop);
        Decoder.SeekDiagnostic += entry => SeekDiagnostic?.Invoke(entry);
        Decoder.Metadata += _ =>
        {
            lock (gate)
            {
                tracks = Decoder.Tracks.ToArray();
                metadataArrival = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            ready.TrySetResult();
            PublishState();
        };
        Decoder.DetailedNotice += notice =>
        {
            lock (gate)
            {
                if (stop.IsCancellationRequested || Finished || pending != null || seeking)
                {
                    SeekDiagnostic?.Invoke(new("decode.notice-during-seek", pending?.Id ?? 0, pending?.Target, Detail: notice.Detail));
                    return;
                }

                if (notice.Generation == Decoder.Generation)
                {
                    decodeNotices.Add(notice, Clock.Position);
                    if (!Clock.Paused)
                    {
                        DrainDecodeNotices(Clock.Position);
                    }
                }
            }
        };
        Decoder.Fatal += text =>
        {
            lock (gate)
            {
                Record([new(AlarmKind.InputError, true, Clock.Position, Clock.Position, Detail: text), new(AlarmKind.InputError, false, Clock.Position, Clock.Position, Clock.Position, Reason: "input_error")]);
                Record(detector.Reset(Clock.Position, "input_error"));
                Status = text;
                Finished = true;
                Clock.Pause();
                stop.Cancel();
                ready.TrySetException(new IOException(text));
                FailPending(text);
            }

            Failed?.Invoke(text);
            PublishState();
        };
        Decoder.Seeked += (pts, generation, id) =>
        {
            lock (gate)
            {
                if (pending == null || pending.Id != id)
                {
                    return;
                }

                Record(detector.Reset(Clock.Position, "seek"));
                decodeNotices.Clear();
                pending.Generation = generation;
                SeekDiagnostic?.Invoke(new("session.ack", id, pts, Generation: generation));
                Clock.Seek(pts);
                Clock.Pause();
                audioResumePosition = pts;
                lastVideo = lastAudio = pts;
                output.Flush();
                meter.Clear();
                seeking = false;
            }

            PublishState();
        };
        Decoder.AudioChanged += _ =>
        {
            lock (gate)
            {
                Record(detector.ResetAudio(Clock.Position));
                output.Flush();
                meter.Clear();
                lastAudio = Clock.Position;
                switchingAudio = false;
            }

            PublishState();
        };
    }

    public async Task StartAsync(double? initialTarget = null, long requestId = 0, bool pauseAfterSeek = false)
    {
        SessionId = SessionId.Length == 0 ? await store.BeginAsync(Source.Location, Settings).ConfigureAwait(false) : await store.ResumeAsync(SessionId).ConfigureAwait(false);
        if (initialTarget.HasValue)
        {
            lock (gate)
            {
                pending = new(requestId, initialTarget.Value, pauseAfterSeek);
                lastSeekTask = pending.Completion.Task;
                seeking = true;
                Status = "正在跳转…";
                Decoder.PrepareInitialSeek(initialTarget.Value, requestId);
            }
        }

        Decoder.Start();
        running = RunAsync();
        PublishState();
    }

    public void SetPauseForNextSeek(bool paused)
    {
        lock (gate)
        {
            nextSeekPause = paused;
        }
    }

    public async Task<SeekResult> AwaitInitialSeekAsync(CancellationToken token)
    {
        long id;
        Task<SeekResult> task;
        lock (gate)
        {
            id = pending?.Id ?? 0;
            task = lastSeekTask ?? throw new InvalidOperationException("没有初始跳转请求。");
        }

        try
        {
            return await task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                if (pending?.Id == id)
                {
                    CancelSeek();
                }
            }

            return new(id, SeekOutcome.Cancelled, Clock.Position);
        }
    }

    void CancelSeek()
    {
        lock (gate)
        {
            if (pending == null)
            {
                return;
            }

            FailPending("跳转已取消或超时。", SeekOutcome.Cancelled);
            if (!stop.IsCancellationRequested)
            {
                Finished = true;
                Status = "跳转已取消或超时，请重新定位或播放。";
                Clock.Pause();
                Decoder.RequestStop();
                stop.Cancel();
            }
        }

        PublishState();
    }

    // Console diagnostics only; WPF awaits StartAsync.
    public void Start() => StartAsync().GetAwaiter().GetResult();
    async Task RunAsync()
    {
        async Task Guard(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch
            {
                lock (gate)
                {
                    stop.Cancel();
                    Clock.Pause();
                }
                Decoder.RequestStop();
                throw;
            }
        }

        var video = Task.Run(() => Guard(VideoLoop));
        var audio = Task.Run(() => Guard(AudioLoop));
        var watchdog = Task.Run(Watchdog);
        try
        {
            await Task.WhenAll(video, audio);
            if (!stop.IsCancellationRequested)
            {
                lock (gate)
                {
                    Clock.Pause();
                    Record(detector.Reset(Clock.Position, Decoder.Ended ? "eof" : "input_error"));
                    Finished = true;
                    Status = !videoStarted ? "所选节目没有输出可显示的视频帧，请查看错误详情。" : Decoder.Ended ? "播放结束" : "输入结束";
                    FailPending("目标位置没有可显示的视频帧。");
                }

                Updated?.Invoke();
                if (!videoStarted)
                {
                    SeekDiagnostic?.Invoke(new("session.no-video", Detail: Status));
                    Failed?.Invoke(Status);
                }

                PublishState();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "播放/渲染失败：" + ex.Message.Split('\n')[0];
            Finished = true;
            Clock.Pause();
            ready.TrySetException(ex);
            FailPending(Status);
            Failed?.Invoke(ex.ToString());
            stop.Cancel();
            Decoder.RequestStop();
            lock (gate)
            {
                Record(detector.Reset(Clock.Position, "render_error"));
            }

            PublishState();
        }
        finally
        {
            stop.Cancel();
            await watchdog;
        }
    }

    async Task<bool> Wait(double pts, int generation, double ahead = 0)
    {
        while (!stop.IsCancellationRequested)
        {
            if (seeking || generation != Decoder.Generation)
            {
                return false;
            }

            if (!Clock.Paused && pts - Clock.Position <= ahead)
            {
                return true;
            }

            await Task.Delay(5, stop.Token);
        }

        return false;
    }

    async Task VideoLoop()
    {
        try
        {
            await foreach (var frame in Decoder.Video.Reader.ReadAllAsync(stop.Token))
            {
                using (frame)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (seeking || frame.Generation != Decoder.Generation)
                    {
                        continue;
                    }

                    SeekTicket? ticket;
                    lock (gate)
                    {
                        ticket = pending;
                    }

                    bool seekFrame = ticket != null && ticket.Generation == frame.Generation;
                    if (renderer == null)
                    {
                        renderer = new(hwnd, Decoder.D3DDevice);
                        renderer.Analyzed += AnalyzeVideo;
                        if (Source.IsLive)
                        {
                            await Task.Delay(Source.NetworkCacheMs, stop.Token);
                        }

                        lock (gate)
                        {
                            if (stop.IsCancellationRequested)
                            {
                                return;
                            }
                            Clock.Seek(frame.Pts);
                            if (!seekFrame)
                            {
                                Clock.Resume();
                            }
                            lastVideo = lastAudio = frame.Pts;
                            videoArrival = audioArrival = System.Diagnostics.Stopwatch.GetTimestamp();
                            Status = seekFrame ? "正在跳转…" : "播放监看中";
                        }
                    }

                    if (seekFrame)
                    {
                        Clock.Seek(frame.Pts);
                        Clock.Pause();
                    }
                    else
                    {
                        if (!await Wait(frame.Pts, frame.Generation))
                        {
                            continue;
                        }
                    }

                    lock (gate)
                    {
                        videoArrival = System.Diagnostics.Stopwatch.GetTimestamp();
                        lastVideo = frame.Pts + frame.Duration;
                        DrainDecodeNotices(frame.Pts, frame);
                        lastFrame?.Dispose();
                        unsafe
                        {
                            lastFrame = new(frame.Frame, frame.Pts, frame.Duration, frame.Generation, frame.Info);
                        }
                    }

                    if (!seekFrame && Clock.Position - frame.Pts > .2)
                    {
                        DroppedFrames++;
                        continue;
                    }

                    renderer.DisplayMode = DisplayMode;
                    renderer.Deinterlace = Deinterlace;
                    var info = frame.Info ?? Info!;
                    renderer.Process(frame, info, Settings, !displayPaused && (!info.Interlaced || Deinterlace == DeinterlaceMode.Weave));
                    ProcessedFrames++;
                    if (info.Interlaced && Deinterlace != DeinterlaceMode.Weave && !displayPaused)
                    {
                        int first = Deinterlace == DeinterlaceMode.BottomField ? 2 : Deinterlace == DeinterlaceMode.TopField ? 1 : info.TopFirst ? 1 : 2;
                        renderer.PresentField(info, Settings, first);
                        if (Deinterlace == DeinterlaceMode.Auto && !seekFrame && await Wait(frame.Pts + frame.Duration / 2, frame.Generation))
                        {
                            renderer.PresentField(info, Settings, first == 1 ? 2 : 1);
                        }
                    }

                    lock (gate)
                    {
                        if (!displayPaused)
                        {
                            displayedFrame?.Dispose();
                            unsafe
                            {
                                displayedFrame = new(frame.Frame, frame.Pts, frame.Duration, frame.Generation, frame.Info);
                            }
                        }

                        if (seekFrame && !stop.IsCancellationRequested && ReferenceEquals(pending, ticket) && frame.Generation == Decoder.Generation)
                        {
                            SeekDiagnostic?.Invoke(new("frame.presented", ticket!.Id, ticket.Target, frame.Pts, frame.Generation));
                            pending = null;
                            Clock.Seek(frame.Pts);
                            if (ticket!.Paused)
                            {
                                Clock.Pause();
                                output.Pause();
                                Status = "暂停，文件检测暂停";
                            }
                            else
                            {
                                Clock.Resume();
                                output.Resume();
                                Status = "播放监看中";
                            }

                            ticket!.Completion.TrySetResult(new(ticket.Id, SeekOutcome.Success, frame.Pts));
                        }
                    }

                    if (!videoStarted)
                    {
                        audioResumePosition = frame.Pts;
                        videoStarted = true;
                        firstVideoPresented.TrySetResult();
                    }

                    Updated?.Invoke();
                    PublishState();
                }
            }

            renderer?.DrainAnalysis();
            lock (gate)
            {
                if (pending != null)
                {
                    FailPending("目标位置没有可显示的视频帧。");
                    Finished = true;
                    Status = "目标位置没有视频帧，请重新定位。";
                    Clock.Pause();
                    stop.Cancel();
                    Decoder.RequestStop();
                }
            }

            PublishState();
        }
        finally
        {
            videoCompleted = true;
            firstVideoPresented.TrySetResult();
        }
    }

    void AnalyzeVideo(VideoMetrics metrics, FrameLease frame, Guid id, DetectionSettings settings)
    {
        lock (gate)
        {
            if (seeking || pending != null || frame.Generation != Decoder.Generation || !ReferenceEquals(settings, Settings))
            {
                return;
            }

            AnalyzedFrames++;
            if (previousAnalysis >= 0 && frame.Pts > previousAnalysis)
            {
                double rate = 1 / (frame.Pts - previousAnalysis);
                ActualAnalysisFps = ActualAnalysisFps == 0 ? rate : ActualAnalysisFps * .8 + rate * .2;
            }

            previousAnalysis = frame.Pts;
            // Keep headroom for a confirmed/manual screenshot. A busy screenshot
            // worker must not stop video analysis or enqueue unlimited GPU frames.
            Guid? candidateImage = Volatile.Read(ref captures) < 6 ? id : null;
            var changes = detector.Video(frame.Pts, metrics, candidateImage);
            if (detector.EvidenceIds.Contains(id))
            {
                Capture(id, frame, frame.Info ?? Info!);
            }

            Record(changes);
            Retain();
        }

        PublishState();
    }

    async Task AudioLoop()
    {
        var buffered = new SeekAudioPreroll();
        Task<bool>? available = null;
        int bufferedGeneration = -1;
        long overflowRequest = long.MinValue;
        while (true)
        {
            stop.Token.ThrowIfCancellationRequested();
            if (bufferedGeneration != Decoder.Generation)
            {
                buffered.Clear();
                bufferedGeneration = Decoder.Generation;
            }

            SeekTicket? ticket;
            lock (gate)
            {
                ticket = pending;
            }

            if (ticket == null && videoStarted && buffered.Count > 0)
            {
                AudioBlock? block = SeekAudioPreroll.Trim(buffered.Take(), audioResumePosition);
                if (block != null)
                {
                    await PlayAudioBlockAsync(block);
                }

                continue;
            }

            if (videoCompleted && !videoStarted)
            {
                return;
            }

            available ??= Decoder.Audio.Reader.WaitToReadAsync(stop.Token).AsTask();
            Task? firstFrame = ticket?.Completion.Task ?? (!videoStarted ? firstVideoPresented.Task : null);
            if (firstFrame != null && !firstFrame.IsCompleted && buffered.Count > 0 && await Task.WhenAny(available, firstFrame) != available)
            {
                continue;
            }

            bool hasAudio = await available;
            available = null;
            if (!hasAudio)
            {
                if (firstFrame != null && !firstFrame.IsCompleted)
                {
                    await firstFrame.WaitAsync(stop.Token);
                    continue;
                }

                if (buffered.Count > 0 && videoStarted && pending == null)
                {
                    continue;
                }

                break;
            }

            while (Decoder.Audio.Reader.TryRead(out AudioBlock? block))
            {
                stop.Token.ThrowIfCancellationRequested();
                if (seeking || block.Generation != Decoder.Generation || block.TrackIndex != Decoder.CurrentAudioIndex)
                {
                    continue;
                }

                if (!videoStarted || pending != null || buffered.Count > 0)
                {
                    if (buffered.Add(block) && overflowRequest != (pending?.Id ?? 0))
                    {
                        overflowRequest = pending?.Id ?? 0;
                        SeekDiagnostic?.Invoke(new("audio.preroll-limit", overflowRequest, pending?.Target, block.Pts, block.Generation, "Bounded seek/startup audio staging reached its limit; old preroll discarded, never reported as a source audio fault."));
                    }

                    continue;
                }

                await PlayAudioBlockAsync(block);
            }
        }
    }

    async Task PlayAudioBlockAsync(AudioBlock block)
    {
        if (!await Wait(block.Pts, block.Generation, .04) || switchingAudio || block.TrackIndex != Decoder.CurrentAudioIndex)
        {
            return;
        }

        lock (gate)
        {
            if (seeking || pending != null || block.Generation != Decoder.Generation)
            {
                return;
            }

            audioArrival = System.Diagnostics.Stopwatch.GetTimestamp();
            lastAudio = block.Pts + block.Samples.Length / (double)(block.Channels * block.Rate);
            Record(detector.Audio(block.Pts, block.Samples, block.Channels, block.Rate, block.Names));
            meter.Add(detector.Levels, block.Samples.Length / block.Channels);
        }

        if (AudioOutputEnabled)
        {
            output.Write(block);
        }

        PublishState();
    }

    async Task Watchdog()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(100, stop.Token);
                if (!videoStarted && metadataArrival != 0 && pending == null && !Finished)
                {
                    double waiting = System.Diagnostics.Stopwatch.GetElapsedTime(metadataArrival).TotalSeconds;
                    double limit = Source.OpenTimeoutSeconds + (Source.IsLive ? Source.NetworkCacheMs / 1000d : 0);
                    if (waiting > limit)
                    {
                        const string message = "已读取节目元数据，但等待视频首帧超时。请查看错误详情后重试打开。";
                        lock (gate)
                        {
                            Finished = true;
                            Status = message;
                            Clock.Pause();
                            FailPending(message);
                            Decoder.RequestStop();
                            stop.Cancel();
                        }

                        SeekDiagnostic?.Invoke(new("session.first-frame-timeout", Detail: message));
                        Failed?.Invoke(message);
                        PublishState();
                        return;
                    }
                }

                if (renderer == null || Clock.Paused || seeking || pending != null || Decoder.Ended)
                {
                    continue;
                }

                lock (gate)
                {
                    Guid? id = null;
                    double now = Clock.Position;
                    DrainDecodeNotices(now);
                    long wall = System.Diagnostics.Stopwatch.GetTimestamp();
                    double videoEnd = Math.Max(lastVideo, now - (wall - videoArrival) / (double)System.Diagnostics.Stopwatch.Frequency);
                    double audioEnd = Math.Max(lastAudio, now - (wall - audioArrival) / (double)System.Diagnostics.Stopwatch.Frequency);
                    if (!detector.Active.Contains(AlarmKind.VideoLoss) && now - videoEnd >= Settings.LossSeconds && lastFrame != null)
                    {
                        id = Guid.NewGuid();
                        Capture(id.Value, lastFrame, lastFrame.Info ?? Info!);
                    }

                    Record(detector.Tick(now, videoEnd, tracks.Length > 0 && !switchingAudio ? audioEnd : null, id));
                    Retain();
                }

                PublishState();
            }
        }
        catch (OperationCanceledException ex)
        {
        }
        catch (Exception ex)
        {
            Status = "告警记录失败：" + ex.Message;
            PauseForFailure();
            Failed?.Invoke(Status);
            PublishState();
        }
    }

    void Post(Action action)
    {
        if (!writes.Writer.TryWrite(action))
        {
            throw new ObjectDisposedException(nameof(PlaybackSession));
        }
    }

    async Task WriteLoop()
    {
        await foreach (var action in writes.Reader.ReadAllAsync())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Status = "证据写入失败，监测已停止：" + ex.Message;
                Finished = true;
                PauseForFailure();
                stop.Cancel();
                FailPending(Status);
                Failed?.Invoke(Status);
                PublishState();
            }
        }
    }

    void Capture(Guid id, FrameLease frame, VideoInfo info)
    {
        if (!requested.Add(id))
        {
            return;
        }

        if (Interlocked.Increment(ref captures) > 8)
        {
            Interlocked.Decrement(ref captures);
            throw new IOException("截图队列积压，请检查本机性能或磁盘");
        }

        FrameLease copy;
        unsafe
        {
            copy = new(frame.Frame, frame.Pts, frame.Duration, frame.Generation, frame.Info);
        }

        Post(() =>
        {
            try
            {
                using (copy)
                {
                    evidence.Capture(id, copy, info);
                }
            }
            finally
            {
                Interlocked.Decrement(ref captures);
            }
        });
    }

    void DrainDecodeNotices(double through, FrameLease? frame = null)
    {
        // All callers hold gate. Packet times are labelled as related data times;
        // only a matching damaged video frame is eligible for an exact screenshot.
        foreach (var (notice, time) in decodeNotices.Take(through, Decoder.Generation))
        {
            Guid? image = null;
            if (Settings.DecodeEnabled && !detector.Active.Contains(AlarmKind.Decode) && Volatile.Read(ref captures) < 6 && notice.IsVideo && notice.TimeSource == DecodeTimeSource.Frame && frame != null && Math.Abs(time - frame.Pts) <= .001)
            {
                image = Guid.NewGuid();
                Capture(image.Value, frame, frame.Info ?? Info!);
            }

            Record(detector.Pulse(AlarmKind.Decode, time, notice.Detail, image));
        }
    }

    void Retain()
    {
        var ids = detector.EvidenceIds;
        requested.IntersectWith(ids);
        Post(() => evidence.Retain(ids));
    }

    void Record(IEnumerable<AlarmTransition> changes)
    {
        var copy = changes.ToArray();
        var info = Info;
        var observed = DateTimeOffset.UtcNow;
        double position = Clock.Position;
        foreach (var transition in copy.Where(c => c.Enter))
        {
            AlarmRaised?.Invoke(transition);
        }

        if (copy.Length > 0)
        {
            Post(() => RecordNow(copy, info, observed, position));
            PublishState();
        }
    }

    void RecordNow(IEnumerable<AlarmTransition> changes, VideoInfo? info, DateTimeOffset? observed = null, double position = 0)
    {
        foreach (var change in changes)
        {
            AlarmTransition c = Decoder.SelectedProgram is { } program ? change with
            {
                Detail = ProgramSelection.Describe(program, change.Detail)
            }

            : change;
            string image = c.Enter && c.Evidence is { } id && info != null ? evidence.Save(id, store.Root, info, c.Visual) : "";
            string peak = c.Enter && c.PeakEvidence == c.Evidence ? image : c.PeakEvidence is { } peakId && info != null ? evidence.Save(peakId, store.Root, info, c.PeakVisual) : "";
            var row = store.RecordAsync(c with { PeakSnapshot = peak }, image, observed?.AddSeconds(c.Start - position), observed).GetAwaiter().GetResult();
            if (row != null)
            {
                Recorded?.Invoke(row);
            }
        }
    }

    void PauseForFailure()
    {
        Clock.Pause();
        output.Pause();
        Decoder.RequestStop();
    }

    void FailPending(string detail, SeekOutcome outcome = SeekOutcome.Failed)
    {
        lock (gate)
        {
            var ticket = pending;
            pending = null;
            seeking = false;
            ticket?.Completion.TrySetResult(new(ticket.Id, outcome, Clock.Position, detail));
        }
    }

    public void TogglePause()
    {
        lock (gate)
        {
            if (pending != null)
            {
                return;
            }

            if (Source.IsLive)
            {
                displayPaused = !displayPaused;
                Status = displayPaused ? "显示冻结，直播检测继续" : "播放监看中";
            }
            else
            {
                if (Clock.Paused)
                {
                    Clock.Resume();
                    output.Resume();
                    Status = "播放监看中";
                }
                else
                {
                    Clock.Pause();
                    output.Pause();
                    Status = "暂停，文件检测暂停";
                }
            }
        }

        Updated?.Invoke();
        PublishState();
    }

    public async Task<SeekResult> SeekAsync(double target, long requestId, CancellationToken token = default)
    {
        if (Source.IsLive || Info == null || !Decoder.CanSeek)
        {
            return new(requestId, SeekOutcome.Failed, Clock.Position, "当前来源不支持文件定位。");
        }

        if (RequiresReopenForSeek)
        {
            return new(requestId, SeekOutcome.ReopenRequired, target);
        }

        SeekTicket ticket;
        lock (gate)
        {
            bool paused = pending?.Paused ?? nextSeekPause ?? Clock.Paused;
            nextSeekPause = null;
            FailPending("已被更新的跳转请求替代。", SeekOutcome.Superseded);
            target = Math.Clamp(target, 0, Math.Max(0, Info.Duration - .1));
            pending = ticket = new(requestId, target, paused);
            lastSeekTask = ticket.Completion.Task;
            seeking = true;
            Status = "正在跳转…";
            output.Flush();
            meter.Clear();
            if (!Decoder.Seek(target, requestId))
            {
                pending = null;
                seeking = false;
                return new(requestId, SeekOutcome.ReopenRequired, target);
            }
        }

        PublishState();
        try
        {
            while (!ticket.Completion.Task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                stop.Token.ThrowIfCancellationRequested();
                if (ticket.Generation < 0 && (Decoder.Ended || Decoder.Completion.IsCompleted))
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(pending, ticket))
                        {
                            FailPending("解码已到达结尾，需要重开文件定位。", SeekOutcome.ReopenRequired);
                        }
                    }

                    break;
                }

                await Task.WhenAny(ticket.Completion.Task, Task.Delay(10, token)).ConfigureAwait(false);
            }

            return await ticket.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, ticket))
                {
                    CancelSeek();
                }
            }

            return new(requestId, SeekOutcome.Cancelled, Clock.Position);
        }
        finally
        {
            PublishState();
        }
    }

    public void Seek(double target) => _ = SeekAsync(target, System.Diagnostics.Stopwatch.GetTimestamp());
    public void SelectAudio(int index)
    {
        if (index == Decoder.CurrentAudioIndex || !tracks.Any(t => t.Index == index))
        {
            return;
        }

        lock (gate)
        {
            switchingAudio = true;
            Record(detector.ResetAudio(Clock.Position));
            output.Flush();
            meter.Clear();
            Decoder.SelectAudio(index);
            lastAudio = Clock.Position;
        }

        PublishState();
    }

    public void Configure(DetectionSettings settings)
    {
        settings.Validate();
        lock (gate)
        {
            Record(detector.Reset(Clock.Position, "settings"));
            Settings = settings;
            detector = new(settings);
            meter.Clear();
            Post(() =>
            {
                SessionId = store.BeginAsync(Source.Location, settings).GetAwaiter().GetResult();
                PublishState();
            });
            lastVideo = lastAudio = Clock.Position;
        }

        PublishState();
    }

    public async Task<string> MarkAsync(string note, bool logEvent = true) => (await MarkDetailedAsync(note, logEvent)).Snapshot;
    public Task<(string Snapshot, string? EventId)> MarkDetailedAsync(string note, bool logEvent = true)
    {
        lock (gate)
        {
            var selected = displayedFrame ?? lastFrame;
            if (selected == null || Info == null || pending != null || seeking)
            {
                return Task.FromResult(("", (string? )null));
            }

            var id = Guid.NewGuid();
            var info = selected.Info ?? Info;
            double pts = selected.Pts;
            Capture(id, selected, info);
            var result = new TaskCompletionSource<(string, string? )>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    string image = evidence.Save(id, store.Root, info);
                    string? eventId = null;
                    if (logEvent)
                    {
                        var row = store.RecordAsync(new(AlarmKind.Manual, true, pts, pts, Detail: note, Evidence: id), image, evidence.Wall(id), DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                        eventId = row?.Id;
                        store.RecordAsync(new(AlarmKind.Manual, false, pts, pts, pts, Reason: "manual")).GetAwaiter().GetResult();
                    }

                    result.TrySetResult((image, eventId));
                }
                catch (Exception ex)
                {
                    result.TrySetException(ex);
                    throw;
                }
            });
            Retain();
            return result.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var failures = new List<Exception>();
        void Release(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        lock (gate)
        {
            stop.Cancel();
            ready.TrySetCanceled();
            FailPending("播放已停止", SeekOutcome.Cancelled);
            Decoder.RequestStop();
            Clock.Pause();
        }
        try
        {
            await running;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            lock (gate)
            {
                Record(detector.Reset(Clock.Position, EndReason));
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        finally
        {
            writes.Writer.TryComplete();
        }

        try
        {
            await writer;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        lock (gate)
        {
            Release(() => lastFrame?.Dispose());
            lastFrame = null;
            Release(() => displayedFrame?.Dispose());
            displayedFrame = null;
            Release(() => renderer?.Dispose());
            renderer = null;
            Release(evidence.Dispose);
            Release(output.Dispose);
        }

        Release(Decoder.Dispose);
        Release(stop.Dispose);
        if (failures.Count > 0)
        {
            throw new AggregateException("播放会话退出清理遇到异常，其他资源已继续释放。", failures);
        }
    }
}
