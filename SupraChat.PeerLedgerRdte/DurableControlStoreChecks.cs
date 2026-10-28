using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using SupraChat.Automation;
using SupraChat.Core;

namespace SupraChat.PeerLedgerRdte;

/// <summary>Disposable synthetic child processes only; exports fixed case results, never checkpoint contents.</summary>
[SupportedOSPlatform("windows")]
internal static class DurableControlStoreChecks
{
    private sealed record Case(string Fault, int ExitCode, bool Passed, bool LocalSignalObserved, string RecoveredStatus);
    internal static int Child(string root, string fault)
    {
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_DISPOSABLE_ONLY", "yes");
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_CREATE_ONCE", "yes");
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS", "1");
        Environment.SetEnvironmentVariable("SUPRACHAT_W9_FAULT", fault);
        Environment.SetEnvironmentVariable("SUPRACHAT_W14_WINDOWS_CUSTODY", null);
        using var store = new W9DurableSessionStore(Path.Combine(root, "control.sqlite"));
        var ledger = new SharedSessionLedger(store.Load(), cp =>
        {
            if (cp.CancellationJournal?.SingleOrDefault()?.Stages.LastOrDefault()?.Stage == "APPLIED_LOCAL_SIGNAL")
                File.WriteAllText(Path.Combine(root, "local-signal-observed"), "SYNTHETIC_LOCAL_SIGNAL_ONLY");
            store.Save(cp);
        });
        var session = ledger.Open("synthetic-control-open", "automation", "synthetic-control").Session!.Id;
        using var owner = new LocalOwnedStreamControls(Guid.ParseExact(session, "N"));
        var stream = owner.Register(TraceSurface.Synthetic);
        var target = ledger.RegisterCancellationTarget(session, owner, stream);
        var grant = Guid.NewGuid().ToString("N");
        CancellationAdmission Admit() => new(new(true, "synthetic-control-actor", "automation", session, grant), true);
        var plan = ledger.PreviewCancellation(session, target, 0, 0, "synthetic-control-cancel", Admit).Entry!.Plan;
        var result = ledger.ConfirmCancellation(session, plan.PlanId, target, 0, 0, plan.ActionKey, true, Admit);
        stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return result.Status == "VERIFIED_LOCAL_SIGNAL" ? 0 : 70;
    }
    internal static async Task<int> RunAsync(string receiptPath)
    {
        if (File.Exists(receiptPath)) return 64;
        var cases = new List<Case>();
        var oldDisposable = Environment.GetEnvironmentVariable("SUPRACHAT_W9_DISPOSABLE_ONLY");
        var oldCreate = Environment.GetEnvironmentVariable("SUPRACHAT_W9_CREATE_ONCE");
        var oldFaults = Environment.GetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS");
        var oldCustody = Environment.GetEnvironmentVariable("SUPRACHAT_W14_WINDOWS_CUSTODY");
        try
        {
            Environment.SetEnvironmentVariable("SUPRACHAT_W9_DISPOSABLE_ONLY", "yes");
            Environment.SetEnvironmentVariable("SUPRACHAT_W9_CREATE_ONCE", "no");
            Environment.SetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS", null);
            Environment.SetEnvironmentVariable("SUPRACHAT_W14_WINDOWS_CUSTODY", null);
            foreach (var fault in new[] { "NONE", "CONTROL_BEFORE_REQUEST", "CONTROL_AFTER_REQUEST", "CONTROL_BEFORE_APPLIED", "CONTROL_AFTER_APPLIED", "CONTROL_WRITE_FAILURE" })
            {
                var root = Path.Combine(Path.GetTempPath(), "suprachat-w9-owned-control-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root); // Preserved disposable artifacts; no user directory deletion.
                using var child = Program.Spawn("--control-crash", root, fault);
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
                catch { await Program.CleanupAsync(child); throw; }
                var expectedExit = fault == "NONE" ? 0 : fault == "CONTROL_WRITE_FAILURE" ? 78 : fault is "CONTROL_BEFORE_REQUEST" or "CONTROL_BEFORE_APPLIED" ? 88 : 89;
                using var store = new W9DurableSessionStore(Path.Combine(root, "control.sqlite"));
                var cp = store.Load()!; var entry = cp.CancellationJournal!.Single(); var plan = entry.Plan;
                var expectedSignal = fault is "NONE" or "CONTROL_BEFORE_APPLIED" or "CONTROL_AFTER_APPLIED";
                var observedSignal = File.Exists(Path.Combine(root, "local-signal-observed"));
                var recovered = new SharedSessionLedger(cp, store.Save);
                CancellationAdmission Admit() => new(new(true, plan.ActorId, plan.Audience, plan.SessionId, plan.GrantId), true);
                var result = recovered.ConfirmCancellation(plan.SessionId, plan.PlanId, plan.TargetId, 0, 0, plan.ActionKey, true, Admit);
                var expectedStatus = fault switch
                {
                    "NONE" => "VERIFIED_LOCAL_SIGNAL",
                    "CONTROL_BEFORE_REQUEST" or "CONTROL_WRITE_FAILURE" => "HOST_RUN_CHANGED",
                    "CONTROL_AFTER_APPLIED" => "APPLIED_LOCAL_SIGNAL",
                    _ => "OUTCOME_UNKNOWN_FRESH_APPROVAL_REQUIRED"
                };
                cases.Add(new(fault, child.ExitCode, child.ExitCode == expectedExit && observedSignal == expectedSignal &&
                    result.Status == expectedStatus && !result.ProviderStopVerified && !result.CallbackCompletionVerified,
                    observedSignal, result.Status));
            }
        }
        catch { /* Fixed failure result; never serialize exceptions or checkpoint data. */ }
        finally
        {
            Environment.SetEnvironmentVariable("SUPRACHAT_W9_DISPOSABLE_ONLY", oldDisposable);
            Environment.SetEnvironmentVariable("SUPRACHAT_W9_CREATE_ONCE", oldCreate);
            Environment.SetEnvironmentVariable("SUPRACHAT_W9_RDTE_FAULTS", oldFaults);
            Environment.SetEnvironmentVariable("SUPRACHAT_W14_WINDOWS_CUSTODY", oldCustody);
        }
        var passed = cases.Count == 6 && cases.All(c => c.Passed);
        using var output = new FileStream(receiptPath, FileMode.CreateNew);
        JsonSerializer.Serialize(output, new { schema = "suprachat-durable-control-store-qualification/v1", passed,
            scope = "SYNTHETIC_LOCAL_CANCELLATION_ONLY", cases, provider_stop_verified = false,
            first_party_capture = false, production_control_admitted = false, credentials_used = false,
            checkpoint_exported = false, automatic_recovery_dispatch = false });
        return passed ? 0 : 1;
    }
}
