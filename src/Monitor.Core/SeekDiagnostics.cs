using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace Monitor.Core;

public sealed record SeekDiagnostic(string Stage, long RequestId = 0, double? Target = null, double? Position = null, int? Generation = null, string Detail = "");

public sealed class SeekDiagnostics : IAsyncDisposable
{
    readonly object gate = new();
    readonly Queue<string> history = new();
    readonly Channel<string> lines = Channel.CreateBounded<string>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    readonly Task writer;
    int disposed;
    public string Path
    {
        get;
    }
    public string? WriteError
    {
        get; private set;
    }
    public SeekDiagnostics(string root)
    {
        Path = System.IO.Path.Combine(root, "seek-diagnostics.log");
        writer = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(root);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1024 * 1024)
                {
                    File.Move(Path, Path + ".previous", true);
                }
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous);
                using var output = new StreamWriter(stream);
                while (await lines.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (lines.Reader.TryRead(out var line))
                    {
                        await output.WriteLineAsync(line).ConfigureAwait(false);
                    }
                    await output.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                WriteError = ex.Message;
            }
        });
    }
    public void Write(SeekDiagnostic entry)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        string line = FormattableString.Invariant($"{DateTimeOffset.UtcNow:O} thread={Environment.CurrentManagedThreadId} stage={entry.Stage} id={entry.RequestId} target={entry.Target:F3} pts={entry.Position:F3} generation={entry.Generation} {entry.Detail}");
        lock (gate)
        {
            history.Enqueue(line);
            while (history.Count > 200)
            {
                history.Dequeue();
            }
        }
        lines.Writer.TryWrite(line);
    }
    public string Snapshot()
    {
        lock (gate)
            return string.Join(Environment.NewLine, history);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        lines.Writer.TryComplete();
        await writer.ConfigureAwait(false);
    }
}
