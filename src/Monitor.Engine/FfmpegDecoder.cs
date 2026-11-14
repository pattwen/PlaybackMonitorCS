using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Text.Json;
using FFmpeg.AutoGen;
using Monitor.Core;

namespace Monitor.Engine;
public sealed unsafe class FfmpegDecoder : IDisposable
{
    readonly SourceProfile source;
    readonly CancellationTokenSource stop = new();
    readonly int? requestedProgram;
    int[] eligibleAudioIndexes = [];
    public PlaybackProgram[] Programs
    {
        get;
        private set;
    } = [];
    public PlaybackProgram? SelectedProgram
    {
        get;
        private set;
    }

    sealed record SeekCommand(double Target, long Id, double Preroll = 0, bool Initial = false);
    SeekCommand? seekCommand;
    SeekCommand? decodingSeek;
    long latestSeekId;
    int retryGeneration = -1;
    bool targetFrameSeen;
    readonly Channel<PacketMessage> packets = Channel.CreateBounded<PacketMessage>(512);
    Task reader = Task.CompletedTask;
    long queuedBytes;
    sealed class PacketMessage(AVPacket* packet, int generation, string? command = null, double value = 0, long requestId = 0, double preroll = 0)
    {
        public AVPacket* Packet = packet;
        public int Generation = generation;
        public string? Command = command;
        public double Value = value;
        public long RequestId = requestId;
        public double Preroll = preroll;
    }

    void Release(PacketMessage message)
    {
        var p = message.Packet;
        if (p != null)
        {
            Interlocked.Add(ref queuedBytes, -p->size);
            ffmpeg.av_packet_free(&p);
            message.Packet = null;
        }
    }

    void Queue(PacketMessage message)
    {
        if (message.Packet != null)
        {
            Interlocked.Add(ref queuedBytes, message.Packet->size);
        }

        try
        {
            while (!packets.Writer.TryWrite(message))
            {
                stop.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref seekCommand) != null)
                {
                    Release(message);
                    return;
                }

                stop.Token.WaitHandle.WaitOne(3);
            }
        }
        catch
        {
            Release(message);
            throw;
        }
    }

    AVFormatContext* format;
    AVCodecContext* video;
    AVCodecContext* audio;
    AVBufferRef* hardware;
    SwrContext* resampler;
    int videoIndex = -1;
    int audioIndex = -1;
    int pendingAudio = -1;
    int generation;
    int decodingGeneration;
    int channels;
    double origin = double.NaN;
    double seekTarget;
    long deadline;
    readonly AVIOInterruptCB_callback interrupt;
    readonly AVCodecContext_get_format chooseFormat;
    string[] names = [];
    bool disposed;
    bool nativeInitialized;
    VideoInfo? rawInfo;
    public Channel<FrameLease> Video
    {
        get;
    } = Channel.CreateBounded<FrameLease>(8);
    public Channel<AudioBlock> Audio
    {
        get;
    } = Channel.CreateBounded<AudioBlock>(160);
    public VideoInfo? Info
    {
        get;
        private set;
    }
    public List<AudioTrack> Tracks
    {
        get;
    } = [];
    public string DecodeMode
    {
        get;
        private set;
    } = "初始化";
    public nint D3DDevice
    {
        get;
        private set;
    }

    public event Action<string>? Notice;
    public event Action<DecodeNotice>? DetailedNotice;
    public event Action<string>? Fatal;
    public event Action<VideoInfo>? Metadata;
    public event Action<double, int, long>? Seeked;
    public event Action<SeekDiagnostic>? SeekDiagnostic;
    public event Action<int>? AudioChanged;
    public int CurrentAudioIndex => Volatile.Read(ref audioIndex);
    public Task Completion
    {
        get;
        private set;
    } = Task.CompletedTask;
    public int Generation => Volatile.Read(ref generation);

    volatile bool ended;
    public bool Ended => ended;
    public bool CanSeek
    {
        get;
        private set;
    }
    public bool CanAcceptSeek => CanSeek && !ended && !Completion.IsCompleted && !stop.IsCancellationRequested;

    public FfmpegDecoder(SourceProfile profile, int? programId = null)
    {
        source = profile;
        requestedProgram = programId;
        interrupt = opaque => stop.IsCancellationRequested || System.Diagnostics.Stopwatch.GetTimestamp() > Interlocked.Read(ref deadline) ? 1 : 0;
        chooseFormat = (ctx, list) =>
        {
            for (var p = list;
                *p != AVPixelFormat.AV_PIX_FMT_NONE;
                p++)
            {
                if (*p == AVPixelFormat.AV_PIX_FMT_D3D11)
                {
                    return *p;
                }
            }

            return list[0];
        };
    }

    public void Start() => Completion = Task.Run(Run);
    internal void PrepareInitialSeek(double pts, long requestId)
    {
        Interlocked.Exchange(ref latestSeekId, requestId);
        Interlocked.Exchange(ref seekCommand, new(pts, requestId, Initial: true));
        SeekDiagnostic?.Invoke(new("decoder.initial", requestId, pts));
    }

    public bool Seek(double pts, long requestId = 0)
    {
        if (!CanAcceptSeek)
        {
            return false;
        }

        Interlocked.Exchange(ref latestSeekId, requestId);
        Interlocked.Exchange(ref seekCommand, new(pts, requestId));
        SeekDiagnostic?.Invoke(new("decoder.queued", requestId, pts, Generation: Generation));
        return true;
    }

    public void SelectAudio(int index)
    {
        if (Tracks.Any(t => t.Index == index))
        {
            Interlocked.Exchange(ref pendingAudio, index);
        }
    }

    void ApplyAudioChange()
    {
        int index = Interlocked.Exchange(ref pendingAudio, -1);
        if (index < 0 || index == audioIndex)
        {
            return;
        }

        ChangeAudio(index);
        while (Audio.Reader.TryRead(out _))
        {
        }

        AudioChanged?.Invoke(index);
    }

    void WriteVideo(FrameLease lease)
    {
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (lease.Generation != Generation)
                {
                    lease.Dispose();
                    return;
                }

                if (Video.Writer.TryWrite(lease))
                {
                    return;
                }

                stop.Token.ThrowIfCancellationRequested();
                ApplyAudioChange();
                stop.Token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception ex)
        {
            lease.Dispose();
            throw;
        }
    }

    void Deadline() => Interlocked.Exchange(ref deadline, System.Diagnostics.Stopwatch.GetTimestamp() + source.OpenTimeoutSeconds * System.Diagnostics.Stopwatch.Frequency);
    AVCodecContext* Codec(int index, bool hw)
    {
        var p = format->streams[index]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(p->codec_id);
        if (codec == null)
        {
            throw new NotSupportedException("无解码器");
        }

        var c = ffmpeg.avcodec_alloc_context3(codec);
        if (c == null)
        {
            throw new OutOfMemoryException();
        }

        try
        {
            NativeRuntime.Check(ffmpeg.avcodec_parameters_to_context(c, p));
            c->pkt_timebase = format->streams[index]->time_base;
            c->thread_count = 0;
            if (hw)
            {
                bool supported = false;
                for (int i = 0;
                    ;
                    i++)
                {
                    var config = ffmpeg.avcodec_get_hw_config(codec, i);
                    if (config == null)
                    {
                        break;
                    }

                    if (config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
                    {
                        supported = true;
                        break;
                    }
                }

                AVBufferRef* device = null;
                if (supported && ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) >= 0)
                {
                    hardware = device;
                    c->hw_device_ctx = ffmpeg.av_buffer_ref(device);
                    c->get_format = chooseFormat;
                    D3DDevice = (nint)((AVD3D11VADeviceContext*)((AVHWDeviceContext*)device->data)->hwctx)->device;
                }
            }

            NativeRuntime.Check(ffmpeg.avcodec_open2(c, codec, null));
            return c;
        }
        catch (Exception ex)
        {
            ffmpeg.avcodec_free_context(&c);
            throw;
        }
    }

    void Open()
    {
        NativeRuntime.Initialize();
        nativeInitialized = true;
        source.Validate();
        Deadline();
        format = ffmpeg.avformat_alloc_context();
        format->interrupt_callback = new()
        {
            callback = interrupt
        };
        AVDictionary* options = null;
        AVInputFormat* requestedInput = null;
        try
        {
            if (Path.GetExtension(source.Location).Equals(".raw", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(source.Location, ".json")))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(source.Location, ".json")));
                var root = json.RootElement;
                rawInfo = root.GetProperty("Video").Deserialize<VideoInfo>(Configuration.Json) ?? throw new InvalidDataException("截图元数据无效");
                rawInfo = rawInfo with
                {
                    Width = root.GetProperty("Width").GetInt32(),
                    Height = root.GetProperty("Height").GetInt32()
                };
                ffmpeg.av_dict_set(&options, "video_size", $"{rawInfo.Width}x{rawInfo.Height}", 0);
                ffmpeg.av_dict_set(&options, "pixel_format", root.GetProperty("PixelFormat").GetString(), 0);
                ffmpeg.av_dict_set(&options, "framerate", "1", 0);
                requestedInput = ffmpeg.av_find_input_format("rawvideo");
            }

            ffmpeg.av_dict_set(&options, "rw_timeout", (source.OpenTimeoutSeconds * 1_000_000L).ToString(), 0);
            if (source.Location.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
            {
                ffmpeg.av_dict_set(&options, "rtsp_transport", source.RtspTransport, 0);
            }

            if (source.IsLive)
            {
                ffmpeg.av_dict_set(&options, "buffer_size", Math.Max(1048576, source.NetworkCacheMs * 4096).ToString(), 0);
            }

            if (source.IsLive)
            {
                ffmpeg.av_dict_set(&options, "max_delay", (source.NetworkCacheMs * 1000L).ToString(), 0);
            }

            var f = format;
            int opened = ffmpeg.avformat_open_input(&f, source.Location, requestedInput, &options);
            format = f;
            NativeRuntime.Check(opened);
            NativeRuntime.Check(ffmpeg.avformat_find_stream_info(format, null));
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        Programs = ProgramCatalog.Read(format);
        int preferredVideo = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        PlaybackSelection selection = ProgramSelection.Resolve(Programs, ProgramCatalog.Streams(format), preferredVideo, requestedProgram);
        SelectedProgram = selection.Program;
        eligibleAudioIndexes = selection.AudioIndexes;
        videoIndex = selection.VideoIndex;
        var p = format->streams[videoIndex]->codecpar;
        var rate = ffmpeg.av_guess_frame_rate(format, format->streams[videoIndex], null);
        var desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)p->format);
        bool compatibleHardware = source.HardwareDecode && !(desc != null && desc->log2_chroma_w == 1 && desc->log2_chroma_h == 0) && !(p->codec_id == AVCodecID.AV_CODEC_ID_H264 && desc != null && desc->comp[0].depth > 8);
        video = Codec(videoIndex, compatibleHardware);
        CanSeek = !source.IsLive && format->duration > 0;
        SeekDiagnostic?.Invoke(new("decoder.metadata", Detail: $"live={source.IsLive} duration={format->duration} avioFlags={(format->pb == null ? -1 : format->pb->seekable)} CanSeek={CanSeek}"));
        Info = new(p->width, p->height, rate.den == 0 ? 25 : ffmpeg.av_q2d(rate), desc == null ? 8 : desc->comp[0].depth, ffmpeg.av_get_pix_fmt_name((AVPixelFormat)p->format) ?? "待解码", ffmpeg.avcodec_get_name(p->codec_id), ffmpeg.avcodec_profile_name(p->codec_id, p->profile) ?? "", p->color_range.ToString(), p->color_primaries.ToString(), p->color_trc.ToString(), p->color_space.ToString(), p->field_order is not (AVFieldOrder.AV_FIELD_UNKNOWN or AVFieldOrder.AV_FIELD_PROGRESSIVE), p->field_order is AVFieldOrder.AV_FIELD_TT or AVFieldOrder.AV_FIELD_TB, format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : 0);
        Info = Info with
        {
            ChromaLocation = (int)p->chroma_location > 0 ? (int)p->chroma_location : 1
        };
        if (rawInfo != null)
        {
            Info = rawInfo with
            {
                Duration = 1
            };
        }

        byte* description = stackalloc byte[256];
        for (int i = 0;
            i < format->nb_streams;
            i++)
        {
            var a = format->streams[i]->codecpar;
            if (a->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO || !selection.AudioIndexes.Contains(i))
            {
                continue;
            }

            ffmpeg.av_channel_layout_describe(&a->ch_layout, description, 256);
            Tracks.Add(new(i, $"音轨{i} · {ffmpeg.avcodec_get_name(a->codec_id)} · {a->ch_layout.nb_channels}声道", a->sample_rate, a->ch_layout.nb_channels, Marshal.PtrToStringUTF8((nint)description) ?? "", Names(a->ch_layout)));
        }

        var sar = ffmpeg.av_guess_sample_aspect_ratio(format, format->streams[videoIndex], null);
        if (rawInfo == null && sar.den > 0 && sar.num > 0)
        {
            Info = Info with
            {
                SampleAspectRatio = ffmpeg.av_q2d(sar)
            };
        }

        if (selection.AudioIndex.HasValue)
        {
            ChangeAudio(selection.AudioIndex.Value);
        }

        AVStream* selectedVideo = format->streams[videoIndex];
        if (selectedVideo->start_time != ffmpeg.AV_NOPTS_VALUE)
        {
            origin = selectedVideo->start_time * ffmpeg.av_q2d(selectedVideo->time_base);
        }
        else
        {
            if (format->start_time != ffmpeg.AV_NOPTS_VALUE)
            {
                origin = format->start_time / (double)ffmpeg.AV_TIME_BASE;
            }
        }

        if (selectedVideo->duration > 0 && selectedVideo->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            Info = Info with
            {
                Duration = selectedVideo->duration * ffmpeg.av_q2d(selectedVideo->time_base)
            };
        }

        SeekDiagnostic?.Invoke(new("decoder.program-timeline", Detail: $"program={SelectedProgram?.Id} video={videoIndex} audio={audioIndex} origin={origin:R} streamStart={selectedVideo->start_time} timebase={selectedVideo->time_base.num}/{selectedVideo->time_base.den}"));
        Metadata?.Invoke(Info);
    }

    static string[] Names(AVChannelLayout layout)
    {
        var result = new string[layout.nb_channels];
        byte* b = stackalloc byte[64];
        for (uint i = 0;
            i < result.Length;
            i++)
        {
            var c = ffmpeg.av_channel_layout_channel_from_index(&layout, i);
            ffmpeg.av_channel_name(b, 64, c);
            result[i] = c == AVChannel.AV_CHAN_NONE ? $"CH{i + 1}" : Marshal.PtrToStringUTF8((nint)b) ?? $"CH{i + 1}";
        }

        return result;
    }

    void ChangeAudio(int index)
    {
        if (!Tracks.Any(t => t.Index == index))
        {
            return;
        }

        var old = audio;
        ffmpeg.avcodec_free_context(&old);
        audio = null;
        var r = resampler;
        ffmpeg.swr_free(&r);
        resampler = null;
        audioIndex = index;
        audio = Codec(index, false);
        channels = audio->ch_layout.nb_channels;
        names = Names(audio->ch_layout);
        SwrContext* swr = null;
        NativeRuntime.Check(ffmpeg.swr_alloc_set_opts2(&swr, &audio->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, 48000, &audio->ch_layout, audio->sample_fmt, audio->sample_rate, 0, null));
        resampler = swr;
        NativeRuntime.Check(ffmpeg.swr_init(resampler));
    }

    double Time(AVFrame* frame, int stream)
    {
        long value = frame->best_effort_timestamp;
        if (value == ffmpeg.AV_NOPTS_VALUE)
        {
            value = frame->pts;
        }

        if (value == ffmpeg.AV_NOPTS_VALUE)
        {
            throw new InvalidDataException("缺少PTS，无法准确定位");
        }

        double pts = value * ffmpeg.av_q2d(format->streams[stream]->time_base);
        if (double.IsNaN(origin))
        {
            origin = pts;
        }

        return pts - origin;
    }

    double? PacketTime(AVPacket* packet)
    {
        long timestamp = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE || double.IsNaN(origin))
        {
            return null;
        }

        return Math.Max(0, timestamp * ffmpeg.av_q2d(format->streams[packet->stream_index]->time_base) - origin);
    }

    void ReportDecode(string message, double? time, int stream, DecodeTimeSource timeSource)
    {
        Notice?.Invoke(message);
        DetailedNotice?.Invoke(new(message, time, decodingGeneration, stream, timeSource, stream == videoIndex));
    }

    void Drain(AVCodecContext* c, bool isVideo)
    {
        var f = ffmpeg.av_frame_alloc();
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                int result = ffmpeg.avcodec_receive_frame(c, f);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                if (result < 0)
                {
                    ReportDecode("解码帧异常：" + NativeRuntime.Error(result), null, isVideo ? videoIndex : audioIndex, DecodeTimeSource.Unknown);
                    break;
                }

                if (decodingGeneration != Generation)
                {
                    ffmpeg.av_frame_unref(f);
                    break;
                }

                if (retryGeneration == decodingGeneration)
                {
                    ffmpeg.av_frame_unref(f);
                    break;
                }

                double pts = Time(f, isVideo ? videoIndex : audioIndex);
                double audioFrameEnd = isVideo ? pts : pts + f->nb_samples / (double)Math.Max(1, f->sample_rate);
                if (pts + .001 < seekTarget && (isVideo || audioFrameEnd <= seekTarget))
                {
                    ffmpeg.av_frame_unref(f);
                    continue;
                }

                if (isVideo && !targetFrameSeen && decodingSeek is { } active)
                {
                    targetFrameSeen = true;
                    double tolerance = Math.Max(.12, 2 / Math.Max(1, Info?.Fps ?? 25));
                    if (pts - seekTarget > tolerance && active.Id == Interlocked.Read(ref latestSeekId))
                    {
                        double limit = Math.Min(active.Target, 30);
                        double preroll = Math.Min(limit, Math.Max(2, active.Preroll * 2));
                        if (preroll > active.Preroll)
                        {
                            var retry = active with
                            {
                                Preroll = preroll
                            };
                            if (Interlocked.CompareExchange(ref seekCommand, retry, null) == null)
                            {
                                retryGeneration = decodingGeneration;
                                SeekDiagnostic?.Invoke(new("reader.retry", active.Id, active.Target, pts, decodingGeneration, $"preroll={preroll:F3}"));
                            }

                            ffmpeg.av_frame_unref(f);
                            break;
                        }

                        SeekDiagnostic?.Invoke(new("decoder.precision-warning", active.Id, active.Target, pts, decodingGeneration, "No closer decodable frame after preroll; reporting actual PTS."));
                    }
                }

                if ((f->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0 || f->decode_error_flags != 0)
                {
                    ReportDecode($"损坏解码帧；错误标记 0x{f->decode_error_flags:X}", pts, isVideo ? videoIndex : audioIndex, DecodeTimeSource.Frame);
                }

                if (isVideo)
                {
                    var pixel = (AVPixelFormat)f->format;
                    if (pixel == AVPixelFormat.AV_PIX_FMT_D3D11 && f->hw_frames_ctx != null)
                    {
                        pixel = ((AVHWFramesContext*)f->hw_frames_ctx->data)->sw_format;
                    }

                    var descriptor = ffmpeg.av_pix_fmt_desc_get(pixel);
                    if (Info != null)
                    {
                        Info = Info with
                        {
                            ChromaLocation = (int)f->chroma_location > 0 ? (int)f->chroma_location : Info.ChromaLocation,
                            Width = f->width,
                            Height = f->height,
                            BitDepth = descriptor == null ? Info.BitDepth : descriptor->comp[0].depth,
                            PixelFormat = ffmpeg.av_get_pix_fmt_name(pixel) ?? Info.PixelFormat,
                            Interlaced = rawInfo?.Interlaced ?? ((f->flags & 8) != 0),
                            TopFirst = rawInfo?.TopFirst ?? ((f->flags & 16) != 0),
                            Transfer = (int)f->color_trc != 2 ? f->color_trc.ToString() : Info.Transfer,
                            Primaries = (int)f->color_primaries != 2 ? f->color_primaries.ToString() : Info.Primaries,
                            Matrix = (int)f->colorspace != 2 ? f->colorspace.ToString() : Info.Matrix,
                            Range = (int)f->color_range != 0 ? f->color_range.ToString() : Info.Range
                        };
                    }

                    if (Info != null)
                    {
                        Info = Info with
                        {
                            HdrMetadataJson = Info.Hdr ? FrameMetadata.Hdr(f, Info.HdrMetadataJson) : ""
                        };
                    }

                    DecodeMode = (AVPixelFormat)f->format == AVPixelFormat.AV_PIX_FMT_D3D11 ? "D3D11VA硬解" : "软件解码";
                    double duration = f->duration > 0 ? f->duration * ffmpeg.av_q2d(format->streams[videoIndex]->time_base) : 1 / (Info?.Fps ?? 25);
                    WriteVideo(new FrameLease(f, pts, duration, decodingGeneration, Info));
                }
                else
                {
                    int capacity = ffmpeg.swr_get_out_samples(resampler, f->nb_samples);
                    var samples = new float[Math.Max(1, capacity) * channels];
                    int count;
                    fixed (float* ptr = samples)
                    {
                        byte* output = (byte*)ptr;
                        count = ffmpeg.swr_convert(resampler, &output, capacity, f->extended_data, f->nb_samples);
                        NativeRuntime.Check(count);
                    }

                    Array.Resize(ref samples, count * channels);
                    if (samples.Length > 0)
                    {
                        AudioBlock? block = SeekAudioPreroll.Trim(new AudioBlock(samples, channels, 48000, names, pts, decodingGeneration, audioIndex), seekTarget);
                        if (block == null)
                        {
                            ffmpeg.av_frame_unref(f);
                            continue;
                        }

                        while (!Audio.Writer.TryWrite(block))
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            if (Volatile.Read(ref pendingAudio) >= 0 || decodingGeneration != Generation)
                            {
                                return;
                            }

                            stop.Token.WaitHandle.WaitOne(5);
                        }
                    }
                }

                ffmpeg.av_frame_unref(f);
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&f);
        }
    }


    void PrimeVideo(SeekCommand command)
    {
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        long bytes = 0;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        bool primed = false;
        SeekDiagnostic?.Invoke(new("decoder.prime.begin", command.Id, command.Target, Detail: $"video={videoIndex} program={SelectedProgram?.Id}"));
        try
        {
            if (packet == null || frame == null)
            {
                throw new OutOfMemoryException();
            }

            while (!primed && bytes < 64L * 1024 * 1024 && System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds < Math.Min(5, source.OpenTimeoutSeconds))
            {
                stop.Token.ThrowIfCancellationRequested();
                Deadline();
                int read = ffmpeg.av_read_frame(format, packet);
                stop.Token.ThrowIfCancellationRequested();
                if (read < 0)
                {
                    break;
                }

                bytes += Math.Max(0, packet->size);
                if (packet->stream_index == videoIndex)
                {
                    int sent = ffmpeg.avcodec_send_packet(video, packet);
                    while (sent >= 0 || sent == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        int received = ffmpeg.avcodec_receive_frame(video, frame);
                        if (received < 0)
                        {
                            break;
                        }

                        if (double.IsNaN(origin))
                        {
                            long timestamp = frame->best_effort_timestamp;
                            if (timestamp == ffmpeg.AV_NOPTS_VALUE)
                            {
                                timestamp = frame->pts;
                            }

                            if (timestamp != ffmpeg.AV_NOPTS_VALUE)
                            {
                                origin = timestamp * ffmpeg.av_q2d(format->streams[videoIndex]->time_base);
                            }
                        }

                        primed = true;
                        ffmpeg.av_frame_unref(frame);
                        break;
                    }
                }

                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
            SeekDiagnostic?.Invoke(new("decoder.prime.end", command.Id, command.Target, Detail: $"primed={primed} bytes={bytes} elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds:F3}s origin={origin:R}"));
        }
    }

    void ReadPackets()
    {
        AVPacket* packet = null;
        bool atEnd = false;
        try
        {
            packet = ffmpeg.av_packet_alloc();
            while (!stop.IsCancellationRequested)
            {
                var command = Interlocked.Exchange(ref seekCommand, null);
                if (command != null)
                {
                    if (command.Initial && Info is { Duration: > 0 } info)
                    {
                        double maximum = Math.Max(0, info.Duration - Math.Max(.1, 1 / Math.Max(1, info.Fps)));
                        if (command.Target > maximum)
                        {
                            SeekDiagnostic?.Invoke(new("reader.target-clamped", command.Id, command.Target, maximum));
                            command = command with
                            {
                                Target = maximum
                            };
                        }
                    }

                    Deadline();
                    SeekDiagnostic?.Invoke(new("reader.begin", command.Id, command.Target, Generation: Generation));
                    if (!command.Initial || command.Target > 0)
                    {
                        double absolute = Math.Max(0, command.Target - command.Preroll) + (double.IsNaN(origin) ? 0 : origin);
                        long microseconds = checked((long)Math.Round(absolute * ffmpeg.AV_TIME_BASE));
                        AVRational seconds = new()
                        {
                            num = 1,
                            den = ffmpeg.AV_TIME_BASE
                        };
                        AVRational timebase = format->streams[videoIndex]->time_base;
                        long timestamp = ffmpeg.av_rescale_q(microseconds, seconds, timebase);
                        NativeRuntime.Check(ffmpeg.avformat_seek_file(format, videoIndex, long.MinValue, timestamp, timestamp, 0));
                        SeekDiagnostic?.Invoke(new("reader.positioned", command.Id, command.Target, Detail: $"stream={videoIndex} timestamp={timestamp} timebase={timebase.num}/{timebase.den} preroll={command.Preroll:F3}"));
                    }
                    else
                    {
                        SeekDiagnostic?.Invoke(new("reader.positioned", command.Id, command.Target, Detail: "fresh source at start; native seek skipped"));
                    }

                    Interlocked.Increment(ref generation);
                    atEnd = false;
                    while (packets.Reader.TryRead(out var old))
                    {
                        Release(old);
                    }

                    Queue(new(null, Generation, "seek", command.Target, command.Id, command.Preroll));
                }

                if (atEnd)
                {
                    stop.Token.WaitHandle.WaitOne(10);
                    continue;
                }

                while (Interlocked.Read(ref queuedBytes) > 32 * 1024 * 1024 && !stop.IsCancellationRequested && Volatile.Read(ref seekCommand) == null)
                {
                    stop.Token.WaitHandle.WaitOne(3);
                }

                if (Volatile.Read(ref seekCommand) != null)
                {
                    continue;
                }

                Deadline();
                int read = ffmpeg.av_read_frame(format, packet);
                if (read == ffmpeg.AVERROR_EOF)
                {
                    Queue(new(null, Generation, "eof"));
                    atEnd = true;
                    continue;
                }

                if (read < 0)
                {
                    throw new IOException("读取失败：" + NativeRuntime.Error(read));
                }

                if (packet->stream_index != videoIndex && !eligibleAudioIndexes.Contains(packet->stream_index))
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var clone = ffmpeg.av_packet_clone(packet);
                if (clone == null)
                {
                    throw new OutOfMemoryException();
                }

                Queue(new(clone, Generation));
                ffmpeg.av_packet_unref(packet);
            }

            packets.Writer.TryComplete();
        }
        catch (OperationCanceledException ex)
        {
            packets.Writer.TryComplete();
        }
        catch (Exception ex2)
        {
            packets.Writer.TryComplete(ex2);
        }
        finally
        {
            if (packet != null)
            {
                ffmpeg.av_packet_free(&packet);
            }
        }
    }

    bool SeekPending()
    {
        return Volatile.Read(ref seekCommand) != null || retryGeneration == decodingGeneration || Interlocked.Read(ref latestSeekId) != (decodingSeek?.Id ?? 0);
    }

    void Run()
    {
        try
        {
            Open();
            if (Volatile.Read(ref seekCommand)is { Initial: true, Target: > 0 } initial)
            {
                PrimeVideo(initial);
            }

            reader = Task.Run(ReadPackets);
            while (!Ended && packets.Reader.WaitToReadAsync(stop.Token).AsTask().GetAwaiter().GetResult())
            {
                while (packets.Reader.TryRead(out var message))
                {
                    try
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        if (message.Generation != Generation)
                        {
                            continue;
                        }

                        ApplyAudioChange();
                        decodingGeneration = message.Generation;
                        if (message.Command == "eof")
                        {
                            if (SeekPending())
                            {
                                SeekDiagnostic?.Invoke(new("decoder.eof-awaiting-seek", decodingSeek?.Id ?? 0));
                                continue;
                            }

                            ffmpeg.avcodec_send_packet(video, null);
                            Drain(video, true);
                            if (SeekPending())
                            {
                                SeekDiagnostic?.Invoke(new("decoder.eof-retry", decodingSeek?.Id ?? 0));
                                continue;
                            }

                            if (audio != null)
                            {
                                ffmpeg.avcodec_send_packet(audio, null);
                                Drain(audio, false);
                            }

                            ended = true;
                            break;
                        }

                        if (message.Command == "seek")
                        {
                            ffmpeg.avcodec_flush_buffers(video);
                            if (audio != null)
                            {
                                ffmpeg.avcodec_flush_buffers(audio);
                            }

                            if (resampler != null)
                            {
                                ffmpeg.swr_close(resampler);
                                NativeRuntime.Check(ffmpeg.swr_init(resampler));
                            }

                            seekTarget = message.Value;
                            decodingSeek = new(message.Value, message.RequestId, message.Preroll);
                            targetFrameSeen = false;
                            retryGeneration = -1;
                            while (Video.Reader.TryRead(out var old))
                            {
                                old.Dispose();
                            }

                            while (Audio.Reader.TryRead(out _))
                            {
                            }

                            SeekDiagnostic?.Invoke(new("decoder.flushed", message.RequestId, message.Value, Generation: message.Generation));
                            Seeked?.Invoke(message.Value, message.Generation, message.RequestId);
                            continue;
                        }

                        if (message.Command == "audio")
                        {
                            ChangeAudio((int)message.Value);
                            while (Audio.Reader.TryRead(out _))
                            {
                            }

                            continue;
                        }

                        var packet = message.Packet;
                        var target = packet->stream_index == videoIndex ? video : packet->stream_index == audioIndex ? audio : null;
                        if (target != null)
                        {
                            if ((packet->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) != 0)
                            {
                                ReportDecode("损坏媒体包", PacketTime(packet), packet->stream_index, DecodeTimeSource.Packet);
                            }

                            int result = ffmpeg.avcodec_send_packet(target, packet);
                            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            {
                                Drain(target, target == video);
                                result = ffmpeg.avcodec_send_packet(target, packet);
                            }

                            if (result >= 0)
                            {
                                Drain(target, target == video);
                            }
                            else
                            {
                                ReportDecode("解码包异常：" + NativeRuntime.Error(result), PacketTime(packet), packet->stream_index, DecodeTimeSource.Packet);
                            }
                        }
                    }
                    finally
                    {
                        Release(message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SeekDiagnostic?.Invoke(new("decoder.error", Detail: ex.ToString()));
            if (!stop.IsCancellationRequested)
            {
                Fatal?.Invoke(source.IsLive && ex is not (DirectoryNotFoundException or DllNotFoundException) ? "输入打开/读取失败，请检查地址、网络、凭据及协议支持。" : ex.Message);
            }
        }
        finally
        {
            stop.Cancel();
            try
            {
                reader.GetAwaiter().GetResult();
            }
            finally
            {
                Video.Writer.TryComplete();
                Audio.Writer.TryComplete();
            }
        }
    }

    public void RequestStop() => stop.Cancel();
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stop.Cancel();
        Completion.GetAwaiter().GetResult();
        if (!nativeInitialized)
        {
            stop.Dispose();
            return;
        }

        while (Video.Reader.TryRead(out var f))
        {
            f.Dispose();
        }

        while (packets.Reader.TryRead(out var p))
        {
            Release(p);
        }

        var v = video;
        ffmpeg.avcodec_free_context(&v);
        var a = audio;
        ffmpeg.avcodec_free_context(&a);
        var r = resampler;
        ffmpeg.swr_free(&r);
        var c = format;
        ffmpeg.avformat_close_input(&c);
        var h = hardware;
        ffmpeg.av_buffer_unref(&h);
        stop.Dispose();
    }
}
