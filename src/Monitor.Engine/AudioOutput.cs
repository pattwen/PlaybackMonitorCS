using Monitor.Core;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
namespace Monitor.Engine;


public sealed class AudioOutput : IDisposable
{
    readonly object gate = new();
    WasapiOut? device; BufferedWaveProvider? buffer;
    NAudio.CoreAudioApi.MMDevice? endpoint;
    DateTime retryAfter;
    string[] speakers = ["FL", "FR"];
    public float Volume { get; set; } = .7f;
    public bool Muted
    {
        get; set;
    }
    public int SoloChannel { get; set; } = -1;
    public string Status { get; private set; } = "等待音频";
    public void Write(AudioBlock block)
    {
        lock (gate)
        {
            if (device?.PlaybackState == PlaybackState.Stopped)
            {
                device.Dispose();
                endpoint?.Dispose();
                endpoint = null;
                device = null;
                buffer = null;
            }
            if (buffer == null)
            {
                if (DateTime.UtcNow < retryAfter)
                    return;
                try
                {
                    using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    endpoint = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                    var mix = endpoint.AudioClient.MixFormat;
                    int count = Math.Clamp(mix.Channels, 1, 8), mask = count switch
                    {
                        1 => 4,
                        2 => 3,
                        4 => 0x33,
                        6 => 0x3F,
                        8 => 0x63F,
                        _ => (1 << count) - 1
                    };
                    if (mix is WaveFormatExtensible)
                    {
                        nint pointer = WaveFormat.MarshalToPtr(mix);
                        try
                        {
                            mask = Marshal.ReadInt32(pointer, 20);
                        }
                        finally { Marshal.FreeHGlobal(pointer); }
                    }
                    string[] all = ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC", "BC", "SL", "SR", "TC", "TFL", "TFC", "TFR", "TBL", "TBC", "TBR"];
                    speakers = all.Where((_, i) => (mask & (1 << i)) != 0).Take(count).ToArray();
                    if (speakers.Length != count)
                    {
                        speakers = Enumerable.Range(1, count).Select(i => $"CH{i}").ToArray();
                    }
                    try
                    {
                        Initialize(Format(count, mask));
                    }
                    catch
                    when (count > 2)
                    {
                        device?.Dispose();
                        speakers = ["FL", "FR"];
                        Initialize(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
                    }
                    Status = $"WASAPI共享模式 · {speakers.Length}声道监听（检测保留源声道）";
                }
                catch (Exception ex)
                {
                    device?.Dispose();
                    endpoint?.Dispose();
                    endpoint = null;
                    device = null;
                    buffer = null;
                    Status = "声音设备不可用：" + ex.Message;
                    retryAfter = DateTime.UtcNow.AddSeconds(5);
                    return;
                }
            }
            if (buffer == null)
                return;
            var stereo = AudioRouter.Route(block.Samples, block.Channels, block.Names, speakers, Volume, Muted, SoloChannel);
            if (buffer.BufferedDuration.TotalMilliseconds > 250)
            {
                buffer.ClearBuffer();
                Status = "本机音频积压，已清除过期监听数据";
            }
            byte[] bytes = new byte[stereo.Length * 4];
            Buffer.BlockCopy(stereo, 0, bytes, 0, bytes.Length);
            buffer.AddSamples(bytes, 0, bytes.Length);
        }
    }
    void Initialize(WaveFormat format)
    {
        buffer = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromSeconds(.4), DiscardOnBufferOverflow = true };
        device = new WasapiOut(endpoint!, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 80);
        device.Init(buffer);
        device.Play();
    }
    static WaveFormat Format(int channels, int mask)
    {
        if (channels <= 2)
        {
            return WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        }
        byte[] bytes = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 48000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 48000 * channels * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)(channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), mask);
        new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray().CopyTo(bytes, 24);
        nint pointer = Marshal.AllocHGlobal(40);
        try
        {
            Marshal.Copy(bytes, 0, pointer, 40);
            return WaveFormat.MarshalFromPtr(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
    public void Pause()
    {
        lock (gate)
            device?.Pause();
    }
    public void Resume()
    {
        lock (gate)
            device?.Play();
    }
    public void Flush()
    {
        lock (gate)
            buffer?.ClearBuffer();
    }
    public void Dispose()
    {
        lock (gate)
        {
            device?.Stop();
            device?.Dispose();
            endpoint?.Dispose();
            endpoint = null;
            device = null;
            buffer = null;
        }
    }
}
