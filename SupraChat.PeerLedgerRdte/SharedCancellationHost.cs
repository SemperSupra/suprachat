using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupraChat.Automation;
using SupraChat.Core;

namespace SupraChat.PeerLedgerRdte;

/// <summary>Disposable synthetic composition only. No provider targets, human identity or production issuer.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SharedCancellationHost : IDisposable
{
    internal sealed record Request(string SessionId, Guid TargetId, string Key, long Version = 0,
        long TargetRevision = 0, Guid PlanId = default, bool Confirmed = false);
    internal sealed record Envelope(string? Operation, string? Method, string? Jsonrpc, int? Id, Request Params);
    internal sealed record Reply(string Status, Guid PlanId, long ReceiptVersion, long FinalSequence,
        string[] Stages, bool IsReplay, bool LocalSignalVerified);
    private sealed class Actor(string id, string audience, string session, string grant,
        LocalOwnedStreamControls owner, LocalOwnedStreamControls.OwnedStreamLease lease)
    {
        internal readonly string Id = id, Audience = audience, Session = session, Grant = grant;
        internal readonly LocalOwnedStreamControls Owner = owner;
        internal readonly LocalOwnedStreamControls.OwnedStreamLease Lease = lease;
        internal DurableCancellationPlan? Plan;
        internal Reply? Applied;
    }
    private readonly WindowsOwnerCustody _custody = WindowsOwnerCustody.Create();
    private readonly W12ScopedGrantAuthority _issuer;
    private W9DurableSessionStore _store;
    private SharedSessionLedger _ledger;
    private readonly Actor[] _actors;
    private readonly Dictionary<Guid, (string Session, LocalOwnedStreamControls.OwnedStreamLease Lease)> _syntheticTargets = new();
    private readonly List<(LocalOwnedStreamControls Owner, LocalOwnedStreamControls.OwnedStreamLease Lease)> _replacements = new();
    private readonly CancellationTokenSource _closing = new(TimeSpan.FromSeconds(90));
    private readonly ConcurrentBag<Task> _workers = new();
    private bool _metadataDenied, _crossSessionDenied, _forgedFieldsDenied, _replaysStable, _revocationDenied;
    private int _clientRuns, _peerRuns;
    internal bool ClientsComplete { get; private set; }
    internal bool GuiApplied => _actors[0].Lease.Token.IsCancellationRequested;
    private static readonly JsonSerializerOptions Wire = new(TraceNormalizer.JsonOptions)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal SharedCancellationHost()
    {
        using var material = _custody.InitializeIssuer(); _issuer = material.CreateAuthority();
        _store = OpenStore(true); _ledger = new(_store.Load(), _store.Save);
        _actors = Enumerable.Range(0, 3).Select(index =>
        {
            var audience = new[] { "human", "automation", "agent" }[index];
            var id = "synthetic-" + audience;
            var session = _ledger.Open("cancel-lab-open-" + index, "automation", "Synthetic " + audience).Session!.Id;
            var owner = new LocalOwnedStreamControls(Guid.ParseExact(session, "N"));
            var lease = owner.Register(TraceSurface.Synthetic);
            _ledger.RegisterCancellationTarget(session, owner, lease);
            _syntheticTargets.Add(lease.StreamId, (session, lease));
            return new Actor(id, audience, session, _issuer.Issue(id, audience, session,
                ["session.read", "stream.cancel.synthetic"], TimeSpan.FromMinutes(2)), owner, lease);
        }).ToArray();
    }
    private W9DurableSessionStore OpenStore(bool create)
    {
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_DISPOSABLE_ONLY", "yes");
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_CREATE_ONCE", create ? "yes" : null);
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS", null);
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_FAULT", null);
        Environment.SetEnvironmentVariable("SUPRACHAT_W14_WINDOWS_CUSTODY", "synthetic");
        return CustodyNative.WithUserFileOwner(() => new W9DurableSessionStore(Path.Combine(_custody.Root, "fixture.sqlite")));
    }
    private static Reply Refused(string reason) => new(reason, Guid.Empty, -1, 0, Array.Empty<string>(), false, false);
    private Reply Dispatch(Actor actor, string method, Request request, Func<bool> observePeer)
    {
        CancellationAdmission Admit()
        {
            if (!observePeer() || !_syntheticTargets.TryGetValue(request.TargetId, out var target) ||
                target.Session != request.SessionId || target.Lease.Surface != TraceSurface.Synthetic)
                return new(new(false, null, null, null, null), false);
            var grant = _issuer.Validate(actor.Grant, request.SessionId, "stream.cancel.synthetic", actor.Id, actor.Audience);
            return new(new(grant.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId), grant.Allowed);
        }
        var result = method switch
        {
            "control/preview" => _ledger.PreviewCancellation(request.SessionId, request.TargetId, request.Version, request.TargetRevision, request.Key, Admit),
            "control/confirm" => _ledger.ConfirmCancellation(request.SessionId, request.PlanId, request.TargetId,
                request.Version, request.TargetRevision, request.Key, request.Confirmed, Admit),
            _ => null
        };
        if (result is null) return Refused("REQUEST_REFUSED");
        if (result.Entry is not { } entry) return Refused(result.Status);
        if (method == "control/preview" && entry.Confirmed is null) actor.Plan = entry.Plan;
        var reply = new Reply(result.Status, entry.Plan.PlanId,
            entry.Plan.ExpectedSessionVersion + (entry.Stages.Any(s => s.Stage == "REQUESTED") ? 1 : 0),
            entry.Stages.LastOrDefault()?.Sequence ?? 0, entry.Stages.Select(s => s.Stage).ToArray(), result.IsReplay,
            entry.Stages.LastOrDefault()?.LocalSignalVerified == true);
        if (result.Status == "VERIFIED_LOCAL_SIGNAL" && !result.IsReplay) actor.Applied = reply;
        return reply;
    }
    internal Reply PreviewGui()
    {
        var actor = _actors[0];
        return Dispatch(actor, "control/preview", new(actor.Session, actor.Lease.StreamId, "gui-cancel-" + Guid.NewGuid().ToString("N")), () => true);
    }
    internal Reply ConfirmGui()
    {
        var actor = _actors[0]; var plan = actor.Plan ?? throw new InvalidOperationException("GUI_REVIEW_REQUIRED");
        return Dispatch(actor, "control/confirm", new(actor.Session, actor.Lease.StreamId, plan.ActionKey, PlanId: plan.PlanId, Confirmed: true), () => true);
    }
    internal Reply[] ReadProjection() => _actors.Select(actor => actor.Applied ?? Refused("NOT_APPLIED")).ToArray();
    internal async Task RunClientsAsync()
    {
        if (!GuiApplied || Interlocked.Exchange(ref _clientRuns, 1) != 0) throw new InvalidOperationException("CLIENTS_NOT_READY");
        var target = _actors[1];
        var metadata = new Actor("synthetic-metadata-only", "automation", target.Session,
            _issuer.Issue("synthetic-metadata-only", "automation", target.Session, ["session.read", "session.mutate"], TimeSpan.FromMinutes(2)), target.Owner, target.Lease);
        await StartPeer(metadata, "metadata");
        _metadataDenied = !target.Lease.Token.IsCancellationRequested;
        await Task.WhenAll(StartPeer(_actors[1], "normal"), StartPeer(_actors[2], "normal"));
        var agent = _actors[2];
        var admitted = _issuer.Validate(agent.Grant, agent.Session, "session.read", agent.Id, agent.Audience);
        if (!admitted.Allowed) throw new UnauthorizedAccessException("AGENT_NOT_ADMITTED");
        _ledger.RevokeOwnGrant(admitted.GrantId!, agent.Session, agent.Audience);
        await StartPeer(agent, "revoked");
        ClientsComplete = true;
    }
    private Task StartPeer(Actor actor, string mode)
    {
        _closing.Token.ThrowIfCancellationRequested();
        var task = Task.Run(() => ServeAsync(actor, mode)); _workers.Add(task); return task;
    }
    private async Task ServeAsync(Actor actor, string mode)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var name = "suprachat-control-lab-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        using var child = Program.Spawn("--cancellation-client", name, actor.Audience, mode);
        try
        {
            var pid = child.Id; var started = child.StartTime.ToUniversalTime();
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!(await Program.ReadFrameAsync(pipe, timeout.Token)).AsSpan().SequenceEqual("suprachat-cancellation-lab/v1"u8) ||
                !Program.ObservePeer(pipe, pid, started)) throw new UnauthorizedAccessException("PEER_REFUSED");
            var other = _actors.First(a => a.Session != actor.Session);
            await Program.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new
            {
                session_id = actor.Session, target_id = actor.Lease.StreamId, other_session = other.Session, other_target = other.Lease.StreamId,
                plan_id = actor.Plan?.PlanId ?? Guid.Empty, key = actor.Plan?.ActionKey ?? actor.Audience + "-shared-cancel-key"
            }, Wire), timeout.Token);
            var count = mode == "normal" ? 5 : 1;
            Reply? applied = null;
            for (var step = 0; step < count; step++)
            {
                var bytes = await Program.ReadFrameAsync(pipe, timeout.Token);
                if (!Program.ObservePeer(pipe, pid, started)) throw new UnauthorizedAccessException("PEER_CHANGED");
                Reply reply; var malformed = false;
                try
                {
                    var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Wire) ?? throw new JsonException();
                    var method = actor.Audience == "agent" ? envelope.Method : envelope.Operation;
                    if (actor.Audience == "agent" ? envelope.Jsonrpc != "2.0" || envelope.Operation is not null || envelope.Id != step + 1 :
                        envelope.Jsonrpc is not null || envelope.Method is not null || envelope.Id is not null) throw new JsonException();
                    var request = envelope.Params;
                    if (method is not ("control/preview" or "control/confirm") || request is null ||
                        !Guid.TryParseExact(request.SessionId, "N", out _) || request.TargetId == Guid.Empty ||
                        string.IsNullOrEmpty(request.Key) || request.Key.Length is < 12 or > 128 ||
                        request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not ':' and not '.') ||
                        request.Version < 0 || request.TargetRevision < 0 || method == "control/confirm" && request.PlanId == Guid.Empty)
                        throw new JsonException();
                    reply = Dispatch(actor, method, request, () => Program.ObservePeer(pipe, pid, started));
                }
                catch (JsonException) { reply = Refused("REQUEST_REFUSED"); malformed = true; }
                var expected = mode != "normal" ? "CANCELLATION_ADMISSION_REFUSED" : step switch
                { 0 => "CONFIRMATION_REQUIRED", 1 or 2 => "VERIFIED_LOCAL_SIGNAL", 3 => "CANCELLATION_ADMISSION_REFUSED", _ => "REQUEST_REFUSED" };
                if (reply.Status != expected) throw new InvalidOperationException("WIRE_OUTCOME_REFUSED");
                if (mode == "metadata") _metadataDenied = true;
                if (mode == "revoked") _revocationDenied = true;
                if (mode == "normal" && step == 1) applied = reply;
                if (mode == "normal" && step == 2)
                {
                    if (!SameEffect(applied!, reply) || !reply.IsReplay) throw new InvalidOperationException("REPLAY_CHANGED");
                    _replaysStable = true;
                }
                if (mode == "normal" && step == 3) _crossSessionDenied = true;
                if (mode == "normal" && step == 4) _forgedFieldsDenied = malformed;
                var response = actor.Audience != "agent" ? JsonSerializer.SerializeToUtf8Bytes(reply, Wire) : malformed
                    ? JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = step + 1, error = new { code = -32600, message = "Request refused" } }, Wire)
                    : JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = step + 1, result = reply }, Wire);
                await Program.WriteFrameAsync(pipe, response, timeout.Token);
            }
            await child.WaitForExitAsync(timeout.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException("CLIENT_REFUSED");
            Interlocked.Increment(ref _peerRuns);
        }
        finally { await Program.CleanupAsync(child); }
    }
    private static bool SameEffect(Reply first, Reply second) => first.Status == second.Status && first.PlanId == second.PlanId &&
        first.ReceiptVersion == second.ReceiptVersion && first.FinalSequence == second.FinalSequence &&
        first.LocalSignalVerified == second.LocalSignalVerified && first.Stages.SequenceEqual(second.Stages);
    internal object VerifyAndRecover()
    {
        if (!ClientsComplete || _peerRuns != 4 || !_metadataDenied || !_crossSessionDenied || !_forgedFieldsDenied || !_replaysStable || !_revocationDenied ||
            _actors.Any(a => a.Lease.Revision != 1 || !a.Lease.Token.IsCancellationRequested || a.Plan is null || a.Applied?.Status != "VERIFIED_LOCAL_SIGNAL"))
            throw new InvalidOperationException("COMPOSITION_INCOMPLETE");
        _store.Dispose(); _store = OpenStore(false); _ledger = new(_store.Load(), _store.Save);
        foreach (var actor in _actors)
        {
            var owner = new LocalOwnedStreamControls(Guid.ParseExact(actor.Session, "N")); var lease = owner.Register(TraceSurface.Synthetic);
            _ledger.RegisterCancellationTarget(actor.Session, owner, lease); _syntheticTargets.Add(lease.StreamId, (actor.Session, lease));
            _replacements.Add((owner, lease));
            var replay = Dispatch(actor, "control/confirm", new(actor.Session, actor.Lease.StreamId, actor.Plan!.ActionKey,
                PlanId: actor.Plan.PlanId, Confirmed: true), () => true);
            if (actor.Audience == "agent" ? replay.Status != "CANCELLATION_ADMISSION_REFUSED" : !replay.IsReplay || !SameEffect(actor.Applied!, replay))
                throw new InvalidOperationException("RECOVERY_CHANGED");
            if (lease.Token.IsCancellationRequested) throw new InvalidOperationException("RECOVERY_REDISPATCHED");
        }
        return new { gui_reviewed_local_signal = true, automation_local_signal = true, agent_local_signal = true,
            metadata_scope_refused = true, cross_session_refused = true, forged_fields_refused = true, replay_stable = true,
            revocation_recovered = true, outcomes_recovered = true, replacement_not_cancelled = true,
            one_ledger_gate = true, one_checkpoint_authority = true, observed_peer_runs = _peerRuns,
            provider_stop_verified = false, callback_completion_verified = false };
    }
    internal static async Task<int> ClientAsync(string name, string audience, string mode)
    {
        if (audience is not ("automation" or "agent") || mode is not ("normal" or "metadata" or "revoked")) return 64;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token); await Program.WriteFrameAsync(pipe, "suprachat-cancellation-lab/v1"u8.ToArray(), timeout.Token);
        using var welcome = JsonDocument.Parse(await Program.ReadFrameAsync(pipe, timeout.Token)); var w = welcome.RootElement;
        var session = w.GetProperty("session_id").GetString()!; var target = w.GetProperty("target_id").GetGuid(); var key = w.GetProperty("key").GetString()!;
        var plan = w.GetProperty("plan_id").GetGuid(); Reply? applied = null;
        for (var step = 0; step < (mode == "normal" ? 5 : 1); step++)
        {
            var method = mode == "revoked" || mode == "normal" && step is 1 or 2 or 4 ? "control/confirm" : "control/preview";
            var request = new Request(step == 3 ? w.GetProperty("other_session").GetString()! : session,
                step == 3 ? w.GetProperty("other_target").GetGuid() : target, key, PlanId: plan, Confirmed: true);
            var envelope = new Envelope(audience == "automation" ? method : null, audience == "agent" ? method : null,
                audience == "agent" ? "2.0" : null, audience == "agent" ? step + 1 : null, request);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Wire);
            if (step == 4)
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                bytes = System.Text.Encoding.UTF8.GetBytes(text[..^1] + ",\"actor_id\":\"synthetic-human\"}");
            }
            await Program.WriteFrameAsync(pipe, bytes, timeout.Token);
            using var document = JsonDocument.Parse(await Program.ReadFrameAsync(pipe, timeout.Token)); var root = document.RootElement;
            if (audience == "agent")
            {
                if (root.GetProperty("jsonrpc").GetString() != "2.0" || root.GetProperty("id").GetInt32() != step + 1) return 72;
                if (step == 4) { if (root.GetProperty("error").GetProperty("code").GetInt32() != -32600) return 72; continue; }
                root = root.GetProperty("result");
            }
            var reply = root.Deserialize<Reply>(Wire)!;
            var expected = mode != "normal" ? "CANCELLATION_ADMISSION_REFUSED" : step switch
            { 0 => "CONFIRMATION_REQUIRED", 1 or 2 => "VERIFIED_LOCAL_SIGNAL", 3 => "CANCELLATION_ADMISSION_REFUSED", _ => "REQUEST_REFUSED" };
            if (reply.Status != expected) return 72;
            if (mode == "normal" && step == 0) plan = reply.PlanId;
            if (mode == "normal" && step == 1) applied = reply;
            if (mode == "normal" && step == 2 && (!reply.IsReplay || !SameEffect(applied!, reply))) return 72;
        }
        return 0;
    }
    public void Dispose()
    {
        _closing.Cancel();
        // Workers have no UI synchronization context. Never dispose their authority while still running.
        var workers = Task.WhenAll(_workers.ToArray());
        try { workers.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch { if (!workers.IsCompleted) throw new InvalidOperationException("WORKER_CLEANUP_PENDING"); }
        foreach (var actor in _actors) { actor.Lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); actor.Owner.Dispose(); }
        foreach (var item in _replacements) { item.Lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); item.Owner.Dispose(); }
        _store.Dispose(); _issuer.Dispose(); _custody.Dispose(); _closing.Dispose();
    }
}
