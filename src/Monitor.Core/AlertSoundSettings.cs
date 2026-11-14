namespace Monitor.Core;

public sealed record AlertSoundSettings
{
    public string FilePath { get; init; } = "";
    public double Volume { get; init; } = .7;
    public double CooldownSeconds { get; init; } = 2;
    public void Validate()
    {
        if (!double.IsFinite(Volume) || Volume < 0 || Volume > 1)
        {
            throw new ArgumentException("告警音量应为0～1。");
        }
        if (!double.IsFinite(CooldownSeconds) || CooldownSeconds < 0 || CooldownSeconds > 60)
        {
            throw new ArgumentException("告警音间隔应为0～60秒。");
        }
        if (FilePath is null || FilePath.Any(char.IsControl))
        {
            throw new ArgumentException("告警音文件路径无效。");
        }
        if (FilePath.Length > 0 && !new[] { ".wav", ".mp3" }.Contains(Path.GetExtension(FilePath), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("音频素材封装无效，只支持 .mp3 或 .wav。");
        }
    }
}
