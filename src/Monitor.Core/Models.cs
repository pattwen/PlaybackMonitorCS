using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monitor.Core;
public enum AlarmKind
{
    Black,
    Freeze,
    Mosaic,
    Flower,
    Silence,
    AudioGap,
    Decode,
    VideoLoss,
    AudioLoss,
    InputError,
    Manual,
    Green,
    Gray
}

public enum DisplayMode
{
    Native,
    SdrPreview
}

public enum DeinterlaceMode
{
    Auto,
    Weave,
    TopField,
    BottomField
}

public sealed record DetectionSettings
{
    public bool BlackEnabled { get;
        init;
    } = true;
    public bool GrayEnabled { get;
        init;
    } = true;
    public double GrayMinAreaPercent { get;
        init;
    } = 80;
    public double GrayChromaLimit { get;
        init;
    } = 8;
    public double GrayUniformity { get;
        init;
    } = 3;
    public double GrayMinLevel { get;
        init;
    } = 24;
    public double GrayMaxLevel { get;
        init;
    } = 235;
    public double GraySeconds { get;
        init;
    } = 1;
    public bool GrayRecordBrief { get;
        init;
    } = true;
    public bool FreezeEnabled { get;
        init;
    } = true;
    public bool MosaicEnabled { get;
        init;
    } = true;
    public bool FlowerEnabled { get;
        init;
    } = true;
    public bool VisualLocalEnabled { get;
        init;
    } = true;
    public VisualSensitivity VisualSensitivity { get;
        init;
    } = VisualSensitivity.Normal;
    public double VisualMinAreaPercent { get;
        init;
    } = .2;
    public bool VisualRecordBrief { get;
        init;
    } = true;
    public double VisualConfirmWindowSeconds { get;
        init;
    } = .5;
    public int VisualMinimumHits { get;
        init;
    } = 3;
    public bool GreenEnabled { get;
        init;
    } = true;
    public double VisualGreenAreaPercent { get;
        init;
    } = 2;
    public bool SilenceEnabled { get;
        init;
    } = true;
    public bool AudioGapEnabled { get;
        init;
    } = true;
    public bool DecodeEnabled { get;
        init;
    } = true;
    public bool LossEnabled { get;
        init;
    } = true;
    public double AnalysisFps { get;
        init;
    } = 25;
    public double BlackLevel { get;
        init;
    } = 20;
    public double BlackRatio { get;
        init;
    } = 98;
    public double BlackSeconds { get;
        init;
    } = 2;
    public double HdrBlackNits { get;
        init;
    } = .05;
    public double FreezeDifference { get;
        init;
    } = .5;
    public double FreezeSeconds { get;
        init;
    } = 5;
    public bool FreezeLocalMotionProtection { get;
        init;
    } = false;
    public double FreezePixelDifference { get;
        init;
    } = 2;
    public double FreezeBlockChangedPercent { get;
        init;
    } = 10;
    public double FreezeMotionAreaPercent { get;
        init;
    } = .02;
    public double FreezeReferenceSeconds { get;
        init;
    } = 2;
    public double FreezeMotionHoldSeconds { get;
        init;
    } = 1.25;
    public double MosaicScore { get;
        init;
    } = 3;
    public double MosaicSeconds { get;
        init;
    } = 1.5;
    public double FlowerScore { get;
        init;
    } = 50;
    public double FlowerSeconds { get;
        init;
    } = 1.5;
    public double SilenceDb { get;
        init;
    } = -50;
    public double SilenceSeconds { get;
        init;
    } = 2;
    public double AudioGapSeconds { get;
        init;
    } = .15;
    public double LossSeconds { get;
        init;
    } = 3;
    public double RecoverySeconds { get;
        init;
    } = 1;
    public double RoiX { get;
        init;
    }
    public double RoiY { get;
        init;
    }
    public double RoiWidth { get;
        init;
    } = 100;
    public double RoiHeight { get;
        init;
    } = 100;
    public int[] SilentChannels { get;
        init;
    } = [];

    public void Validate()
    {
        foreach (var(name, value, lo, hi)in new[]
        {
            (nameof(AnalysisFps), AnalysisFps, 1d, 60d),
            (nameof(GrayMinAreaPercent), GrayMinAreaPercent, .1, 100d),
            (nameof(GrayChromaLimit), GrayChromaLimit, 0d, 64d),
            (nameof(GrayUniformity), GrayUniformity, .1, 32d),
            (nameof(GrayMinLevel), GrayMinLevel, 0d, 255d),
            (nameof(GrayMaxLevel), GrayMaxLevel, 0d, 255d),
            (nameof(BlackLevel), BlackLevel, 0d, 100d),
            (nameof(BlackRatio), BlackRatio, 50d, 100d),
            (nameof(FreezeDifference), FreezeDifference, 0d, 20d),
            (nameof(MosaicScore), MosaicScore, 1.1, 30d),
            (nameof(FlowerScore), FlowerScore, 10d, 150d),
            (nameof(SilenceDb), SilenceDb, -100d, -1d),
            (nameof(AudioGapSeconds), AudioGapSeconds, .04, 3d),
            (nameof(RoiX), RoiX, 0d, 99d),
            (nameof(RoiY), RoiY, 0d, 99d),
            (nameof(RoiWidth), RoiWidth, 1d, 100d),
            (nameof(RoiHeight), RoiHeight, 1d, 100d),
            (nameof(HdrBlackNits), HdrBlackNits, 0d, 10d),
            (nameof(VisualMinAreaPercent), VisualMinAreaPercent, .01, 100d),
            (nameof(VisualGreenAreaPercent), VisualGreenAreaPercent, .01, 100d),
            (nameof(VisualConfirmWindowSeconds), VisualConfirmWindowSeconds, .1, 5d),
            (nameof(FreezePixelDifference), FreezePixelDifference, 0d, 255d),
            (nameof(FreezeBlockChangedPercent), FreezeBlockChangedPercent, .1, 100d),
            (nameof(FreezeMotionAreaPercent), FreezeMotionAreaPercent, .001, 100d),
            (nameof(FreezeReferenceSeconds), FreezeReferenceSeconds, .1, 30d),
            (nameof(FreezeMotionHoldSeconds), FreezeMotionHoldSeconds, 0d, 10d)
        }

        )
            if (!double.IsFinite(value) || value < lo || value > hi)
            {
                throw new ArgumentException($"{name}: {lo}～{hi}");
            }

        foreach (double value in new[]
        {
            BlackSeconds,
            GraySeconds,
            FreezeSeconds,
            MosaicSeconds,
            FlowerSeconds,
            SilenceSeconds,
            LossSeconds,
            RecoverySeconds
        }

        )
        {
            if (!double.IsFinite(value) || value < .1 || value > 3600)
            {
                throw new ArgumentException("持续时间应为0.1～3600秒");
            }
        }

        if (GrayMinLevel >= GrayMaxLevel)
        {
            throw new ArgumentException("灰屏亮度下限必须小于上限。");
        }

        if (RoiX + RoiWidth > 100 || RoiY + RoiHeight > 100)
        {
            throw new ArgumentException("检测区域超出画面");
        }

        if (!Enum.IsDefined(VisualSensitivity) || VisualMinimumHits is < 1 or > 125)
        {
            throw new ArgumentException("视觉灵敏度或窗口内异常次数无效（1～125）。");
        }

        if (SilentChannels.Any(c => c < 0 || c > 31))
        {
            throw new ArgumentException("声道编号无效");
        }
    }
}

public sealed record SourceProfile
{
    public string Id { get;
        init;
    } = Guid.NewGuid().ToString("N");
    public string Name { get;
        init;
    } = "新输入";
    public string Location { get;
        init;
    } = "";
    public bool IsLive { get;
        init;
    }
    public int NetworkCacheMs { get;
        init;
    } = 1000;
    public int OpenTimeoutSeconds { get;
        init;
    } = 15;
    public string RtspTransport { get;
        init;
    } = "tcp";
    public bool HardwareDecode { get;
        init;
    } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || string.IsNullOrWhiteSpace(Location) || Location.Any(char.IsControl))
        {
            throw new ArgumentException("名称或地址无效");
        }

        if (IsLive && (!Uri.TryCreate(Location, UriKind.Absolute, out var u) || !new[]
        {
            "rtmp",
            "rtmps",
            "rtsp",
            "rtsps",
            "http",
            "https",
            "udp",
            "rtp",
            "srt"
        }.Contains(u.Scheme)))
        {
            throw new ArgumentException("不支持的媒体协议");
        }

        if (NetworkCacheMs < 100 || NetworkCacheMs > 20000 || OpenTimeoutSeconds < 3 || OpenTimeoutSeconds > 120 || RtspTransport is not ("tcp" or "udp"))
        {
            throw new ArgumentException("连接参数无效");
        }
    }
}

public sealed record AppSettings
{
    public DetectionSettings Detection { get;
        init;
    } = new();
    public List<SourceProfile> Sources { get;
        init;
    } = [];
    public DisplayMode DisplayMode { get;
        init;
    } = DisplayMode.Native;
    public DeinterlaceMode Deinterlace { get;
        init;
    } = DeinterlaceMode.Auto;
    public bool Beep { get;
        init;
    }
    public string LastSourceId { get;
        init;
    } = "";
    public AlertSoundSettings AlertSound { get;
        init;
    } = new();
}

public sealed record VideoInfo(int Width, int Height, double Fps, int BitDepth, string PixelFormat, string Codec, string Profile, string Range, string Primaries, string Transfer, string Matrix, bool Interlaced, bool TopFirst, double Duration)
{
    public bool IsPq => Transfer.Contains("2084", StringComparison.OrdinalIgnoreCase);
    public bool IsHlg => Transfer.Contains("b67", StringComparison.OrdinalIgnoreCase);
    public bool Hdr => IsPq || IsHlg;
    public double SampleAspectRatio { get;
        init;
    } = 1;
    public string HdrMetadataJson { get;
        init;
    } = "";
    public int ChromaLocation { get;
        init;
    } = 1;
}

public sealed record AudioTrack(int Index, string Label, int SampleRate, int Channels, string Layout, string[] Names);
public sealed record ChannelLevel(string Name, double RmsDb, double PeakDb);
public sealed record VideoMetrics(double BlackRatio, double Difference, double BlockScore, double NoiseScore, double[] Thumbnail)
{
    public VisualFrameMetrics? Visual { get;
        init;
    }
    public VisualEvidence? Gray { get;
        init;
    }
    public FreezeMotionMetrics? LocalMotion { get;
        init;
    }
}

public sealed record AlarmTransition(AlarmKind Kind, bool Enter, double Start, double Confirmed, double? End = null, string Detail = "", string Reason = "", Guid? Evidence = null)
{
    public VisualEvidence? Visual { get;
        init;
    }
    public Guid? PeakEvidence { get;
        init;
    }
    public VisualEvidence? PeakVisual { get;
        init;
    }
    public string PeakSnapshot { get;
        init;
    } = "";
}

public sealed record AlarmEvent
{
    public string Id { get;
        init;
    } = Guid.NewGuid().ToString("N");
    public string SessionId { get;
        init;
    } = "";
    public string Source { get;
        init;
    } = "";
    public AlarmKind Kind { get;
        init;
    }
    public double Start { get;
        init;
    }
    public double Confirmed { get;
        init;
    }
    public double? End { get;
        init;
    }
    public DateTimeOffset DetectedAt { get;
        init;
    } = DateTimeOffset.UtcNow;
    public DateTimeOffset? OnsetAt { get;
        init;
    }
    public DateTimeOffset? ClosedAt { get;
        init;
    }
    public string Reason { get;
        init;
    } = "active";
    public string Detail { get;
        init;
    } = "";
    public string Snapshot { get;
        init;
    } = "";
    public string PeakSnapshot { get;
        init;
    } = "";
    public VisualEvidence? Visual { get;
        init;
    }
    public VisualEvidence? PeakVisual { get;
        init;
    }
    public string Review { get;
        init;
    } = "待复核";
    public string Note { get;
        init;
    } = "";

    [JsonIgnore]
    public string Label => Text.Label(Kind);

    [JsonIgnore]
    public string StartText => Text.Timecode(Start);

    [JsonIgnore]
    public string ConfirmedText => Text.Timecode(Confirmed);

    [JsonIgnore]
    public string EndText => End is { } e ? $"{Text.Timecode(e)} / {e - Start:F2}s" : "—";

    [JsonIgnore]
    public string Status => Text.Reason(Reason);

    [JsonIgnore]
    public string WallTime => DetectedAt.ToLocalTime().ToString("MM-dd HH:mm:ss.fff");
}

public static class Text
{
    public static string Label(AlarmKind kind) => kind switch
    {
        AlarmKind.Black => "黑画面",
        AlarmKind.Freeze => "疑似静帧",
        AlarmKind.Mosaic => "疑似马赛克",
        AlarmKind.Flower => "疑似花屏",
        AlarmKind.Green => "疑似绿屏",
        AlarmKind.Gray => "疑似灰屏",
        AlarmKind.Silence => "静音/缺音",
        AlarmKind.AudioGap => "疑似音频断续",
        AlarmKind.Decode => "解码/数据异常",
        AlarmKind.VideoLoss => "视频中断",
        AlarmKind.AudioLoss => "音频中断",
        AlarmKind.InputError => "打开/读取失败",
        _ => "人工标记"
    };
    public static string Reason(string reason) => reason switch
    {
        "active" => "进行中",
        "recovered" => "已恢复",
        "eof" => "播放结束",
        "seek" => "跳转中断",
        "settings" => "设置变更",
        "stopped" => "已停止",
        "source" => "切换输入",
        "closed" => "退出中断",
        "interrupted" => "程序异常中断",
        "sample_gap" => "采样中断",
        "manual" => "人工记录",
        "track" => "音轨切换",
        "input_error" => "输入失败",
        "render_error" => "本机渲染异常",
        _ => reason
    };
    public static string Timecode(double s)
    {
        long ms = (long)Math.Max(0, Math.Round(s * 1000));
        return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";
    }

    public static string SafeSource(string source)
    {
        if (!source.Contains("://"))
        {
            return source;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var u))
        {
            return "[无效地址]";
        }

        var b = new UriBuilder(u)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        return b.Uri.GetLeftPart(UriPartial.Path);
    }

    public static string Csv(string value) => "\"" + ((value.Length > 0 && "=+-@\t\r".Contains(value[0])) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
}

public static class Configuration
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new();
        }

        var c = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("配置为空");
        c.Detection.Validate();
        c.AlertSound.Validate();
        foreach (var p in c.Sources)
        {
            p.Validate();
        }

        return c;
    }

    public static void Save(string path, AppSettings settings)
    {
        settings.Detection.Validate();
        settings.AlertSound.Validate();
        foreach (var p in settings.Sources)
        {
            p.Validate();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, Json));
        File.Move(path + ".tmp", path, true);
    }
}
