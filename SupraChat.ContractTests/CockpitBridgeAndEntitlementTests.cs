using System;
using System.Collections.Generic;
using SupraChat.Core;

namespace SupraChat.ContractTests;

public static class CockpitBridgeAndEntitlementTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        // ====================================================================
        // 1. Subscription Entitlement Guardrails
        // ====================================================================

        // A. Permitted subscription route (/v1/responses) with CHAT intent
        var chatResult = EntitlementGuard.Evaluate(
            route: "/v1/responses",
            intent: "CHAT",
            storeParameter: false);

        Require(chatResult.Admitted, "Standard CHAT request on /v1/responses must be admitted");
        Require(chatResult.EntitlementTier == EntitlementGuard.SubscriptionTier, "Entitlement tier must be SUBSCRIPTION_CHAT");
        Require(!chatResult.WorkSpendIncurred, "Zero Work spend required for subscription CHAT");
        Require(chatResult.Reason == "PERMITTED_SUBSCRIPTION_CHAT_NO_WORK_SPEND", "Reason mismatch for permitted CHAT");
        Require(!string.IsNullOrEmpty(chatResult.AuditReceiptId), "Audit receipt ID must be generated");

        // B. Disallowed route -> REFUSED
        var invalidRoute = EntitlementGuard.Evaluate(
            route: "/v1/work/task-runner",
            intent: "CHAT",
            storeParameter: false);

        Require(!invalidRoute.Admitted, "Disallowed route must be refused");
        Require(invalidRoute.Reason == "REFUSED_ROUTE_NOT_PERMITTED_UNDER_SUBSCRIPTION", "Reason mismatch for invalid route");

        // C. store: true forbidden under subscription guardrails -> REFUSED
        var storeTrueResult = EntitlementGuard.Evaluate(
            route: "/v1/responses",
            intent: "CHAT",
            storeParameter: true);

        Require(!storeTrueResult.Admitted, "store: true must be refused under subscription guardrails");
        Require(storeTrueResult.Reason == "REFUSED_STORE_TRUE_FORBIDDEN_UNDER_SUBSCRIPTION", "Reason mismatch for store: true");

        // D. Outbound Work-pool billing headers stripped and sanitized
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer test-token",
            ["OpenAI-Organization-Work-Pool"] = "pool-enterprise-alpha",
            ["X-OpenAI-Work-Context"] = "ctx-456"
        };

        var headerResult = EntitlementGuard.Evaluate(
            route: "/v1/responses",
            intent: "CHAT",
            storeParameter: false,
            headers: headers);

        Require(headerResult.Admitted, "Sanitized CHAT request must be admitted");
        Require(headerResult.ForbiddenHeadersBlocked.Count == 2, "Both forbidden Work headers must be detected and blocked");
        Require(!headers.ContainsKey("OpenAI-Organization-Work-Pool"), "Work-pool header must be stripped from outbound dictionary");
        Require(!headers.ContainsKey("X-OpenAI-Work-Context"), "Work-context header must be stripped from outbound dictionary");
        Require(headers.ContainsKey("Authorization"), "Legitimate Authorization header must be preserved");

        // E. Unconsented WORK intent -> REFUSED (Zero Work spend)
        var unapprovedWork = EntitlementGuard.Evaluate(
            route: "/v1/responses",
            intent: "WORK",
            storeParameter: false);

        Require(!unapprovedWork.Admitted, "Unapproved WORK intent must be refused");
        Require(unapprovedWork.Reason == "REFUSED_WORK_INTENT_REQUIRES_OPERATOR_APPROVAL", "Work refusal reason mismatch");
        Require(!unapprovedWork.WorkSpendIncurred, "No Work spend must be incurred on refusal");

        // F. Confirmed operator work lease -> ADMITTED
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var confirmedLease = new BrowserExperienceModePolicy.HostWorkLease(
            Type: "EXPLICIT_HOST_WORK_LEASE",
            Session: "session-operator-1",
            Epoch: 1,
            Confirmed: true,
            Expires: nowSeconds + 300);

        var approvedWork = EntitlementGuard.Evaluate(
            route: "/v1/responses",
            intent: "WORK",
            storeParameter: false,
            operatorWorkLease: confirmedLease,
            nowUnixSeconds: nowSeconds);

        Require(approvedWork.Admitted, "Explicitly confirmed operator work lease must be admitted");
        Require(approvedWork.WorkSpendIncurred, "Work spend flag must be accurately recorded on approved lease");
        Require(approvedWork.EntitlementTier == EntitlementGuard.WorkTier, "Entitlement tier must be METERED_WORK_CREDITS");

        // G. Expired operator work lease -> REFUSED
        var expiredLease = new BrowserExperienceModePolicy.HostWorkLease(
            Type: "EXPLICIT_HOST_WORK_LEASE",
            Session: "session-operator-1",
            Epoch: 1,
            Confirmed: true,
            Expires: nowSeconds - 300);

        var expiredResult = EntitlementGuard.Evaluate(
            route: "/v1/responses",
            intent: "WORK",
            storeParameter: false,
            operatorWorkLease: expiredLease,
            nowUnixSeconds: nowSeconds);

        Require(!expiredResult.Admitted, "Expired operator work lease must be refused");

        // ====================================================================
        // 2. Terminal Transfer Bridge & Granular Selection
        // ====================================================================

        // A. ANSI escape sequence stripping
        var ansiRaw = "\x1B[31mError:\x1B[0m Process exited with \x1B[1mcode 1\x1B[0m\r\n";
        var ansiClean = TerminalTransferBridge.StripAnsi(ansiRaw);
        Require(ansiClean == "Error: Process exited with code 1\r\n", "ANSI stripping failed: " + ansiClean);

        // B. Markdown code block extraction
        var markdownSample = @"
Here are two separate commands to run:

```bash
git fetch origin
git status
```

And to verify tests:
```powershell
dotnet test
```
";
        var snippets = TerminalTransferBridge.ExtractCodeSnippets(markdownSample);
        Require(snippets.Count == 2, "Expected 2 code snippets, got " + snippets.Count);
        Require(snippets[0].Language == "bash", "Snippet 1 language mismatch");
        Require(snippets[0].Lines.Count == 2, "Snippet 1 line count mismatch");
        Require(snippets[0].Lines[0] == "git fetch origin", "Snippet 1 line 1 mismatch");
        Require(snippets[1].Language == "powershell", "Snippet 2 language mismatch");
        Require(snippets[1].Lines[0] == "dotnet test", "Snippet 2 line 1 mismatch");

        // C. Secret redaction filter
        var textWithSecret = "API call failed with token sk-abcdef1234567890abcdef12345678 and Bearer eyJhbGciOiJIUzI1NiJ9";
        var redacted = TerminalTransferBridge.RedactSecrets(textWithSecret);
        Require(!redacted.Contains("sk-abcdef1234567890abcdef12345678"), "Raw OpenAI secret must be redacted");
        Require(redacted.Contains("[REDACTED_SECRET]"), "Secret replacement token missing");

        // D. Last command output extraction from terminal buffer
        var bufferSample = @"
PS C:\Users\Mark\Projects> git status
On branch main
Your branch is up to date with 'origin/main'.
nothing to commit, working tree clean
PS C:\Users\Mark\Projects> 
";
        var execBlock = TerminalTransferBridge.ExtractLastCommandOutput(bufferSample);
        Require(execBlock.Command == "git status", "Command extraction mismatch: " + execBlock.Command);
        Require(execBlock.Output.Contains("working tree clean"), "Command stdout missing");
        Require(!execBlock.ContainsError, "Clean status should not report error");

        // E. Error detection in terminal output
        var errorBuffer = @"
PS C:\Users\Mark\Projects> dotnet run --invalid
Unhandled exception: System.InvalidOperationException: Command not found
PS C:\Users\Mark\Projects> 
";
        var errorBlock = TerminalTransferBridge.ExtractLastCommandOutput(errorBuffer);
        Require(errorBlock.ContainsError, "Error pattern must be detected");

        // F. Format output for chat
        var formatted = TerminalTransferBridge.FormatTerminalOutputForChat("git status", "working tree clean", 0);
        Require(formatted.Contains("Command: `git status`"), "Formatted command header missing");
        Require(formatted.Contains("Exit Code: `0`"), "Formatted exit code missing");
        Require(formatted.Contains("```terminal-output"), "Terminal output markdown block missing");

        // G. Error trace extraction
        var stackTraceBuffer = @"
Unhandled exception: System.NullReferenceException: Object reference not set to an instance of an object.
   at SupraChat.Core.Example.Run() in C:\Projects\Example.cs:line 42
   at Program.Main(String[] args) in C:\Projects\Program.cs:line 10
";
        var extractedTrace = TerminalTransferBridge.ExtractErrorTrace(stackTraceBuffer);
        Require(extractedTrace.Contains("NullReferenceException"), "Error trace must capture exception line");
        Require(extractedTrace.Contains("line 42"), "Error trace must capture call stack frame");

        // H. Sensitive token detection
        Require(TerminalTransferBridge.ContainsSensitiveTokens("bearer sk-123456789012345678901234"), "Must detect OpenAI secret token");
        Require(!TerminalTransferBridge.ContainsSensitiveTokens("public static void Main()"), "Safe text must not report sensitive tokens");

        // ====================================================================
        // 3. SessionTerminalHost Execution & State Checks
        // ====================================================================
        using var terminalHost = new SessionTerminalHost();
        Require(!terminalHost.IsRunning, "Terminal host must be initially idle");
        var exit = await terminalHost.ExecuteCommandAsync("echo test-cockpit-terminal");
        Require(exit == 0, "Echo command exit code must be 0, got " + exit);
        Require(terminalHost.LastOutput.Contains("test-cockpit-terminal"), "LastOutput must contain echoed text");
        Require(terminalHost.LastCommand == "echo test-cockpit-terminal", "LastCommand mismatch");
        Require(!terminalHost.IsRunning, "Terminal host must return to idle state after completion");

        terminalHost.ClearScrollback();
        Require(string.IsNullOrEmpty(terminalHost.LastOutput), "LastOutput must be empty after ClearScrollback");

        return;
    }
}
