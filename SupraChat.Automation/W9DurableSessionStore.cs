using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SupraChat.Core;

namespace SupraChat.Automation;

/// <summary>
/// Experimental W9 single-writer SQLite snapshot for THE W6 in-memory ledger.
/// Stores complete session+event+first-outcome checkpoint atomically before
/// HTTP admission acknowledges any mutable metadata operation.
/// Deliberately not a production user-data store or a remote DLE.
/// </summary>
public sealed class W9DurableSessionStore : IDisposable
{
    private const string StoreSchema = "suprachat-w9-w6-checkpoint/v1";
    private readonly FileStream _ownerLock;
    private readonly SqliteConnection _db;
    private readonly string? _testFault;

    public W9DurableSessionStore(string absolutePath)
    {
        if (Environment.GetEnvironmentVariable("SUPRACHAT_W9_DISPOSABLE_ONLY") != "yes")
            throw new InvalidOperationException("W9_SYNTHETIC_OWNED_STORE_ADMISSION_REQUIRED");
        if (!Path.IsPathFullyQualified(absolutePath) ||
            !string.Equals(Path.GetExtension(absolutePath), ".sqlite",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("W9_EXPLICIT_OWNED_ABSOLUTE_SQLITE_PATH_REQUIRED");
        var full = Path.GetFullPath(absolutePath);
        var parent = Directory.GetParent(full)
            ?? throw new InvalidDataException("W9_PARENT_DIRECTORY_REQUIRED");
        if (!parent.Exists || !parent.Name.StartsWith("suprachat-w9-owned-",
                StringComparison.Ordinal) ||
            parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("W9_UNOWNED_DIRECTORY_REFUSED");
        // macOS/Linux create 0700 mkdtemp directories; reject group/other
        // accessible parents rather than depending only on a path prefix.
        if (!OperatingSystem.IsWindows())
        {
            var permission = File.GetUnixFileMode(parent.FullName);
            if ((permission & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                               UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                               UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidDataException("W9_DIRECTORY_NOT_OWNER_PRIVATE");
        }
        var create = Environment.GetEnvironmentVariable("SUPRACHAT_W9_CREATE_ONCE") == "yes";
        var exists = File.Exists(full);
        if (exists && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("W9_SYMLINK_STORE_REFUSED");
        if (!create && !exists)
            throw new InvalidDataException("W9_MISSING_STORE_REFUSED_NO_IMPLICIT_RESET");
        if (create && exists)
            throw new InvalidDataException("W9_CREATE_MUST_NOT_OVERWRITE_EXISTING");
        var lockPath = full + ".w9owner";
        if (File.Exists(lockPath) &&
            File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("W9_OWNER_LOCK_REPARSE_POINT_REFUSED");
        _ownerLock = new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (create)
            {
                // CreateNew is an atomic file-exists guard; a host restart
                // cannot manufacture an empty replacement for a missing DB.
                using var initialFile = new FileStream(full, FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None);
            }
            _db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            try
            {
                _db.Open();
                if (Scalar("PRAGMA journal_mode=WAL") != "wal")
                    throw new InvalidDataException("W9_STORE_WAL_REQUIRED");
                Exec("PRAGMA synchronous=FULL");
                Exec("PRAGMA foreign_keys=ON");
                if (Scalar("PRAGMA integrity_check") != "ok")
                    throw new InvalidDataException("W9_STORE_INTEGRITY_FAILED");
                if (create)
                {
                    using var tx = _db.BeginTransaction(deferred: false);
                    Exec(@"CREATE TABLE checkpoint (
                        slot INTEGER PRIMARY KEY CHECK(slot=1),
                        schema_id TEXT NOT NULL, revision INTEGER NOT NULL,
                        sha256 TEXT NOT NULL, json TEXT NOT NULL)", tx);
                    var genesis = new SessionLedgerCheckpoint(
                        SharedSessionLedger.Schema, 0,
                        Array.Empty<SessionLedgerState>(),
                        Array.Empty<SessionLedgerCheckpointOperation>(),
                        Array.Empty<SessionLedgerEvent>());
                    var content = JsonSerializer.Serialize(genesis);
                    using var init = Command(@"INSERT INTO checkpoint
                        (slot,schema_id,revision,sha256,json)
                        VALUES(1,$schema,1,$sha,$json)", tx);
                    init.Parameters.AddWithValue("$schema", StoreSchema);
                    init.Parameters.AddWithValue("$sha", Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(content))));
                    init.Parameters.AddWithValue("$json", content);
                    if (init.ExecuteNonQuery() != 1)
                        throw new InvalidDataException("W9_GENESIS_NOT_COMMITTED");
                    tx.Commit();
                }
                // Existing SQLite files without a prior committed genesis or
                // session checkpoint are invalid, NEVER treated as new empty.
                using var cmd = Command("SELECT schema_id FROM checkpoint WHERE slot=1");
                var schema = cmd.ExecuteScalar();
                if (schema is not string id || id != StoreSchema)
                    throw new InvalidDataException("W9_MISSING_OR_UNSUPPORTED_CHECKPOINT");
                // Only disposable, local synthetic RDTE hosts may deliberately
                // inject crashes; external actions cannot be admitted here.
                if (Environment.GetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS") == "1")
                    _testFault = Environment.GetEnvironmentVariable("SUPRACHAT_W9_FAULT");
            }
            catch
            {
                _db.Dispose();
                throw;
            }
        }
        catch
        {
            _ownerLock.Dispose();
            throw;
        }
    }

    public SessionLedgerCheckpoint? Load()
    {
        using var cmd = Command(
            "SELECT schema_id, revision, sha256, json FROM checkpoint WHERE slot=1");
        using var record = cmd.ExecuteReader();
        if (!record.Read()) return null;
        if (record.GetString(0) != StoreSchema || record.GetInt64(1) < 1)
            throw new InvalidDataException("INVALID_SNAPSHOT_METADATA");
        var sha = record.GetString(2);
        var content = record.GetString(3);
        var computed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        if (!CryptographicOperations.FixedTimeEquals(
              Encoding.ASCII.GetBytes(sha), Encoding.ASCII.GetBytes(computed)))
            throw new InvalidDataException("SNAPSHOT_DIGEST_MISMATCH");
        var parsed = JsonSerializer.Deserialize<SessionLedgerCheckpoint>(content);
        if (parsed is null) throw new InvalidDataException("EMPTY_LEDGER_CHECKPOINT");
        return parsed;
    }

    /// <summary>
    /// Called ONLY while SharedSessionLedger's own _gate is held. On any
    /// storage uncertainty the entire host terminates: in-memory state may
    /// have advanced but can never become visible in a subsequent read.
    /// </summary>
    public void Save(SessionLedgerCheckpoint checkpoint)
    {
        try
        {
            var content = JsonSerializer.Serialize(checkpoint);
            if (Encoding.UTF8.GetByteCount(content) > 2_000_000)
                throw new InvalidDataException("CHECKPOINT_CAPACITY_REACHED");
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            using (var tx = _db.BeginTransaction(deferred: false))
            {
                using var put = Command(@"INSERT INTO checkpoint(slot,schema_id,revision,sha256,json)
                  VALUES(1,$schema,1,$sha,$json)
                  ON CONFLICT(slot) DO UPDATE SET
                  revision=checkpoint.revision+1, sha256=excluded.sha256,
                  json=excluded.json", tx);
                put.Parameters.AddWithValue("$schema", StoreSchema);
                put.Parameters.AddWithValue("$sha", digest);
                put.Parameters.AddWithValue("$json", content);
                if (put.ExecuteNonQuery() != 1)
                    throw new InvalidDataException("CHECKPOINT_WRITE_FAILED");
                if (_testFault == "BEFORE_COMMIT")
                    Environment.Exit(88);
                tx.Commit();
            }
            if (_testFault == "AFTER_COMMIT_BEFORE_ACK")
                Environment.Exit(89);
        }
        catch
        {
            // Never let HTTP translate failed durable admission to APPLIED or
            // continue serving reads from uncommitted advanced state.
            Environment.Exit(78);
            throw;
        }
    }

    private string Scalar(string sql) =>
        Convert.ToString(Command(sql).ExecuteScalar()) ?? "";
    private void Exec(string sql, SqliteTransaction? tx=null)
    {
        using var command = Command(sql,tx);
        command.ExecuteNonQuery();
    }
    private SqliteCommand Command(string sql, SqliteTransaction? tx=null)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }
    public void Dispose()
    {
        _db.Dispose();
        _ownerLock.Dispose();
    }
}
