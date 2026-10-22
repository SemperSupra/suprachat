using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SupraChat.DurableRdte;

/// <summary>
/// Public synthetic metadata transactional durability experiment ONLY.
/// Not wired to W6 production host or any ChatGPT/terminal actuator.
/// </summary>
public sealed class DurableLedger : IDisposable
{
    public const string Schema = "suprachat-w7-transactional-ledger-rdte/v1";
    private const int MaxSessions = 32;
    private const int MaxOperations = 4096;
    private readonly SqliteConnection _db;

    public DurableLedger(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("ABSOLUTE_STORE_PATH_REQUIRED");
        if (File.Exists(absolutePath) && (File.GetAttributes(absolutePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("SYMLINK_STORE_REFUSED");
        _db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = absolutePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5
        }.ToString());
        _db.Open();
        Sql("PRAGMA busy_timeout=5000");
        if (!string.Equals(Scalar("PRAGMA journal_mode=WAL"), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLITE_WAL_UNAVAILABLE");
        Sql("PRAGMA synchronous=FULL");
        Sql("PRAGMA foreign_keys=ON");
        if (Scalar("PRAGMA quick_check") != "ok")
            throw new InvalidDataException("STORE_CORRUPT_QUICK_CHECK");
        // SQLite transactional DDL creates an all-or-nothing schema.
        using (var tx = _db.BeginTransaction(deferred: false))
        {
            Sql(@"CREATE TABLE IF NOT EXISTS ledger_meta(
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                schema_version INTEGER NOT NULL, next_sequence INTEGER NOT NULL);", tx);
            Sql(@"CREATE TABLE IF NOT EXISTS sessions(
                id TEXT PRIMARY KEY, title TEXT NOT NULL,
                version INTEGER NOT NULL, created_sequence INTEGER NOT NULL);", tx);
            Sql(@"CREATE TABLE IF NOT EXISTS outcomes(
                idempotency_key TEXT PRIMARY KEY, digest TEXT NOT NULL,
                response_json TEXT NOT NULL, first_sequence INTEGER NOT NULL);", tx);
            Sql(@"CREATE TABLE IF NOT EXISTS events(
                sequence INTEGER PRIMARY KEY, session_id TEXT,
                actor_label TEXT NOT NULL, kind TEXT NOT NULL,
                status TEXT NOT NULL, session_version INTEGER,
                FOREIGN KEY(session_id) REFERENCES sessions(id));", tx);
            using var init = Command("INSERT OR IGNORE INTO ledger_meta(singleton,schema_version,next_sequence) VALUES(1,1,0)", tx);
            init.ExecuteNonQuery();
            using var version = Command("SELECT schema_version FROM ledger_meta WHERE singleton=1",tx);
            if (Convert.ToInt64(version.ExecuteScalar()) != 1)
                throw new InvalidDataException("STORE_SCHEMA_VERSION_UNSUPPORTED");
            tx.Commit();
        }
        if (Scalar("PRAGMA integrity_check") != "ok")
            throw new InvalidDataException("STORE_CORRUPT_INTEGRITY_CHECK");
    }

    public DurableResult Execute(DurableRequest req)
    {
        if (req.Action is "list")
        {
            using var cmd = Command("SELECT id,title,version,created_sequence FROM sessions ORDER BY id");
            using var reader = cmd.ExecuteReader();
            var items = new List<DurableSession>();
            while (reader.Read()) items.Add(new DurableSession(reader.GetString(0),
                reader.GetString(1),reader.GetInt64(2),reader.GetInt64(3)));
            return new(Schema,true,false,"READ",0,null,items.ToArray());
        }
        if (req.Action is "inspect")
        {
            var inspectedId = Require(req.SessionId,"INVALID_SESSION_ID",64);
            using var cmd = Command("SELECT id,title,version,created_sequence FROM sessions WHERE id=$id");
            cmd.Parameters.AddWithValue("$id",inspectedId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return new(Schema,false,false,"SESSION_NOT_FOUND",0);
            return new(Schema,true,false,"READ",0,new DurableSession(
                reader.GetString(0),reader.GetString(1),reader.GetInt64(2),reader.GetInt64(3)));
        }
        if (req.Action is not ("open" or "change"))
            return new(Schema,false,false,"UNKNOWN_ACTION",0);
        var key = Require(req.OperationId,"INVALID_OPERATION_KEY",128);
        if (key.Length < 12 || key.Any(c=> !(char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or ':')))
            throw new ArgumentException("INVALID_OPERATION_KEY");
        var actor = Require(req.Audience,"INVALID_AUDIENCE",10);
        if (actor is not ("human" or "automation" or "agent"))
            throw new ArgumentException("INVALID_AUDIENCE");
        var title = Require(req.Title,"INVALID_TITLE",80);
        var id = req.Action=="change" ? Require(req.SessionId,"INVALID_SESSION_ID",64) : null;
        var version = req.ExpectedVersion ?? 0;
        if (version < 0) throw new ArgumentException("INVALID_EXPECTED_VERSION");

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new {req.Action,actor,title,id,version}))));

        using var tx = _db.BeginTransaction(deferred:false);
        using (var find = Command("SELECT digest,response_json FROM outcomes WHERE idempotency_key=$k",tx))
        {
            find.Parameters.AddWithValue("$k",key);
            using var old = find.ExecuteReader();
            if (old.Read())
            {
                var digest = old.GetString(0);
                var receipt = old.GetString(1);
                return digest == fingerprint
                    ? JsonSerializer.Deserialize<DurableResult>(receipt)! with {IsReplay=true}
                    : new(Schema,false,false,"IDEMPOTENCY_KEY_PAYLOAD_CONFLICT",0);
            }
        }
        var outcomes = Convert.ToInt64(Command("SELECT COUNT(*) FROM outcomes",tx).ExecuteScalar());
        if (outcomes >= MaxOperations)
            return new(Schema,false,false,"OUTCOME_CAPACITY_REACHED",0);
        var sequence = Convert.ToInt64(Command("SELECT next_sequence FROM ledger_meta WHERE singleton=1",tx).ExecuteScalar()) + 1;
        DurableSession? updated=null;
        string status;
        if (req.Action=="open")
        {
            var count = Convert.ToInt64(Command("SELECT COUNT(*) FROM sessions",tx).ExecuteScalar());
            if(count>=MaxSessions) status="SESSION_CAPACITY_REACHED";
            else
            {
                var sid=Guid.NewGuid().ToString("N");
                using var create=Command("INSERT INTO sessions(id,title,version,created_sequence) VALUES($id,$title,0,$seq)",tx);
                create.Parameters.AddWithValue("$id",sid);
                create.Parameters.AddWithValue("$title",title);
                create.Parameters.AddWithValue("$seq",sequence);
                create.ExecuteNonQuery();
                updated = new(sid,title,0,sequence);
                status="APPLIED";
            }
        }
        else
        {
            using var check=Command("SELECT title,version,created_sequence FROM sessions WHERE id=$id",tx);
            check.Parameters.AddWithValue("$id",id!);
            using(var row=check.ExecuteReader())
            {
                if(!row.Read()) status="SESSION_NOT_FOUND";
                else
                {
                    var previousTitle=row.GetString(0);
                    var actualVersion=row.GetInt64(1);
                    var creation=row.GetInt64(2);
                    if(actualVersion!=version)
                    {
                        status="VERSION_CONFLICT";
                        updated=new(id!,previousTitle,actualVersion,creation);
                    }
                    else
                    {
                        updated=new(id!,title,actualVersion+1,creation);
                        status="APPLIED";
                    }
                }
            }
            if(status=="APPLIED")
            {
                using var update=Command("UPDATE sessions SET title=$title,version=$version WHERE id=$id AND version=$old",tx);
                update.Parameters.AddWithValue("$title",title);
                update.Parameters.AddWithValue("$version",updated!.Version);
                update.Parameters.AddWithValue("$id",id!);
                update.Parameters.AddWithValue("$old",version);
                if(update.ExecuteNonQuery()!=1) throw new InvalidDataException("CAS_UNEXPECTED");
            }
        }
        var result=new DurableResult(Schema,status=="APPLIED",false,status,sequence,updated);
        using(var eventCommand=Command(@"INSERT INTO events(sequence,session_id,actor_label,kind,status,session_version)
          VALUES($seq,$id,$actor,$kind,$status,$version)",tx))
        {
            eventCommand.Parameters.AddWithValue("$seq",sequence);
            eventCommand.Parameters.AddWithValue("$id",(object?)updated?.Id ?? DBNull.Value);
            eventCommand.Parameters.AddWithValue("$actor",actor);
            eventCommand.Parameters.AddWithValue("$kind",req.Action);
            eventCommand.Parameters.AddWithValue("$status",status);
            eventCommand.Parameters.AddWithValue("$version",(object?)updated?.Version ?? DBNull.Value);
            eventCommand.ExecuteNonQuery();
        }
        using(var op=Command("INSERT INTO outcomes(idempotency_key,digest,response_json,first_sequence) VALUES($k,$d,$r,$s)",tx))
        {
            op.Parameters.AddWithValue("$k",key);
            op.Parameters.AddWithValue("$d",fingerprint);
            op.Parameters.AddWithValue("$r",JsonSerializer.Serialize(result));
            op.Parameters.AddWithValue("$s",sequence);
            op.ExecuteNonQuery();
        }
        using(var advance=Command("UPDATE ledger_meta SET next_sequence=$seq WHERE singleton=1",tx))
        {
            advance.Parameters.AddWithValue("$seq",sequence);
            if(advance.ExecuteNonQuery()!=1)throw new InvalidDataException("SEQUENCE_UPDATE_FAILED");
        }
        if(req.Fault=="BEFORE_COMMIT")
            Environment.Exit(88); // deliberate crash: SQLite must roll back every staged row.
        tx.Commit();
        if(req.Fault=="AFTER_COMMIT_BEFORE_REPLY")
            Environment.Exit(89); // deliberate lost ACK: durable first receipt must replay.
        return result;
    }

    public object InspectIntegrity()
    {
        if(Scalar("PRAGMA integrity_check")!="ok") throw new InvalidDataException("STORE_CORRUPT");
        var seq=Convert.ToInt64(Command("SELECT next_sequence FROM ledger_meta WHERE singleton=1").ExecuteScalar());
        var count=Convert.ToInt64(Command("SELECT COUNT(*) FROM outcomes").ExecuteScalar());
        var events=Convert.ToInt64(Command("SELECT COUNT(*) FROM events").ExecuteScalar());
        var sessions=Convert.ToInt64(Command("SELECT COUNT(*) FROM sessions").ExecuteScalar());
        if(seq!=count || seq!=events) throw new InvalidDataException("SEQUENCE_RECEIPT_DIVERGENCE");
        return new {schema=Schema, integrity="PASS", sequence=seq,
            outcomes=count,events,sessions, provider_contacted=false,
            authenticated_audience_identity=false, integrated_with_w6=false};
    }

    private string Scalar(string sql) => Convert.ToString(Command(sql).ExecuteScalar()) ?? "";
    private void Sql(string sql, SqliteTransaction? tx=null) {using var cmd=Command(sql,tx);cmd.ExecuteNonQuery();}
    private SqliteCommand Command(string sql, SqliteTransaction? tx=null)
    {
        var cmd=_db.CreateCommand();
        cmd.CommandText=sql;
        if(tx is not null)cmd.Transaction=tx;
        return cmd;
    }
    private static string Require(string? s,string error,int max)
    {
        if(string.IsNullOrWhiteSpace(s)||s.Length>max||s.Any(char.IsControl))
            throw new ArgumentException(error);
        return s.Trim();
    }
    public void Dispose()=>_db.Dispose();
}
public sealed record DurableRequest(string Action,string? OperationId=null,string? Audience=null,
  string? Title=null,string? SessionId=null,long? ExpectedVersion=null,string? Fault=null);
public sealed record DurableSession(string Id,string Title,long Version,long CreatedSequence);
public sealed record DurableResult(string Schema,bool Applied,bool IsReplay,string Status,
    long Sequence,DurableSession? Session=null,IReadOnlyList<DurableSession>? Sessions=null);
