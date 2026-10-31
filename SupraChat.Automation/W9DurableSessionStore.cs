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
        if (!Path.IsPathFullyQualified(absolutePath) ||
            !Directory.Exists(Path.GetDirectoryName(absolutePath)))
            throw new ArgumentException("EXPLICIT_EXISTING_ABSOLUTE_STORE_DIRECTORY_REQUIRED");
        if (File.Exists(absolutePath) &&
            (File.GetAttributes(absolutePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("STORE_REPARSE_POINT_REFUSED");
        // Kept open throughout the host lifetime. A second W6 process must
        // fail closed rather than create a competing session authority.
        _ownerLock = new FileStream(absolutePath + ".w9owner", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            _db.Open();
            if (Scalar("PRAGMA journal_mode=WAL") != "wal")
                throw new InvalidDataException("STORE_WAL_REQUIRED");
            Exec("PRAGMA synchronous=FULL");
            Exec("PRAGMA foreign_keys=ON");
            if (Scalar("PRAGMA integrity_check") != "ok")
                throw new InvalidDataException("STORE_INTEGRITY_FAILED");

            using (var tx = _db.BeginTransaction(deferred: false))
            {
                Exec(@"CREATE TABLE IF NOT EXISTS checkpoint (
                    slot INTEGER PRIMARY KEY CHECK(slot=1),
                    schema_id TEXT NOT NULL, revision INTEGER NOT NULL,
                    sha256 TEXT NOT NULL, json TEXT NOT NULL)", tx);
                tx.Commit();
            }
            using var cmd = Command("SELECT schema_id FROM checkpoint WHERE slot=1");
            var schema = cmd.ExecuteScalar();
            if (schema is not null && schema is not DBNull &&
                (string)schema != StoreSchema)
                throw new InvalidDataException("UNSUPPORTED_DURABLE_SCHEMA");

            // Only disposable public RDTE hosts may inject intentional
            // crash points, and never when a real provider is admitted.
            if (Environment.GetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS") == "1")
                _testFault = Environment.GetEnvironmentVariable("SUPRACHAT_W9_FAULT");
        }
        catch
        {
            _db.Dispose();
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
