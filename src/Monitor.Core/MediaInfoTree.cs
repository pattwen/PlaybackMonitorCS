using System.Text;
using System.Text.Json;

namespace Monitor.Core;

public sealed record MediaInfoNode(string Name, string Value, IReadOnlyList<MediaInfoNode> Children)
{
    public string DisplayText => Value.Length == 0 ? Name : Name + "：" + Value;
}

public sealed record MediaInfoReport(string Source, string RawJson, IReadOnlyList<MediaInfoNode> Nodes);

public static class MediaInfoTree
{
    static readonly IReadOnlyDictionary<string, string> TrackNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["General"] = "常规",
        ["Video"] = "视频",
        ["Audio"] = "音频",
        ["Text"] = "字幕",
        ["Menu"] = "菜单",
        ["Other"] = "其他",
        ["Image"] = "图片"
    };
    static readonly IReadOnlyDictionary<string, string> FieldNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // 基础与结构
        // ["creatingLibrary"] = "创建库",
        ["name"] = "名称",
        // ["version"] = "版本",
        // ["url"] = "播放地址",
        // ["media"] = "媒体",
        // ["@ref"] = "文件路径",
        ["track"] = "轨道",
        // ["@type"] = "轨道类型",
        // ["@typeorder"] = "同类型序号",
        ["ID"] = "轨道 ID",
        ["StreamOrder"] = "流顺序",
        ["Title"] = "标题",
        ["Language"] = "语言",
        ["Default"] = "默认轨道",
        ["AlternateGroup"] = "备选组",
        ["extra"] = "扩展信息",

        // 轨道计数与文件信息
        ["VideoCount"] = "视频轨数",
        ["AudioCount"] = "音频轨数",
        ["MenuCount"] = "菜单轨数",
        ["FileExtension"] = "文件扩展名",
        ["FileSize"] = "文件大小",
        ["HeaderSize"] = "文件头大小",
        ["DataSize"] = "数据大小",
        ["FooterSize"] = "文件尾大小",
        ["IsStreamable"] = "支持流式传输",
        ["File_Created_Date"] = "文件创建时间 (UTC)",
        ["File_Created_Date_Local"] = "文件创建时间 (本地)",
        ["File_Modified_Date"] = "文件修改时间 (UTC)",
        ["File_Modified_Date_Local"] = "文件修改时间 (本地)",
        ["Encoded_Application"] = "编码应用",
        ["Encoded_Library"] = "编码库",

        // 格式与编码
        ["Format"] = "格式",
        ["Format_Profile"] = "格式配置",
        ["Format_Level"] = "格式级别",
        ["Format_Settings_CABAC"] = "CABAC",
        ["Format_Settings_RefFrames"] = "参考帧数",
        ["Format_Settings_SBR"] = "SBR 频段复制",
        ["Format_AdditionalFeatures"] = "附加特性",
        ["CodecID"] = "编码标识",
        ["CodecID_Compatible"] = "兼容编码标识",
        ["CodecConfigurationBox"] = "编码配置盒",
        ["Compression_Mode"] = "压缩模式",

        // 时长、码率与大小
        ["Duration"] = "时长",
        ["Source_Duration"] = "源时长",
        ["Source_Duration_LastFrame"] = "源末帧时长",
        ["OverallBitRate_Mode"] = "总码率模式",
        ["OverallBitRate"] = "总码率",
        ["BitRate_Mode"] = "码率模式",
        ["BitRate"] = "码率",
        ["BitRate_Maximum"] = "最大码率",
        ["BufferSize"] = "缓冲区大小",
        ["StreamSize"] = "流大小",
        ["Source_StreamSize"] = "源流大小",

        // 视频画面与帧率
        ["Width"] = "宽度",
        ["Height"] = "高度",
        ["Stored_Height"] = "存储高度",
        ["Sampled_Width"] = "采样宽度",
        ["Sampled_Height"] = "采样高度",
        ["PixelAspectRatio"] = "像素宽高比",
        ["DisplayAspectRatio"] = "显示宽高比",
        ["Rotation"] = "旋转角度",
        ["FrameRate"] = "帧率",
        ["FrameRate_Mode"] = "帧率模式",
        ["FrameRate_Num"] = "帧率分子",
        ["FrameRate_Den"] = "帧率分母",
        ["FrameCount"] = "帧数",
        ["Source_FrameCount"] = "源帧数",
        ["ScanType"] = "扫描方式",
        ["ScanOrder"] = "场序",

        // 色彩与 HDR
        ["BitDepth"] = "位深",
        ["ColorSpace"] = "色彩空间",
        ["ChromaSubsampling"] = "色度采样",
        ["colour_primaries"] = "色彩原色",
        ["transfer_characteristics"] = "传递函数",
        ["matrix_coefficients"] = "矩阵系数",
        ["colour_range"] = "色彩范围",
        ["HDR_Format"] = "HDR格式",

        // 音频与菜单
        ["Channels"] = "声道数",
        ["ChannelPositions"] = "声道位置",
        ["ChannelLayout"] = "声道布局",
        ["SamplingRate"] = "采样率",
        ["SamplesPerFrame"] = "每帧采样数",
        ["SamplingCount"] = "采样总数",
        ["Source_Delay"] = "源延迟",
        ["Source_Delay_Source"] = "源延迟来源",
        ["Menus"] = "关联菜单",
        ["Menu_For"] = "关联轨道"
    };

    public static MediaInfoReport Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Object ||
            !media.TryGetProperty("track", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Array)

        {
            throw new InvalidDataException("返回结果不是包含轨道信息的 MediaInfo JSON。");
        }
        string source = media.TryGetProperty("@ref", out var reference) ? Value(reference) : "";
        var nodes = new List<MediaInfoNode>();
        foreach (var property in root.EnumerateObject().Where(p => p.Name != "media"))
        {
            nodes.Add(Node(property.Name, property.Value));
        }
        foreach (var property in media.EnumerateObject().Where(p => p.Name is not ("track" or "@ref")))
        {
            nodes.Add(Node(property.Name, property.Value));
        }
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("MediaInfo 轨道条目不是对象。");
            }
            string type = track.TryGetProperty("@type", out var kind) ? Value(kind) : "轨道";
            int number = counters.GetValueOrDefault(type) + 1;
            counters[type] = number;
            string name = TrackNames.TryGetValue(type, out var translated) ? translated : type;
            nodes.Add(new(name + " #" + number, "", track.EnumerateObject().Select(p => Node(p.Name, p.Value)).ToArray()));
        }
        return new(source, json, nodes);
    }
    static MediaInfoNode Node(string key, JsonElement value)
    {
        string name = FieldNames.TryGetValue(key, out var translated) ? $"{translated} ({key})" : key;
        return value.ValueKind switch
        {
            JsonValueKind.Object => new(name, "", value.EnumerateObject().Select(p => Node(p.Name, p.Value)).ToArray()),
            JsonValueKind.Array => new(name, "", value.EnumerateArray().Select((p, i) => Node($"[{i}]", p)).ToArray()),
            _ => new(name, Value(value), [])
        };
    }
    static string Value(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    public static string ToText(IEnumerable<MediaInfoNode> nodes)
    {
        var text = new StringBuilder();
        void Append(MediaInfoNode node, int depth)
        {
            text.Append(' ', depth * 2).AppendLine(node.DisplayText);
            foreach (var child in node.Children)
                Append(child, depth + 1);
        }
        foreach (var node in nodes)
            Append(node, 0);
        return text.ToString();
    }
}
