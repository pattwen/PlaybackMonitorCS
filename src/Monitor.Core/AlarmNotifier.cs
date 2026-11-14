namespace Monitor.Core;

public enum AlertSoundState
{
    Played,
    Busy,
    Cooldown,
    Cancelled,
    Failed
}
public sealed record AlertSoundResult(AlertSoundState State, string Detail = "");

public interface IAlertOutput : IAsyncDisposable
{
    Task<AlertSoundResult> PlayAsync(AlertSoundSettings settings, bool preview = false);
    void Stop();
}

public sealed class AlarmNotifier(IAlertOutput output) : IAsyncDisposable
{
    readonly object gate = new();
    AlertSoundSettings settings = new(); string source = ""; long epoch;
    bool enabled, disposed;
    public event Action<string>? Failed;
    public void Configure(bool value, AlertSoundSettings options)
    {
        options.Validate();
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            if (enabled != value || settings != options)
            {
                epoch++;
                output.Stop();
            }
            enabled = value;
            settings = options;
        }
    }
    public void SetSource(string identity)
    {
        lock (gate)
        {
            source = identity;
            epoch++;
            output.Stop();
        }
    }
    bool Current(string identity, long version)
    {
        lock (gate)
            return !disposed && enabled && source == identity && epoch == version;
    }
    public async Task NotifyAsync(string identity)
    {
        long version = -1;
        AlertSoundSettings options;
        Task<AlertSoundResult> pending;
        try
        {
            lock (gate)
            {
                if (disposed || !enabled || source != identity)
                {
                    return;
                }
                version = epoch;
                options = settings;
                pending = output.PlayAsync(options);
            }
            var result = await pending.ConfigureAwait(false);
            if (result.State != AlertSoundState.Failed || !Current(identity, version))
            {
                return;
            }
            string message = result.Detail;
            if (options.FilePath.Length > 0)
            {
                lock (gate)
                {
                    if (!Current(identity, version))
                    {
                        return;
                    }
                    pending = output.PlayAsync(options with
                    {
                        FilePath = ""
                    }, true);
                }
                var fallback = await pending.ConfigureAwait(false);
                message += fallback.State == AlertSoundState.Played ? " 已使用内置提示音。" : " 内置提示音未能输出：" + fallback.Detail;
            }
            if (Current(identity, version))
            {
                Failed?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            if (Current(identity, version))
            {
                Failed?.Invoke("提示音失败：" + ex.Message);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            epoch++;
            output.Stop();
        }
        await output.DisposeAsync().ConfigureAwait(false);
    }
}
