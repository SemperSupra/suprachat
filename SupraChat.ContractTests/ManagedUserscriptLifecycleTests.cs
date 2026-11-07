using SupraChat.Core;

namespace SupraChat.ContractTests;

public static class ManagedUserscriptLifecycleTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        var manager = ManagedUserscriptManager.CreateDefault();

        // 1. Canonical Chat-lock #97 script verification (T09)
        var defaultManifest = manager.GetManifest("chatgpt-lock-chat-mode");
        Require(defaultManifest != null, "Default chat-lock manifest must be registered");
        Require(defaultManifest!.Enabled, "Default manifest must be enabled initially");
        Require(!defaultManifest.Revoked, "Default manifest must not be revoked initially");

        var verifyResult = manager.VerifyAndRegister(defaultManifest, ManagedUserscriptManager.CanonicalChatLockScript);
        Require(verifyResult.Success, "Canonical script content must verify cleanly against its SHA-256 hash");
        Require(verifyResult.Status == "VERIFIED_AND_REGISTERED", "Status must be VERIFIED_AND_REGISTERED");

        // 2. Tampered script content -> REFUSED_HASH_MISMATCH
        var tamperedScript = ManagedUserscriptManager.CanonicalChatLockScript + "\n// unauthorized edit";
        var tamperedResult = manager.VerifyAndRegister(defaultManifest, tamperedScript);
        Require(!tamperedResult.Success, "Tampered script must be refused");
        Require(tamperedResult.Status == "REFUSED_HASH_MISMATCH", "Status must be REFUSED_HASH_MISMATCH");

        // 3. Origin mismatch -> REFUSED_ORIGIN_MISMATCH
        var spoofedManifest = defaultManifest with
        {
            Id = "spoofed-script",
            Origin = "https://evil.chatgpt.com"
        };
        var spoofResult = manager.VerifyAndRegister(spoofedManifest, ManagedUserscriptManager.CanonicalChatLockScript);
        Require(!spoofResult.Success, "Spoofed origin must be refused");
        Require(spoofResult.Status == "REFUSED_ORIGIN_MISMATCH", "Status must be REFUSED_ORIGIN_MISMATCH");

        // 4. Unauthorized grants -> REFUSED_UNAUTHORIZED_GRANTS
        var grantScript = "// valid content";
        var grantHash = ManagedUserscriptManager.ComputeSha256(grantScript);
        var grantManifest = new UserscriptManifest(
            Id: "unsafe-grant-script",
            Name: "Unsafe Grant Script",
            Version: "1.0.0",
            Origin: ManagedUserscriptManager.CanonicalOrigin,
            RunAt: "document-start",
            ContentHash: grantHash,
            Grants: new[] { "GM_xmlhttpRequest" });

        var grantResult = manager.VerifyAndRegister(grantManifest, grantScript);
        Require(!grantResult.Success, "Unauthorized GM_xmlhttpRequest grant must be refused");
        Require(grantResult.Status == "REFUSED_UNAUTHORIZED_GRANTS", "Status must be REFUSED_UNAUTHORIZED_GRANTS");

        // 5. Lifecycle state transitions: enable, disable, revocation
        Require(manager.SetEnabled("chatgpt-lock-chat-mode", false), "Disable must succeed");
        Require(!manager.GetManifest("chatgpt-lock-chat-mode")!.Enabled, "Manifest must be disabled");

        Require(manager.SetEnabled("chatgpt-lock-chat-mode", true), "Re-enable must succeed");
        Require(manager.GetManifest("chatgpt-lock-chat-mode")!.Enabled, "Manifest must be re-enabled");

        Require(manager.Revoke("chatgpt-lock-chat-mode"), "Revocation must succeed");
        var revokedManifest = manager.GetManifest("chatgpt-lock-chat-mode");
        Require(revokedManifest!.Revoked, "Manifest must report Revoked=true");
        Require(!revokedManifest.Enabled, "Revoked manifest must be disabled");

        Require(!manager.SetEnabled("chatgpt-lock-chat-mode", true), "Re-enabling revoked manifest must fail");
        var reRegisterRevoked = manager.VerifyAndRegister(revokedManifest, ManagedUserscriptManager.CanonicalChatLockScript);
        Require(!reRegisterRevoked.Success, "Registering revoked manifest must fail");
        Require(reRegisterRevoked.Status == "REFUSED_REVOKED", "Status must be REFUSED_REVOKED");

        // 6. Promotional UI and reversible rollback (T10)
        var initialPromo = manager.GetPromoStatus();
        Require(!initialPromo.SponsoredActive, "Initial promo must not be active");
        Require(initialPromo.KeyboardFocusable, "Keyboard focusable invariant required");
        Require(initialPromo.HighContrastCompatible, "High contrast compatibility required");

        manager.InjectPromoFixture();
        var activePromo = manager.GetPromoStatus();
        Require(activePromo.SponsoredActive, "Promo fixture must be active");
        Require(activePromo.ReversibleRestoreAvailable, "Reversible restore must be available");
        Require(activePromo.AccessibleLabel.StartsWith("Sponsored:", StringComparison.Ordinal), "Sponsored accessible label required");

        Require(manager.RestoreOrganic(), "RestoreOrganic must succeed");
        var restoredPromo = manager.GetPromoStatus();
        Require(!restoredPromo.SponsoredActive, "Restored state must not be active");
        Require(!restoredPromo.ReversibleRestoreAvailable, "Restore must not be available once organic is restored");

        await Task.CompletedTask;
    }
}
