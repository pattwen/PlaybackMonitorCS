using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Monitor.Core;

namespace Monitor.App;


public sealed class RecordBrowser : IAsyncDisposable
{
    readonly EventRepository repository; 
    readonly Dispatcher dispatcher;
    readonly EventProjection projection = new(); 
    readonly ConcurrentQueue<EventChange> pending = new();
    readonly CancellationTokenSource stop = new();
    int scheduled, disposed, generation; 
    bool loading; 
    string? filter; 
    string current = "";
    readonly List<Task> loads = [];
    public ObservableCollection<AlarmEvent> Rows { get; } = [];
    public long Count => projection.Count;
    public TimelineEntry[] Markers => projection.Markers;
    public bool Loading => loading;
    public event Action? Applying; 
    public event Action? Changed;
    public event Action<string>? Failed;
    public RecordBrowser(EventRepository repository, Dispatcher dispatcher)
    {
        this.repository = repository;
        this.dispatcher = dispatcher;
        repository.Changed += Enqueue;
    }
    void Enqueue(EventChange change)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        pending.Enqueue(change);
        Schedule();
    }
    void Schedule()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref scheduled, 1) != 0)
        {
            return;
        }
        if (dispatcher.HasShutdownStarted)
        {
            return;
        }
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Drain));
    }
    public Task ResetAsync(string? session, string markerSession)
    {
        dispatcher.VerifyAccess();
        filter = session;
        current = markerSession;
        int version = ++generation;
        loading = true;
        Changed?.Invoke();
        loads.RemoveAll(task => task.IsCompleted);
        var load = LoadAsync(version, session, markerSession);
        loads.Add(load);
        return load;
    }
    async Task LoadAsync(int version, string? session, string markerSession)
    {
        try
        {
            var snapshot = await repository.ReadPageAsync(session, markerSession, stop.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (disposed != 0 || version != generation)
                {
                    return;
                }
                Applying?.Invoke();
                projection.Reset(snapshot, session, markerSession);
                SyncRows();
                loading = false;
                Changed?.Invoke();
                Schedule();
            }, DispatcherPriority.Background, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref disposed) == 0 && !dispatcher.HasShutdownStarted)
                await dispatcher.InvokeAsync(() =>
                {
                    if (disposed == 0 && version == generation)
                    {
                        loading = false;
                        Failed?.Invoke("记录加载失败：" + ex.Message);
                        Schedule();
                    }
                });
        }
    }

    void Drain()
    {
        Interlocked.Exchange(ref scheduled, 0);
        if (disposed != 0 || loading)
        {
            return;
        }

        if (pending.Count > 4096)
        {
            while (pending.TryDequeue(out _))
            {
            }
            _ = ResetAsync(filter, current);
            return;
        }
        var batch = new List<EventChange>();
        while (batch.Count < 128 && pending.TryDequeue(out var change))
        {
            batch.Add(change);
        }
        if (batch.Count > 0)
        {
            Applying?.Invoke();
            if (projection.Apply(batch))
            {
                SyncRows();
                Changed?.Invoke();
            }
        }
        if (!pending.IsEmpty)
        {
            Schedule();
        }
    }
    void SyncRows()
    {
        var target = projection.Rows;
        for (int i = 0; i < target.Length; i++)
        {
            if (i < Rows.Count && Rows[i].Id == target[i].Id)
            {
                if (!ReferenceEquals(Rows[i], target[i]))
                {
                    Rows[i] = target[i];
                }
                continue;
            }
            int existing = -1;
            for (int j = i + 1; j < Rows.Count; j++)
                if (Rows[j].Id == target[i].Id)
                {
                    existing = j;
                    break;
                }
            if (existing >= 0)
            {
                Rows.Move(existing, i);
                if (!ReferenceEquals(Rows[i], target[i]))
                {
                    Rows[i] = target[i];
                }
            }
            else
            {
                Rows.Insert(i, target[i]);
            }
        }
        while (Rows.Count > target.Length)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        repository.Changed -= Enqueue;
        stop.Cancel();
        try
        {
            await Task.WhenAll(loads);
        }
        catch (OperationCanceledException)
        {
        }
        while (pending.TryDequeue(out _))
        {
        }
        stop.Dispose();
    }
}
