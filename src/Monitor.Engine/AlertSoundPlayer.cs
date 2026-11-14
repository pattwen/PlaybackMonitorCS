using System;
using System.Diagnostics;
using Monitor.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Monitor.Engine;

public sealed class AlertSoundPlayer : IAlertOutput
{
    readonly object gate = new();
    Task<AlertSoundResult> active = Task.FromResult(new AlertSoundResult(AlertSoundState.Cancelled));
    CancellationTokenSource? current;
    long lastRequest;
    bool disposed;
    bool requested;

    public static Task<TimeSpan> ValidateFileAsync(AlertSoundSettings settings) => Task.Run(() =>
    {
        settings.Validate();
        using WaveStream input = settings.FilePath.Length == 0 ? BuiltIn() : new AudioFileReader(settings.FilePath);
        ValidateDuration(input.TotalTime);
        return input.TotalTime;
    });
    static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(10))
        {
            throw new InvalidDataException("请选择时长大于0且不超过10秒的告警音。");
        }
    }

    public Task<AlertSoundResult> PlayAsync(AlertSoundSettings settings, bool preview = false)
    {
        settings.Validate();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!active.IsCompleted)
            {
                return Task.FromResult(new AlertSoundResult(AlertSoundState.Busy));
            }
            if (!preview && requested && Stopwatch.GetElapsedTime(lastRequest).TotalSeconds < settings.CooldownSeconds)
            {
                return Task.FromResult(new AlertSoundResult(AlertSoundState.Cooldown));
            }
            if (!preview)
            {
                requested = true;
                lastRequest = Stopwatch.GetTimestamp();
            }
            var cancellation = new CancellationTokenSource();
            current = cancellation;
            active = Task.Run(async () =>
            {
                try
                {
                    return await PlayCore(settings, cancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                        if (ReferenceEquals(current, cancellation))
                        {
                            current = null;
                        }
                    cancellation.Dispose();
                }
            });
            return active;
        }
    }
    static async Task<AlertSoundResult> PlayCore(AlertSoundSettings settings, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using WaveStream input = settings.FilePath.Length == 0 ? BuiltIn() : new AudioFileReader(settings.FilePath);
            ValidateDuration(input.TotalTime);
            var samples = new VolumeSampleProvider(input.ToSampleProvider()) { Volume = (float)settings.Volume };
            using var output = new WasapiOut(AudioClientShareMode.Shared, false, 80);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) => { if (e.Exception is null) stopped.TrySetResult(); else stopped.TrySetException(e.Exception); };
            output.Init(samples.ToWaveProvider());
            token.ThrowIfCancellationRequested();
            output.Play();
            using var registration = token.Register(output.Stop);
            try
            {
                Task completed = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(11), token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (completed != stopped.Task)
                {
                    throw new TimeoutException("告警音输出超时，请检查声音设备。");
                }
                await stopped.Task.ConfigureAwait(false);
                return new(AlertSoundState.Played);
            }
            finally { output.Stop(); }
        }
        catch (OperationCanceledException) { return new(AlertSoundState.Cancelled); }
        catch (Exception ex) { return new(AlertSoundState.Failed, "告警音播放失败：" + ex.Message); }
    }

    static WaveStream BuiltIn()
    {
        const int rate = 48000;
        float[] samples = new float[rate * 36 / 100];
        for (int i = 0; i < samples.Length; i++)
        {
            double time = i / (double)rate;
            double local = time < .14 ? time : time >= .22 ? time - .22 : -1;
            if (local < 0)
            {
                continue;
            }
            double envelope = Math.Clamp(Math.Min(local / .005, (.14 - local) / .005), 0, 1);
            samples[i] = (float)(.45 * envelope * Math.Sin(2 * Math.PI * (time < .14 ? 880 : 660) * local));
        }
        byte[] bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new RawSourceWaveStream(new MemoryStream(bytes, false), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
            cancellation = current;
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<AlertSoundResult> pending;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            pending = active;
        }
        Stop();
        await pending.ConfigureAwait(false);
    }
}
