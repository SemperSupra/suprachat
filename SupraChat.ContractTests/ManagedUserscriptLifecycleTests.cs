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
        Require(!defaultManifest!.Enabled, "Synthetic placeholder must start disabled");
        Require(!defaultManifest.PromoUiSupported, "Synthetic placeholder cannot claim UI support");
        Require(ManagedUserscriptManager.Grade == "SYNTHETIC_CONTRACT_ONLY", "Synthetic evidence grade required");
        Require(!ManagedUserscriptManager.SyntheticChatLockFixture.Contains("userscripts-private", StringComparison.OrdinalIgnoreCase), "Public fixture must not reference private source");
        Require(!defaultManifest.Revoked, "Default manifest must not be revoked initially");

        var verifyResult = manager.VerifyAndRegister(defaultManifest, ManagedUserscriptManager.SyntheticChatLockFixture);
        Require(!verifyResult.Success, "A self-declared hash cannot authorize userscript registration");
        Require(verifyResult.Status == "REFUSED_SOURCE_AUTHORITY_MISSING", "Missing independent source authority must be explicit");

        // 2. Tampered script content -> REFUSED_HASH_MISMATCH
        var tamperedScript = ManagedUserscriptManager.SyntheticChatLockFixture + "\n// unauthorized edit";
        var tamperedResult = manager.VerifyAndRegister(defaultManifest, tamperedScript);
        Require(!tamperedResult.Success, "Tampered script must be refused");
        Require(tamperedResult.Status == "REFUSED_HASH_MISMATCH", "Status must be REFUSED_HASH_MISMATCH");

        // 3. Origin mismatch -> REFUSED_ORIGIN_MISMATCH
        var spoofedManifest = defaultManifest with
        {
            Id = "spoofed-script",
            Origin = "https://evil.chatgpt.com"
        };
        var spoofResult = manager.VerifyAndRegister(spoofedManifest, ManagedUserscriptManager.SyntheticChatLockFixture);
        Require(!spoofResult.Success, "Spoofed origin must be refused");
        Require(spoofResult.Status == "REFUSED_ORIGIN_MISMATCH", "Status must be REFUSED_ORIGIN_MISMATCH");

        var suffixHost = spoofedManifest with { Origin = "https://chatgpt.com.evil.example" };
        var suffixResult = manager.VerifyAndRegister(suffixHost, ManagedUserscriptManager.SyntheticChatLockFixture);
        Require(!suffixResult.Success && suffixResult.Status == "REFUSED_ORIGIN_MISMATCH",
            "Origin prefix spoofing must be refused");

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
        var unlistedGrant = grantManifest with { Grants = new[] { "GM_download" } };
        var unlistedResult = manager.VerifyAndRegister(unlistedGrant, grantScript);
        Require(!unlistedResult.Success && unlistedResult.Status == "REFUSED_UNAUTHORIZED_GRANTS", "Unknown grant must be refused");
        var nullGrant = grantManifest with { Grants = null! };
        var nullResult = manager.VerifyAndRegister(nullGrant, grantScript);
        Require(!nullResult.Success && nullResult.Status == "REFUSED_UNAUTHORIZED_GRANTS", "Null grants must fail closed");

        // 5. Lifecycle state transitions: enable, disable, revocation
        Require(manager.SetEnabled("chatgpt-lock-chat-mode", false), "Disable must succeed");
        Require(!manager.GetManifest("chatgpt-lock-chat-mode")!.Enabled, "Manifest must be disabled");

        Require(!manager.SetEnabled("chatgpt-lock-chat-mode", true), "Unqualified placeholder must not be enabled");
        Require(!manager.GetManifest("chatgpt-lock-chat-mode")!.Enabled, "Unqualified placeholder remains disabled");

        Require(manager.Revoke("chatgpt-lock-chat-mode"), "Revocation must succeed");
        var revokedManifest = manager.GetManifest("chatgpt-lock-chat-mode");
        Require(revokedManifest!.Revoked, "Manifest must report Revoked=true");
        Require(!revokedManifest.Enabled, "Revoked manifest must be disabled");

        Require(!manager.SetEnabled("chatgpt-lock-chat-mode", true), "Re-enabling revoked manifest must fail");
        var reRegisterRevoked = manager.VerifyAndRegister(revokedManifest, ManagedUserscriptManager.SyntheticChatLockFixture);
        Require(!reRegisterRevoked.Success, "Registering revoked manifest must fail");
        Require(reRegisterRevoked.Status == "REFUSED_REVOKED", "Status must be REFUSED_REVOKED");

        // 6. Promotional UI and reversible rollback (T10)
        var initialPromo = manager.GetPromoStatus();
        Require(!initialPromo.SponsoredActive, "Initial promo must not be active");
        Require(!initialPromo.KeyboardFocusable, "Synthetic fixture cannot assert keyboard focus");
        Require(!initialPromo.HighContrastCompatible, "Synthetic fixture cannot assert contrast compliance");

        manager.InjectPromoFixture();
        var activePromo = manager.GetPromoStatus();
        Require(activePromo.SponsoredActive, "Promo fixture must be active");
        Require(activePromo.ReversibleRestoreAvailable, "Reversible restore must be available");
        Require(activePromo.AccessibleLabel.StartsWith("Synthetic promo fixture", StringComparison.Ordinal), "Synthetic-only status label required");

        Require(manager.RestoreOrganic(), "RestoreOrganic must succeed");
        var restoredPromo = manager.GetPromoStatus();
        Require(!restoredPromo.SponsoredActive, "Restored state must not be active");
        Require(!restoredPromo.ReversibleRestoreAvailable, "Restore must not be available once organic is restored");

        await Task.CompletedTask;
    }
}
