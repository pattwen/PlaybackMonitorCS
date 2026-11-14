using System.Globalization;

namespace Monitor.Core;
public enum PlaybackStreamKind
{
    Other,
    Video,
    Audio
}

public sealed record PlaybackStream(int Index, PlaybackStreamKind Kind, bool Default, bool Decodable, int Width = 0, int Height = 0);
public sealed record PlaybackProgram(int Id, int Number, string Name, string Provider, int[] StreamIndexes, bool HasVideo)
{
    public string Label => $"节目 {Number} · {(Name.Length == 0 ? $"ID {Id}" : Name)}" + (Provider.Length == 0 ? "" : $" · {Provider}") + (HasVideo ? "" : "（无视频）");
}

public sealed record PlaybackSelection(PlaybackProgram? Program, int VideoIndex, int? AudioIndex, int[] AudioIndexes);


public static class ProgramSelection
{
    public static string Describe(PlaybackProgram program, string detail)
    {
        return $"[Program:{program.Id.ToString(CultureInfo.InvariantCulture)}] {program.Label} | {detail}";
    }

    public static bool TryReadProgramId(string detail, out int programId)
    {
        programId = 0;
        const string prefix = "[Program:";
        if (!detail.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int end = detail.IndexOf(']', prefix.Length);
        return end > prefix.Length && end < 32 && int.TryParse(detail.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out programId);
    }

    public static PlaybackSelection Resolve(IReadOnlyList<PlaybackProgram> programs, IReadOnlyList<PlaybackStream> streams, int preferredVideo, int? requestedProgram)
    {
        PlaybackProgram? selected = null;
        if (requestedProgram.HasValue)
        {
            selected = programs.FirstOrDefault(item => item.Id == requestedProgram.Value) ?? throw new InvalidDataException($"未找到节目ID {requestedProgram.Value}。节目表可能已改变，请重新打开来源。");
        }
        else
        {
            if (programs.Count > 0)
            {
                selected = programs.FirstOrDefault(item => item.HasVideo && item.StreamIndexes.Contains(preferredVideo)) ?? programs.FirstOrDefault(item => item.HasVideo && streams.Any(stream => item.StreamIndexes.Contains(stream.Index) && stream.Kind == PlaybackStreamKind.Video && stream.Decodable));
            }
        }

        PlaybackStream[] eligible = selected == null ? streams.ToArray() : streams.Where(item => selected.StreamIndexes.Contains(item.Index)).ToArray();
        PlaybackStream[] videos = eligible.Where(item => item.Kind == PlaybackStreamKind.Video && item.Decodable).ToArray();
        PlaybackStream? video = videos.FirstOrDefault(item => item.Index == preferredVideo) ?? videos.OrderByDescending(item => item.Default).ThenByDescending(item => (long)item.Width * item.Height).ThenBy(item => item.Index).FirstOrDefault();
        if (video == null)
        {
            throw new InvalidDataException(selected == null ? "来源中没有可解码的视频流。" : $"{selected.Label}没有可解码的视频流，请选择其他节目或检查FFmpeg解码器支持。");
        }

        PlaybackStream[] audio = eligible.Where(item => item.Kind == PlaybackStreamKind.Audio).ToArray();
        PlaybackStream? defaultAudio = audio.Where(item => item.Decodable).OrderByDescending(item => item.Default).ThenBy(item => item.Index).FirstOrDefault();
        if (audio.Length > 0 && defaultAudio == null)
        {
            throw new InvalidDataException("所选节目包含音频，但当前FFmpeg没有对应音频解码器。");
        }

        return new PlaybackSelection(selected, video.Index, defaultAudio?.Index, audio.Select(item => item.Index).ToArray());
    }
}
