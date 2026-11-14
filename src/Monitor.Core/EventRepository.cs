using System.IO;
using System.Threading.Tasks;
using System.Threading.Channels;
namespace Monitor.Core;


public sealed class EventRepository : IAsyncDisposable
{
    readonly Channel<Action<EventStore>> jobs = Channel.CreateBounded<Action<EventStore>>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly Task worker;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int disposed;
    readonly object readersGate = new();
    readonly HashSet<Task> readers = [];
    public string Root
    {
        get;
    }
    public Task Ready => ready.Task;
    public event Action<EventChange>? Changed;
    public EventRepository(string root)
    {
        Root = Path.GetFullPath(root);
        worker = Task.Run(async () =>
        {
            try
            {
                using var store = new EventStore(Root);
                store.Changed += change => Changed?.Invoke(change);
                ready.TrySetResult();
                await foreach (var action in jobs.Reader.ReadAllAsync())
                {
                    action(store);
                }
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                jobs.Writer.TryComplete(ex);
                throw;
            }
        });
    }
    async Task<T> Run<T>(Func<EventStore, T> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await Ready.ConfigureAwait(false);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await jobs.Writer.WriteAsync(store => { try { result.TrySetResult(action(store)); } catch (Exception ex) { result.TrySetException(ex); } }).ConfigureAwait(false);
        return await result.Task.ConfigureAwait(false);
    }
    public Task<string> BeginAsync(string source, DetectionSettings settings) => Run(store => { store.Begin(source, settings); return store.SessionId; });
    public Task<string> ResumeAsync(string id) => Run(store => { store.ResumeSession(id); return store.SessionId; });
    public Task<AlarmEvent?> RecordAsync(AlarmTransition change, string snapshot = "", DateTimeOffset? onset = null, DateTimeOffset? detected = null) => Run(store => store.Record(change, snapshot, onset, detected));
    public Task<AlarmEvent> ReviewAsync(string id, string review, string note) => Run(store => store.Review(id, review, note));
    Task<T> Read<T>(Func<T> action, CancellationToken token = default)
    {
        lock (readersGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            var task = Task.Run(action, token);
            readers.Add(task);
            _ = task.ContinueWith(done => { lock (readersGate) readers.Remove(done); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    public async Task<EventPage> ReadPageAsync(string? session, string markerSession, CancellationToken token = default)
    {
        await Ready.WaitAsync(token).ConfigureAwait(false);
        return await Read(() => EventStore.ReadPage(Root, session, markerSession, token: token), token).ConfigureAwait(false);
    }
    public async Task ExportAsync(string path, string? session)
    {
        await Ready.ConfigureAwait(false);
        await Read(() => { EventStore.ExportReadOnly(Root, path, session); return true; }).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        Task[] activeReads;
        lock (readersGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            jobs.Writer.TryComplete();
            activeReads = readers.ToArray();
        }
        await Task.WhenAll(activeReads.Append(worker)).ConfigureAwait(false);
    }
}
