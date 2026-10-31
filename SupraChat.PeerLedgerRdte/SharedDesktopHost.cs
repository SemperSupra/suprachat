using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupraChat.Automation;
using SupraChat.Core;

namespace SupraChat.PeerLedgerRdte;

[SupportedOSPlatform("windows")]
internal sealed class SharedDesktopHost : IDisposable
{
    internal sealed record Request(string SessionId, string Key, long Version = 0, string? Value = null);
    internal sealed record Envelope(string? Operation, string? Method, string? Jsonrpc, int? Id, Request Params);
    internal sealed record Reply(string Status, long Version, long Cursor, int TraceEvents, bool Replayed = false);
    internal sealed record Projection(int Session, string Title, long Version, long Cursor, int TraceEvents);
    private readonly WindowsOwnerCustody _custody = WindowsOwnerCustody.Create();
    private readonly W12ScopedGrantAuthority _issuer;
    private W9DurableSessionStore _store;
    private SharedSessionLedger _ledger;
    private readonly TraceNormalizer _trace = new();
    private readonly string[] _sessions;
    private readonly LocalTraceSession[] _traceCaps;
    private readonly MetadataCaptureConsent? _consent;
    private readonly MetadataConsentBinding[]? _consentBindings;
    private readonly object[] _consentViews = [new(), new()];
    private readonly ConsentClock _consentTime = new();
    private int _consentAllowed, _consentCancelled, _consentFirstPartyRefused, _consentViewsChanged, _consentExpired;
    public bool ConsentEnabled => _consent is not null;
    private readonly string[] _ownerGrants;
    private readonly string[] _peerGrants;
    private readonly string _sameSessionAgentGrant;
    private readonly CancellationTokenSource _closing = new(TimeSpan.FromSeconds(90));
    private readonly ConcurrentBag<Task> _workers = new();
    private static readonly JsonSerializerOptions WireOptions = new(TraceNormalizer.JsonOptions)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private int _peers, _clientRuns, _revocationRuns;
    public int GuiMutations { get; private set; }
    public bool ClientsComplete { get; private set; }
    public bool AgentRevoked { get; private set; }

    public SharedDesktopHost(bool requireConsent = false)
    {
        using var material = _custody.InitializeIssuer();
        _issuer = material.CreateAuthority();
        _store = OpenStore(create: true);
        _ledger = new(_store.Load(), _store.Save);
        var createOwnerGrant = _issuer.Issue("local-owner-fixture", "human", "create-only",
            ["session.create", "session.approve"], TimeSpan.FromMinutes(2));
        _sessions = new[] { "Session A", "Session B" }.Select((title, i) =>
            _ledger.ExecuteAdmitted(new("open", "create-only", "shared-lab-open-session-" + i, 0, title), () =>
            {
                var grant = _issuer.Validate(createOwnerGrant, "create-only", "session.create", "local-owner-fixture", "human");
                var approve = _issuer.Validate(createOwnerGrant, "create-only", "session.approve", "local-owner-fixture", "human");
                return new(grant.Allowed && approve.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId,
                    HumanBootstrapVerified: approve.Allowed && grant.Audience == "human");
            }).Session!.Id).ToArray();
        _traceCaps = _sessions.Select(id => _trace.CreateSession(Guid.ParseExact(id, "N"))).ToArray();
        if (requireConsent)
        {
            _consent = new(_trace, _consentTime);
            _consentBindings = _traceCaps.Select((cap, i) => _consent.Register(cap, _consentViews[i])).ToArray();
        }
        _ownerGrants = _sessions.Select(id => _issuer.Issue("local-owner-fixture", "human", id,
            ["session.read", "session.mutate", "session.approve"], TimeSpan.FromMinutes(2))).ToArray();
        _peerGrants = _sessions.Select((id, i) => _issuer.Issue(i == 0 ? "automation-fixture" : "agent-fixture",
            i == 0 ? "automation" : "agent", id, ["session.read", "session.mutate"], TimeSpan.FromMinutes(2))).ToArray();
        // Independent agent, distinct actor/grant, targets the *same* session as GUI and automation.
        _sameSessionAgentGrant = _issuer.Issue("agent-shared-fixture", "agent", _sessions[0],
            ["session.read", "session.mutate"], TimeSpan.FromMinutes(2));
        foreach (var cap in _traceCaps)
        {
            if (requireConsent) continue;
            _trace.SetCapture(cap, TraceCaptureMode.Metadata);
            _trace.TryObserve(cap, new(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Local, TraceEventKind.Request));
        }
    }

    private sealed class ConsentClock : TimeProvider
    {
        private readonly long _started = TimeProvider.System.GetTimestamp();
        private long _offset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => TimeProvider.System.GetElapsedTime(_started).Ticks + Interlocked.Read(ref _offset);
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref _offset));
        public void Expire() => Interlocked.Add(ref _offset, TimeSpan.FromMinutes(3).Ticks);
    }

    private T WithConsent<T>(int index, Func<MetadataCaptureConsent, MetadataConsentBinding, T> action, bool write = false)
    {
        var result = _ledger.InspectProjectionAdmitted(_sessions[index], () => OwnerAdmission(index, write ? "session.mutate" : "session.read"),
            _ => action(_consent ?? throw new InvalidOperationException("CONSENT_MODE_REQUIRED"), _consentBindings![index]));
        if (result.Admission.Status != "OBSERVED") throw new UnauthorizedAccessException("CONSENT_OWNER_REFUSED");
        return result.Projection!;
    }
    public MetadataConsentStatus ConsentStatus(int index) => WithConsent(index, (c, b) => c.Status(b));
    public bool ConsentReviewPending(int index, MetadataConsentPlan plan) => WithConsent(index, (c, b) => c.IsPending(b, plan));
    public MetadataConsentPlan ConsentPreview(int index, bool firstParty = false) => WithConsent(index, (c, b) =>
    {
        var plan = c.Preview(b, firstParty ? MetadataConsentScope.FirstPartyMetadata : MetadataConsentScope.SyntheticMetadata);
        if (firstParty && !plan.Review.Available) _consentFirstPartyRefused++;
        return plan;
    });
    public void ConsentCancel(int index, MetadataConsentPlan plan) => WithConsent(index, (c, _) => { c.CancelPreview(plan); _consentCancelled++; return true; });
    public MetadataConsentResult ConsentConfirm(int index, MetadataConsentPlan plan, bool reviewed) => WithConsent(index, (c, b) =>
    {
        var result = c.Confirm(b, plan, reviewed); if (result.Outcome == "ALLOWED") _consentAllowed++;
        return result;
    }, write: true);
    public void ConsentStop(int index) => WithConsent(index, (c, b) => { c.Stop(b); return true; }, write: true);
    public bool ConsentEmit(int index) => WithConsent(index, (c, b) => c.TryObserve(b, _consentViews[index],
        new(TraceSurface.Synthetic, TraceAdapter.Fixture, TraceDirection.Local, TraceEventKind.Request)));
    public void ConsentReplaceView(int index) => WithConsent(index, (c, b) =>
    {
        var next = new object(); c.ReplaceView(b, _consentViews[index], next); _consentViews[index] = next;
        _consentViewsChanged++; return true;
    }, write: true);
    public void ConsentExpire()
    {
        _consentTime.Expire();
        for (var i = 0; i < 2; i++) if (ConsentStatus(i).State == "EXPIRED") _consentExpired++;
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

    private SessionLedgerAdmission OwnerAdmission(int index, string action)
    {
        var grant = _issuer.Validate(_ownerGrants[index], _sessions[index], action, "local-owner-fixture", "human");
        var approve = _issuer.Validate(_ownerGrants[index], _sessions[index], "session.approve", "local-owner-fixture", "human");
        return new(grant.Allowed && approve.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId,
            HumanBootstrapVerified: approve.Allowed && grant.Audience == "human");
    }

    public Projection Read(int index)
    {
        var view = _ledger.InspectProjectionAdmitted(_sessions[index], () => OwnerAdmission(index, "session.read"), session =>
            new Projection(index, session.Title, session.Version, _ledger.InspectAll().Sequence, TraceCount(index, session.Id)));
        return view.Projection ?? throw new UnauthorizedAccessException("LAB_READ_REFUSED");
    }

    public string Rename(int index, long version, string title)
    {
        var result = _ledger.ExecuteAdmitted(new("set-title", _sessions[index], "gui-" + Guid.NewGuid().ToString("N"), version, title),
            () => OwnerAdmission(index, "session.mutate"));
        if (result.Applied) GuiMutations++;
        return result.Status;
    }

    private int TraceCount(int index, string sessionId)
    {
        var page = _trace.Read(_traceCaps[index]);
        if (page.SessionId.ToString("N") != sessionId || page.Events.Any(e => e.SessionId.ToString("N") != sessionId))
            throw new UnauthorizedAccessException("LAB_TRACE_SESSION_REFUSED");
        return page.Events.Count;
    }

    public async Task RunClientsAsync()
    {
        if (Interlocked.Exchange(ref _clientRuns, 1) != 0) throw new InvalidOperationException("LAB_CLIENTS_ALREADY_RUN");
        var a = Task.Run(() => ServeAsync(0, revoke: false));
        var b = Task.Run(() => ServeAsync(1, revoke: false));
        _workers.Add(a); _workers.Add(b);
        await Task.WhenAll(a, b);
        // After the automation client commits session A, a separate agent process
        // reads the resulting version and performs its own CAS mutation on A.
        // This is a real cross-process same-session rep, not a second ledger.
        var sharedAgent = Task.Run(() => ServeAsync(0, revoke: false, sameSessionAgent: true));
        _workers.Add(sharedAgent);
        await sharedAgent;
        ClientsComplete = true;
    }

    public async Task RevokeAgentAsync()
    {
        if (!ClientsComplete || Interlocked.Exchange(ref _revocationRuns, 1) != 0)
            throw new InvalidOperationException("LAB_REVOCATION_UNAVAILABLE");
        var worker = Task.Run(() => ServeAsync(1, revoke: true));
        _workers.Add(worker); await worker;
        AgentRevoked = true;
    }

    private async Task ServeAsync(int index, bool revoke, bool sameSessionAgent = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var pipeName = "suprachat-shared-lab-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        var audience = sameSessionAgent || index == 1 ? "agent" : "automation";
        var actor = sameSessionAgent ? "agent-shared-fixture" : index == 0 ? "automation-fixture" : "agent-fixture";
        using var child = Program.Spawn("--shared-client", pipeName, audience, sameSessionAgent ? "replay" : revoke ? "revoke" : "mutate");
        try
        {
            var pid = child.Id; var started = child.StartTime.ToUniversalTime();
            await pipe.WaitForConnectionAsync(timeout.Token);
            var hello = await Program.ReadFrameAsync(pipe, timeout.Token);
            if (!hello.AsSpan().SequenceEqual("suprachat-shared-lab/v1"u8) || !Program.ObservePeer(pipe, pid, started))
                throw new UnauthorizedAccessException("LAB_PEER_REFUSED");
            // A non-authority welcome supplies synthetic IDs/revision; no grant or principal is accepted from the child.
            var initial = Read(index);
            await Program.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new
            { session_id = _sessions[index], other_session_id = _sessions[1 - index], version = initial.Version, consent_protocol = ConsentEnabled }), timeout.Token);
            var methods = sameSessionAgent
                ? new[] { "session/inspect", "session/change", "session/change",
                    "session/change", "session/change", "trace/read", "session/inspect" }
                : revoke ? new[] { "session/revoke-self", "session/inspect" } :
                ConsentEnabled ? new[] { "session/inspect", "session/change", "trace/read", "session/inspect", "consent/status" } :
                new[] { "session/inspect", "session/change", "trace/read", "session/inspect" };
            for (var n = 0; n < methods.Length; n++)
            {
                var bytes = await Program.ReadFrameAsync(pipe, timeout.Token);
                var envelope = JsonSerializer.Deserialize<Envelope>(bytes, WireOptions) ?? throw new InvalidDataException();
                var format = audience == "agent"
                    ? envelope.Jsonrpc == "2.0" && envelope.Operation is null && envelope.Method == methods[n] && envelope.Id == n + 1
                    : envelope.Jsonrpc is null && envelope.Method is null && envelope.Id is null && envelope.Operation == methods[n];
                if (!format || !Program.ObservePeer(pipe, pid, started)) throw new UnauthorizedAccessException("LAB_PEER_REFUSED");
                var request = envelope.Params;
                var operation = methods[n] switch { "session/change" => "set-title", "session/revoke-self" => "revoke-self", _ => "inspect" };
                SessionLedgerAdmission Admit()
                {
                    if (!Program.ObservePeer(pipe, pid, started)) return new(false, null, null, null, null);
                    var grant = _issuer.Validate(sameSessionAgent ? _sameSessionAgentGrant : _peerGrants[index], request.SessionId,
                        operation == "inspect" ? "session.read" : "session.mutate", actor, audience);
                    return new(grant.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId);
                }
                Reply reply;
                if (methods[n] is "trace/read" or "consent/status")
                {
                    var projection = _ledger.InspectProjectionAdmitted(request.SessionId, Admit,
                        session => new Reply(methods[n] == "consent/status" ?
                            (_consent!.Status(_consentBindings![index]).CaptureActive ? "CONSENT_ACTIVE" : "CONSENT_OFF") : "OBSERVED",
                            session.Version, _ledger.InspectAll().Sequence, TraceCount(index, session.Id)));
                    reply = projection.Projection ?? new(projection.Admission.Status, -1, -1, 0);
                }
                else
                {
                    var result = _ledger.ExecuteAdmitted(new(operation, request.SessionId, request.Key, request.Version, request.Value), Admit);
                    reply = new(result.Status, result.Session?.Version ?? -1, result.Sequence, 0, result.IsReplay);
                }
                var expected = sameSessionAgent ? n switch
                {
                    1 or 2 => "APPLIED",
                    3 => "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT",
                    4 => "VERSION_CONFLICT",
                    _ => "OBSERVED"
                } : revoke ? (n == 0 ? "REVOKED" : "GRANT_REVOKED") :
                    n == 1 ? "APPLIED" : n == 3 ? "ADMISSION_REFUSED" : n == 4 ? "CONSENT_OFF" : "OBSERVED";
                if (reply.Status != expected || (sameSessionAgent && reply.Replayed != (n == 2)))
                    throw new InvalidOperationException("LAB_PROTOCOL_OUTCOME_REFUSED");
                var response = audience == "agent"
                    ? JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = envelope.Id, result = reply }, WireOptions)
                    : JsonSerializer.SerializeToUtf8Bytes(reply, WireOptions);
                await Program.WriteFrameAsync(pipe, response, timeout.Token);
            }
            await child.WaitForExitAsync(timeout.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException("LAB_CLIENT_FAILED");
            Interlocked.Increment(ref _peers);
        }
        finally { await Program.CleanupAsync(child); }
    }

    internal static async Task<int> ClientAsync(string name, string audience, string mode)
    {
        if (audience is not ("automation" or "agent") || mode is not ("mutate" or "revoke" or "replay")) return 64;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        await Program.WriteFrameAsync(pipe, "suprachat-shared-lab/v1"u8.ToArray(), timeout.Token);
        using var welcome = JsonDocument.Parse(await Program.ReadFrameAsync(pipe, timeout.Token));
        var session = welcome.RootElement.GetProperty("session_id").GetString()!;
        var other = welcome.RootElement.GetProperty("other_session_id").GetString()!;
        var version = welcome.RootElement.GetProperty("version").GetInt64();
        var consentProtocol = welcome.RootElement.GetProperty("consent_protocol").GetBoolean();
        var methods = mode == "replay"
            ? new[] { "session/inspect", "session/change", "session/change",
                "session/change", "session/change", "trace/read", "session/inspect" }
            : mode == "revoke" ? new[] { "session/revoke-self", "session/inspect" } :
            consentProtocol ? new[] { "session/inspect", "session/change", "trace/read", "session/inspect", "consent/status" } :
            new[] { "session/inspect", "session/change", "trace/read", "session/inspect" };
        for (var n = 0; n < methods.Length; n++)
        {
            var request = new Request(mode == "mutate" && n == 3 ? other : session,
                mode == "replay" && n == 4 ? audience + "-shared-stale-key" : audience + "-shared-mutation-key",
                mode == "replay" && n == 4 ? version - 1 : version,
                mode == "replay" && n == 3 ? audience + "-altered-payload" : audience + "-updated");
            var envelope = new Envelope(audience == "automation" ? methods[n] : null,
                audience == "agent" ? methods[n] : null, audience == "agent" ? "2.0" : null, audience == "agent" ? n + 1 : null, request);
            await Program.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(envelope, TraceNormalizer.JsonOptions), timeout.Token);
            using var response = JsonDocument.Parse(await Program.ReadFrameAsync(pipe, timeout.Token));
            var result = response.RootElement;
            if (audience == "agent")
            {
                if (result.GetProperty("jsonrpc").GetString() != "2.0" || result.GetProperty("id").GetInt32() != n + 1 ||
                    result.TryGetProperty("error", out _)) return 72;
                result = result.GetProperty("result");
            }
            var reply = result.Deserialize<Reply>(WireOptions)!;
            var expected = mode == "replay" ? n switch
            {
                1 or 2 => "APPLIED",
                3 => "IDEMPOTENCY_KEY_PAYLOAD_CONFLICT",
                4 => "VERSION_CONFLICT",
                _ => "OBSERVED"
            } : mode == "revoke" ? (n == 0 ? "REVOKED" : "GRANT_REVOKED") :
                n == 1 ? "APPLIED" : n == 3 ? "ADMISSION_REFUSED" : n == 4 ? "CONSENT_OFF" : "OBSERVED";
            if (reply.Status != expected || mode == "mutate" && n is 1 or 2 && reply.Version != version + 1 || mode == "replay" && reply.Replayed != (n == 2)) return 72;
            if (mode == "replay" && (n is 1 or 2 or 4 or 5 or 6) && reply.Version != version + 1) return 72;
            if (mode == "replay" && n == 3 && reply.Version != -1) return 72;
        }
        return 0;
    }

    public object VerifyAndRecover()
    {
        if (!ClientsComplete || !AgentRevoked || GuiMutations < 1 || _peers != 4) throw new InvalidOperationException("LAB_INCOMPLETE");
        var before = new[] { Read(0), Read(1) };
        _store.Dispose(); _store = OpenStore(create: false); _ledger = new(_store.Load(), _store.Save);
        var after = new[] { Read(0), Read(1) };
        var grant = _issuer.Validate(_peerGrants[1], _sessions[1], "session.read");
        var copied = false;
        var recoveredRead = _ledger.InspectProjectionAdmitted(_sessions[1], () =>
            new SessionLedgerAdmission(grant.Allowed, grant.ActorId, grant.Audience, grant.SessionId, grant.GrantId),
            session => { copied = true; return session.Version; });
        if (!grant.Allowed || !_ledger.IsGrantRevoked(grant.GrantId!) || !before.SequenceEqual(after) ||
            recoveredRead.Admission.Status != "GRANT_REVOKED" || copied ||
            after[0].Version != 3 || after[1].Version != 1 || after.Any(p => p.TraceEvents != 1))
            throw new InvalidOperationException("LAB_RECOVERY_REFUSED");
        var consent = ConsentEnabled ? new[] { ConsentStatus(0), ConsentStatus(1) } : null;
        if (consent is not null && (consent.Any(s => s.CaptureActive || s.RetainedEvents != 1) ||
            consent.Sum(s => s.RefusedObservations) < 5 || _consentAllowed != 3 || _consentCancelled < 1 ||
            _consentFirstPartyRefused < 1 || _consentViewsChanged < 1 || _consentExpired < 1))
            throw new InvalidOperationException("CONSENT_LAB_INCOMPLETE");
        return new
        {
            shared_sessions = 2, gui_mutations = GuiMutations, enrolled_peer_runs = _peers,
            automation_and_agent_protocols = true, same_session_agent_after_automation = true,
            same_session_replay_conflict_stale_qualified = true,
            wrong_session_refused = true, revoked_read_refused = true,
            versions = after.Select(p => p.Version).ToArray(), common_cursor = after.Select(p => p.Cursor).Distinct().Count() == 1,
            durable_revocation_recovered = true, metadata_recovered = true, trace_session_routing = true,
            consent = consent is null ? null : new { synthetic_only = true, stopped = true, preview_cancelled = true,
                view_invalidated = true, lease_expired = true, first_party_refused = true, reviewed_starts = _consentAllowed,
                refused_observations = consent.Sum(s => s.RefusedObservations), machine_status_agrees = true }
        };
    }

    public void Dispose()
    {
        _closing.Cancel();
        try { if (!Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(5))) Environment.Exit(78); }
        catch (AggregateException) { /* Failed workers are terminal; their finally blocks drained their owned children. */ }
        _store.Dispose(); _issuer.Dispose(); _consent?.Dispose(); _trace.Dispose(); _custody.Dispose(); _closing.Dispose();
    }
}
