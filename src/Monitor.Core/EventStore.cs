using System.IO;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Monitor.Core;

public sealed class EventStore : IDisposable
{
    readonly SqliteConnection db; readonly object gate = new();
    readonly Dictionary<AlarmKind, string> active = [];
    public string Root
    {
        get;
    }
    public string SessionId { get; private set; } = "";
    string source = "";
    public event Action<EventChange>? Changed;
    public EventStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "snapshots"));
        db = Open(root, false);
        try
        {
            Execute("PRAGMA journal_mode=WAL");
            Execute("CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,source TEXT,opened TEXT,settings TEXT)");
            Execute("CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY,session TEXT,kind INTEGER,payload TEXT,sort_time INTEGER NOT NULL DEFAULT 0)");
            bool hasSort;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(events)";
                using var reader = cmd.ExecuteReader();
                hasSort = false;
                while (reader.Read())
                {
                    hasSort |= reader.GetString(1) == "sort_time";
                }
            }
            if (!hasSort)
            {
                Execute("ALTER TABLE events ADD COLUMN sort_time INTEGER NOT NULL DEFAULT 0");
            }
            Execute("CREATE TABLE IF NOT EXISTS event_changes(seq INTEGER PRIMARY KEY AUTOINCREMENT,event_id TEXT NOT NULL,session TEXT NOT NULL,added INTEGER NOT NULL)");
            Execute("CREATE INDEX IF NOT EXISTS ix_event_changes_id ON event_changes(event_id,seq)");
            Execute("CREATE INDEX IF NOT EXISTS ix_events_session_time ON events(session,sort_time DESC,id DESC)");
            Execute("CREATE INDEX IF NOT EXISTS ix_events_time ON events(sort_time DESC,id DESC)");


            while (true)
            {
                var batch = Query(db, "SELECT payload FROM events WHERE sort_time=0 LIMIT 256");
                if (batch.Count == 0)
                    break;
                using var tx = db.BeginTransaction();
                foreach (var row in batch)
                {
                    using var command = db.CreateCommand();
                    command.Transaction = tx;
                    command.CommandText = "UPDATE events SET sort_time=$time WHERE id=$id";
                    command.Parameters.AddWithValue("$time", Math.Max(1, row.DetectedAt.UtcTicks));
                    command.Parameters.AddWithValue("$id", row.Id);
                    command.ExecuteNonQuery();
                }
                tx.Commit();
            }
            foreach (var row in Query(db, "SELECT payload FROM events WHERE json_extract(payload,'$.Reason')='active'"))
            {
                Save(row with
                {
                    Reason = "interrupted"
                }, "interrupted");
            }
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    static SqliteConnection Open(string root, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "events.sqlite3"),
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    void Execute(string sql, params (string, object?)[] args)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in args)
        {
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }
    static List<AlarmEvent> Query(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string, object?)[] args)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in args)
        {
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<AlarmEvent>();
        while (reader.Read())
        {
            rows.Add(JsonSerializer.Deserialize<AlarmEvent>(reader.GetString(0), Configuration.Json)!);
        }
        return rows;
    }
    public void Begin(string input, DetectionSettings settings)
    {
        lock (gate)
        {
            SessionId = Guid.NewGuid().ToString("N");
            source = Text.SafeSource(input);
            active.Clear();
            Execute("INSERT INTO sessions VALUES($id,$source,$date,$settings)", ("$id", SessionId), ("$source", source), ("$date", DateTimeOffset.UtcNow.ToString("O")), ("$settings", JsonSerializer.Serialize(settings, Configuration.Json)));
        }
    }
    public void ResumeSession(string id)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT source FROM sessions WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            source = command.ExecuteScalar() as string ?? throw new KeyNotFoundException(id);
            SessionId = id;
            active.Clear();
        }
    }
    AlarmEvent Save(AlarmEvent row, string action)
    {
        string json = JsonSerializer.Serialize(row, Configuration.Json);
        long sequence;
        using (var transaction = db.BeginTransaction())
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO events(id,session,kind,payload,sort_time) VALUES($id,$s,$k,$p,$time) ON CONFLICT(id) DO UPDATE SET payload=$p,sort_time=$time";
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$s", row.SessionId);
            command.Parameters.AddWithValue("$k", (int)row.Kind);
            command.Parameters.AddWithValue("$p", json);
            command.Parameters.AddWithValue("$time", Math.Max(1, row.DetectedAt.UtcTicks));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO event_changes(event_id,session,added) VALUES($id,$s,$a); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$s", row.SessionId);
            command.Parameters.AddWithValue("$a", action == "start" ? 1 : 0);
            sequence = Convert.ToInt64(command.ExecuteScalar());
            transaction.Commit();
        }


        Changed?.Invoke(new(sequence, row, action == "start"));
        File.AppendAllText(Path.Combine(Root, "events.jsonl"), JsonSerializer.Serialize(new
        {
            Action = action,
            Event = row
        }) + Environment.NewLine, Encoding.UTF8);
        return row;
    }
    public AlarmEvent? Record(AlarmTransition transition, string snapshot = "", DateTimeOffset? onset = null, DateTimeOffset? detected = null)
    {
        lock (gate)
        {
            if (transition.Enter)
            {
                var row = new AlarmEvent
                {
                    SessionId = SessionId,
                    Source = source,
                    Kind = transition.Kind,
                    Start = transition.Start,
                    Confirmed = transition.Confirmed,
                    Detail = transition.Detail,
                    Snapshot = snapshot,
                    OnsetAt = onset,
                    DetectedAt = detected ?? DateTimeOffset.UtcNow,
                    Visual = transition.Visual,
                    PeakVisual = transition.PeakVisual,
                    PeakSnapshot = transition.PeakSnapshot
                };
                active[transition.Kind] = row.Id;
                return Save(row, "start");
            }
            if (!active.Remove(transition.Kind, out var id))
            {
                return null;
            }
            var previous = Get(id);
            return previous is null ? null : Save(previous with
            {
                End = transition.End,
                Reason = transition.Reason,
                ClosedAt = detected ?? DateTimeOffset.UtcNow,
                Detail = transition.Visual != null ? transition.Detail : previous.Detail,
                Visual = transition.Visual ?? previous.Visual,
                PeakVisual = transition.PeakVisual ?? previous.PeakVisual,
                PeakSnapshot = string.IsNullOrEmpty(transition.PeakSnapshot) ? previous.PeakSnapshot : transition.PeakSnapshot
            }, "end");
        }
    }
    AlarmEvent? Get(string id) => Query(db, "SELECT payload FROM events WHERE id=$id", null, ("$id", id)).FirstOrDefault();
    public AlarmEvent Review(string id, string status, string note)
    {
        lock (gate)
        {
            var row = Get(id) ?? throw new KeyNotFoundException(id);
            return Save(row with
            {
                Review = status,
                Note = note
            }, "review");
        }
    }
    public List<AlarmEvent> Rows(string? session = null)
    {
        lock (gate)
            return Query(db, "SELECT payload FROM events" + (session is null ? "" : " WHERE session=$s") + " ORDER BY sort_time DESC,id DESC", null, session is null ? [] : [("$s", session)]);
    }
    public static EventPage ReadPage(string root, string? session, string markerSession, int limit = 2000, CancellationToken token = default)
    {
        using var connection = Open(root, true);
        using var transaction = connection.BeginTransaction(deferred: true);
        long Scalar(string sql, params (string, object?)[] args)
        {
            using var c = connection.CreateCommand();
            c.Transaction = transaction;
            c.CommandText = sql;
            foreach (var (key, value) in args)
            {
                c.Parameters.AddWithValue(key, value ?? DBNull.Value);
            }
            return Convert.ToInt64(c.ExecuteScalar());
        }
        long watermark = Scalar("SELECT COALESCE(MAX(seq),0) FROM event_changes");
        string where = session == null ? "" : " WHERE session=$s";
        (string, object?)[] filter = session == null ? [] : [("$s", session)];
        long count = Scalar("SELECT COUNT(*) FROM events" + where, filter);
        token.ThrowIfCancellationRequested();
        var rows = Query(connection, "SELECT payload FROM events" + where + " ORDER BY sort_time DESC,id DESC LIMIT $limit", transaction, [.. filter, ("$limit", Math.Clamp(limit, 1, 10000))]);
        var markers = new List<TimelineEntry>();
        if (markerSession.Length > 0)
        {
            using var c = connection.CreateCommand();
            c.Transaction = transaction;
            c.CommandText = "SELECT id,kind,json_extract(payload,'$.Start') FROM events WHERE session=$s";
            c.Parameters.AddWithValue("$s", markerSession);
            using var reader = c.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                markers.Add(new(reader.GetString(0), (AlarmKind)reader.GetInt32(1), reader.GetDouble(2)));
            }
        }
        transaction.Commit();
        return new(watermark, count, rows.ToArray(), markers.ToArray());
    }
    public static EventChange[] ReadChanges(string root, long after, int limit = 256)
    {
        using var connection = Open(root, true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.seq,e.payload,c.added FROM event_changes c JOIN events e ON e.id=c.event_id WHERE c.seq>$after ORDER BY c.seq LIMIT $limit";
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<EventChange>();
        while (reader.Read())
        {
            result.Add(new(reader.GetInt64(0), JsonSerializer.Deserialize<AlarmEvent>(reader.GetString(1), Configuration.Json)!, reader.GetInt32(2) != 0));
        }
        return result.ToArray();
    }
    public void Export(string path, string? session = null) => ExportReadOnly(Root, path, session);
    public static void ExportReadOnly(string root, string path, string? session = null)
    {
        using var connection = Open(root, true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM events" + (session == null ? "" : " WHERE session=$s") + " ORDER BY sort_time DESC,id DESC";
        if (session != null)
        {
            command.Parameters.AddWithValue("$s", session);
        }
        using var reader = command.ExecuteReader();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("ID,Session,Source,Type,Start,Confirmed,End,StartSeconds,DetectedAt,OnsetAt,ClosedAt,Reason,Detail,Snapshot,Review,Note,PeakSnapshot,Visual,PeakVisual");
        while (reader.Read())
        {
            var e = JsonSerializer.Deserialize<AlarmEvent>(reader.GetString(0), Configuration.Json)!;
            writer.WriteLine(string.Join(",", new[] { e.Id, e.SessionId, e.Source, e.Label, e.StartText, e.ConfirmedText, e.EndText, e.Start.ToString("R", CultureInfo.InvariantCulture), e.DetectedAt.ToString("O"), e.OnsetAt?.ToString("O") ?? "", e.ClosedAt?.ToString("O") ?? "", e.Reason, e.Detail, e.Snapshot, e.Review, e.Note, e.PeakSnapshot, JsonSerializer.Serialize(e.Visual), JsonSerializer.Serialize(e.PeakVisual) }.Select(Text.Csv)));
        }
    }
    public void Dispose()
    {
        lock (gate)
            db.Dispose();
    }
}
