using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SupraChat.Core;

namespace SupraChat.Automation;

/// <summary>
/// Research-only W7 managed .NET transactional SQLite store.
/// It does not replace the W6 ephemeral host or authenticate actors.
/// Only disposable owned synthetic fixture paths may be opened. No provider
/// submissions, credential persistence or terminal execution.
/// </summary>
public sealed class ManagedDurableLedger : IDisposable
{
    public const string StoreSchema = "suprachat-w7-managed-sqlite-experimental/v1";
    private const int MaxSessionCount = 32;
    private const int MaxOpCount = 4096;
    private const int MaxEventCount = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _connection;
    private bool _failed;
    private bool _disposed;

    public ManagedDurableLedger(string databasePath, bool create)
    {
        if (Environment.GetEnvironmentVariable("SUPRACHAT_W7_DISPOSABLE_ONLY") != "yes")
            throw new InvalidOperationException("W7_RESEARCH_ONLY_ADMISSION_REQUIRED");
        if (string.IsNullOrWhiteSpace(databasePath) || !Path.IsPathFullyQualified(databasePath) ||
            !string.Equals(Path.GetExtension(databasePath), ".sqlite3", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("W7_EXPLICIT_OWNED_SQLITE_PATH_REQUIRED");
        var full = Path.GetFullPath(databasePath);
        var parent = Directory.GetParent(full)
            ?? throw new ArgumentException("W7_PARENT_DIRECTORY_REQUIRED");
        if (!parent.Name.StartsWith("suprachat-w7-owned-", StringComparison.Ordinal) ||
            !parent.Exists || parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("W7_UNOWNED_STORAGE_PATH_REFUSED");
        if (File.Exists(full) && new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("W7_SYMLINK_DATABASE_REFUSED");
        if (!create && !File.Exists(full))
            throw new InvalidOperationException("W7_MISSING_STORE_FAIL_CLOSED");
        if (create && File.Exists(full))
            throw new InvalidOperationException("W7_CREATE_CANNOT_REPLACE_EXISTING_STORE");

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            _connection.Open();
            if (create)
            {
                Execute("PRAGMA journal_mode=WAL");
                Execute("PRAGMA synchronous=FULL");
                Execute("PRAGMA busy_timeout=500");
                Execute("""
                    CREATE TABLE metadata (k INTEGER PRIMARY KEY CHECK(k=1), schema TEXT NOT NULL);
                    INSERT INTO metadata(k,schema) VALUES (1,'suprachat-w7-managed-sqlite-experimental/v1');
                    CREATE TABLE sessions (
                        id TEXT PRIMARY KEY, title TEXT NOT NULL,
                        conversation_url TEXT, workspace_key TEXT,
                        version INTEGER NOT NULL CHECK(version >= 0));
                    CREATE TABLE outcomes (
                        operation_key TEXT PRIMARY KEY,
                        fingerprint TEXT NOT NULL,
                        response TEXT NOT NULL);
                    CREATE TABLE events (
                        seq INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        actor_label TEXT NOT NULL,
                        action TEXT NOT NULL, version INTEGER NOT NULL);
                    PRAGMA user_version=7;
                    """);
            }
            Execute("PRAGMA synchronous=FULL");
            Execute("PRAGMA busy_timeout=500");
            if (ScalarLong("PRAGMA user_version") != 7 ||
                ScalarString("PRAGMA quick_check") != "ok" ||
                ScalarString("SELECT schema FROM metadata WHERE k=1") != StoreSchema)
                throw new InvalidOperationException("W7_SCHEMA_OR_INTEGRITY_REFUSED");
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public SessionLedgerResult Open(string key, string actor, string title,
        string? conversationUrl = null, string? workspaceKey = null)
    {
        ValidateKeyActor(key, actor);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 80 || title.Any(char.IsControl))
            throw new ArgumentException("INVALID_TITLE");
        var name = title.Trim();
        string? canonical = null;
        if (!string.IsNullOrWhiteSpace(conversationUrl))
        {
            var parsed = ChatGptControlContract.Inspect(conversationUrl);
            if (!parsed.Recognized || parsed.CanonicalUrl is null)
                throw new ArgumentException("INVALID_PROVIDER_REFERENCE");
            canonical = parsed.CanonicalUrl;
        }
        var workspace = NormalizeWorkspace(workspaceKey);
        var fingerprint = Fingerprint("open", actor, key, name, canonical, workspace);
        return ExecuteMutation(key, fingerprint, actor, "open", (transaction, sequence) =>
        {
            if (ScalarLong("SELECT COUNT(*) FROM sessions", transaction) >= MaxSessionCount)
                return new(SharedSessionLedger.Schema, false, false, "SESSION_CAPACITY_REACHED",
                    null, sequence);
            var id = Guid.NewGuid().ToString("N");
            Write("INSERT INTO sessions(id,title,conversation_url,workspace_key,version) VALUES ($id,$title,$url,$ws,0)",
                transaction, ("$id", id), ("$title", name),
                ("$url", (object?)canonical ?? DBNull.Value), ("$ws", (object?)workspace ?? DBNull.Value));
            var row = new SessionLedgerState(id, name, canonical, workspace, 0,
                "NO_REMOTE_BINDING", false, false);
            return new(SharedSessionLedger.Schema, true, false, "APPLIED", row, sequence + 1);
        });
    }

    public SessionLedgerResult Change(string key, string actor, string sessionId,
        long expectedVersion, string operation, string? value)
    {
        ValidateKeyActor(key, actor);
        if (!Guid.TryParseExact(sessionId, "N", out _) || expectedVersion < 0)
            throw new ArgumentException("INVALID_SESSION_OR_VERSION");
        if (operation is not ("set-title" or "set-conversation" or "set-workspace"))
            throw new ArgumentException("INVALID_MUTATION");
        var normalized = operation switch
        {
            "set-title" => NormalizeTitle(value),
            "set-conversation" => NormalizeConversation(value),
            "set-workspace" => NormalizeWorkspace(value),
            _ => null
        };
        var fingerprint = Fingerprint(operation, actor, key, sessionId,
            expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), normalized);
        return ExecuteMutation(key, fingerprint, actor, operation, (transaction, sequence) =>
        {
            var row = ReadSession(sessionId, transaction);
            if (row is null)
                return new(SharedSessionLedger.Schema,false,false,"SESSION_NOT_FOUND",null,sequence);
            if (row.Version != expectedVersion)
                return new(SharedSessionLedger.Schema,false,false,"VERSION_CONFLICT",row,sequence);

            var next = operation switch
            {
                "set-title" => row with {Title = normalized!, Version = row.Version+1},
                "set-conversation" => row with {ConversationUrl = normalized, Version = row.Version+1},
                "set-workspace" => row with {WorkspaceKey = normalized, Version = row.Version+1},
                _ => throw new InvalidOperationException("INVALID_MUTATION")
            };
            Write("UPDATE sessions SET title=$title,conversation_url=$url,workspace_key=$ws,version=$v WHERE id=$id",
                transaction,("$title",next.Title),("$url",(object?)next.ConversationUrl ?? DBNull.Value),
                ("$ws",(object?)next.WorkspaceKey ?? DBNull.Value),("$v",next.Version),("$id",next.Id));
            return new(SharedSessionLedger.Schema,true,false,"APPLIED",next,sequence+1);
        });
    }

    public SessionLedgerInventory InspectAll()
    {
        EnsureWritableState();
        // Reads cannot observe transactions from another owner until commit.
        var sessions = new List<SessionLedgerState>();
        using (var cmd = Command(
            "SELECT id,title,conversation_url,workspace_key,version FROM sessions ORDER BY id"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                sessions.Add(ReadRow(reader));
        var events = new List<SessionLedgerEvent>();
        using (var cmd = Command(
            "SELECT seq,session_id,actor_label,action,version FROM events ORDER BY seq"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                events.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),
                    reader.GetString(3),reader.GetInt64(4)));
        return new(SharedSessionLedger.Schema,
            events.Count==0?0:events[^1].Sequence,sessions,events,
            DurableAcrossProcessRestart:true,AuthenticatedAudiences:false,
            ChatSubmissionAllowed:false,TerminalExecutionAllowed:false,
            CrossProcessTransportAvailable:false);
    }

    private SessionLedgerResult ExecuteMutation(string key, string fingerprint,
        string actor, string operation, Func<SqliteTransaction,long,SessionLedgerResult> change)
    {
        EnsureWritableState();
        try
        {
            using var tx = _connection.BeginTransaction(deferred:false);
            var existing = ScalarString(
                "SELECT response FROM outcomes WHERE operation_key=$key",
                tx,("$key",key));
            if (existing is not null)
            {
                var originalHash = ScalarString(
                    "SELECT fingerprint FROM outcomes WHERE operation_key=$key",tx,("$key",key));
                if (!string.Equals(originalHash,fingerprint,StringComparison.Ordinal))
                {
                    tx.Commit();
                    return new(SharedSessionLedger.Schema,false,false,
                        "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT",null,LatestSequence());
                }
                var cached = JsonSerializer.Deserialize<SessionLedgerResult>(existing,JsonOptions)
                    ?? throw new InvalidOperationException("W7_CORRUPT_OUTCOME");
                tx.Commit();
                return cached with {IsReplay=true};
            }
            if (ScalarLong("SELECT COUNT(*) FROM outcomes",tx) >= MaxOpCount)
            {
                tx.Commit();
                return new(SharedSessionLedger.Schema,false,false,"IDEMPOTENCY_CAPACITY_REACHED",
                    null,LatestSequence());
            }
            var sequence=ScalarLong("SELECT COALESCE(MAX(seq),0) FROM events",tx);
            if (sequence >= MaxEventCount)
            {
                tx.Commit();
                return new(SharedSessionLedger.Schema,false,false,"EVENT_CAPACITY_REACHED",
                    null,sequence);
            }
            if (Environment.GetEnvironmentVariable("SUPRACHAT_W7_FAULT_POINT")=="before_write")
                Environment.Exit(82);
            var result=change(tx,sequence);
            if (result.Applied)
            {
                Write("INSERT INTO events(session_id,actor_label,action,version) VALUES($id,$actor,$action,$version)",
                    tx,("$id",result.Session!.Id),("$actor",actor),("$action",operation),
                    ("$version",result.Session.Version));
            }
            Write("INSERT INTO outcomes(operation_key,fingerprint,response) VALUES($key,$hash,$reply)",
                tx,("$key",key),("$hash",fingerprint),
                ("$reply",JsonSerializer.Serialize(result,JsonOptions)));
            if (Environment.GetEnvironmentVariable("SUPRACHAT_W7_FAULT_POINT")=="after_write_before_commit")
                Environment.Exit(83);
            tx.Commit();
            if (Environment.GetEnvironmentVariable("SUPRACHAT_W7_FAULT_POINT")=="after_commit_before_ack")
                Environment.Exit(84);
            return result;
        }
        catch (Exception)
        {
            _failed = true; // never silently restart from an empty store after I/O trouble
            throw;
        }
    }

    private SessionLedgerState? ReadSession(string id, SqliteTransaction tx)
    {
        using var cmd=Command(
            "SELECT id,title,conversation_url,workspace_key,version FROM sessions WHERE id=$id",
            tx,("$id",id));
        using var reader=cmd.ExecuteReader();
        return reader.Read()?ReadRow(reader):null;
    }
    private static SessionLedgerState ReadRow(SqliteDataReader reader) =>
        new(reader.GetString(0),reader.GetString(1),
            reader.IsDBNull(2)?null:reader.GetString(2),
            reader.IsDBNull(3)?null:reader.GetString(3),
            reader.GetInt64(4),"NO_REMOTE_BINDING",false,false);

    private long LatestSequence() =>
        ScalarLong("SELECT COALESCE(MAX(seq),0) FROM events");
    private SqliteCommand Command(string sql, SqliteTransaction? tx=null,
        params (string Name,object Value)[] args)
    {
        var cmd=_connection.CreateCommand();
        cmd.CommandText=sql;
        cmd.Transaction=tx;
        foreach (var (name,value) in args)cmd.Parameters.AddWithValue(name,value);
        return cmd;
    }
    private void Execute(string sql)
    {
        using var cmd=Command(sql);cmd.ExecuteNonQuery();
    }
    private long ScalarLong(string sql, SqliteTransaction? tx=null,
        params (string Name,object Value)[] args)
    {
        using var cmd=Command(sql,tx,args);return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private string? ScalarString(string sql, SqliteTransaction? tx=null,
        params (string Name,object Value)[] args)
    {
        using var cmd=Command(sql,tx,args);return cmd.ExecuteScalar() as string;
    }
    private void EnsureWritableState()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if (_failed)throw new InvalidOperationException("W7_FAILED_CLOSED_RESTART_REQUIRED");
    }
    private static void ValidateKeyActor(string key,string actor)
    {
        if (string.IsNullOrWhiteSpace(key)||key.Length is < 12 or > 128 ||
            key.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or ':' or '.')))
            throw new ArgumentException("INVALID_IDEMPOTENCY_KEY");
        if (actor is not ("human" or "automation" or "agent"))
            throw new ArgumentException("INVALID_UNTRUSTED_AUDIENCE_LABEL");
    }
    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)||value.Length>80||value.Any(char.IsControl))
            throw new ArgumentException("INVALID_TITLE");
        return value.Trim();
    }
    private static string? NormalizeConversation(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        var parsed=ChatGptControlContract.Inspect(value);
        if(!parsed.Recognized||parsed.CanonicalUrl is null)
            throw new ArgumentException("INVALID_CONVERSATION_URL");
        return parsed.CanonicalUrl;
    }
    private static string? NormalizeWorkspace(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        var normalized=value.Trim().Replace('\\','/').TrimEnd('/');
        if(normalized.Length is 0 or >512||normalized.Any(char.IsControl))
            throw new ArgumentException("INVALID_WORKSPACE_REFERENCE");
        return normalized;
    }
    private static string Fingerprint(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(parts,JsonOptions))));
    public void Dispose(){if(_disposed)return;_disposed=true;_connection.Dispose();}
}
