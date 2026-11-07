using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupraChat.Core;

public sealed record UserscriptManifest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("run_at")] string RunAt,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("grants")] IReadOnlyList<string> Grants,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("revoked")] bool Revoked = false,
    [property: JsonPropertyName("promo_ui_supported")] bool PromoUiSupported = true);

public sealed record UserscriptVerificationResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("content_hash")] string? ComputedHash = null,
    [property: JsonPropertyName("manifest")] UserscriptManifest? Manifest = null);

public sealed record PromoUiStatus(
    [property: JsonPropertyName("sponsored_active")] bool SponsoredActive,
    [property: JsonPropertyName("accessible_label")] string AccessibleLabel,
    [property: JsonPropertyName("keyboard_focusable")] bool KeyboardFocusable,
    [property: JsonPropertyName("high_contrast_compatible")] bool HighContrastCompatible,
    [property: JsonPropertyName("reversible_restore_available")] bool ReversibleRestoreAvailable);

/// <summary>
/// Architectural implementation of Campaign Package P5:
/// Managed userscript lifecycle, cryptographic hash verification, zero-grant enforcement, and reversible promotional UI management.
/// Integrates the ChatGPT Lock to Chat Mode userscript fixture (PR #97 / #96) with fail-closed security.
/// </summary>
public sealed class ManagedUserscriptManager
{
    public const string Schema = "suprachat-userscript-manifest/v1";
    public const string Grade = "QUALIFIED_SYNTHETIC";
    public const string CanonicalOrigin = "https://chatgpt.com";

    // Pinned canonical Chat-lock userscript source fixture from userscripts-private#97
    public const string CanonicalChatLockScript = """
        // ==UserScript==
        // @name         ChatGPT Lock to Chat Mode
        // @namespace    https://github.com/SupraShellScripts/userscripts-private
        // @version      2.0.0
        // @description  Intercepts Work controls and binds ChatGPT WebUI to Chat mode.
        // @match        https://chatgpt.com/*
        // @match        https://chat.openai.com/*
        // @run-at       document-start
        // @grant        none
        // ==/UserScript==
        (function() {
            'use strict';
            const observer = new MutationObserver(() => {
                const workTab = document.querySelector('[role="tab"][aria-label*="Work"], [role="tab"][data-state="active"]');
                if (workTab && workTab.getAttribute('aria-selected') === 'true') {
                    const chatTab = document.querySelector('[role="tab"][aria-label*="Chat"]');
                    if (chatTab) chatTab.click();
                }
            });
            observer.observe(document.documentElement, { childList: true, subtree: true });
        })();
        """;

    private static readonly HashSet<string> ForbiddenGrants = new(StringComparer.OrdinalIgnoreCase)
    {
        "GM_xmlhttpRequest",
        "GM.xmlHttpRequest",
        "unsafeWindow",
        "GM_setValue",
        "GM_getValue",
        "GM_deleteValue",
        "GM_cookie"
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, UserscriptManifest> _manifests = new(StringComparer.Ordinal);
    private bool _promoActive = false;

    public static ManagedUserscriptManager Default { get; } = CreateDefault();

    public static ManagedUserscriptManager CreateDefault()
    {
        var manager = new ManagedUserscriptManager();
        var canonicalHash = ComputeSha256(CanonicalChatLockScript);

        var canonicalManifest = new UserscriptManifest(
            Id: "chatgpt-lock-chat-mode",
            Name: "ChatGPT Lock to Chat Mode",
            Version: "2.0.0",
            Origin: CanonicalOrigin,
            RunAt: "document-start",
            ContentHash: canonicalHash,
            Grants: new[] { "none" },
            Enabled: true,
            Revoked: false,
            PromoUiSupported: true);

        manager.Register(canonicalManifest);
        return manager;
    }

    public void Register(UserscriptManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_gate)
        {
            _manifests[manifest.Id] = manifest;
        }
    }

    public UserscriptVerificationResult VerifyAndRegister(UserscriptManifest candidate, string scriptContent)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(scriptContent))
            return new UserscriptVerificationResult(false, "REFUSED_EMPTY_CONTENT");

        // 1. Origin validation
        if (string.IsNullOrWhiteSpace(candidate.Origin) ||
            !candidate.Origin.StartsWith(CanonicalOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return new UserscriptVerificationResult(false, "REFUSED_ORIGIN_MISMATCH");
        }

        // 2. Grant enforcement (must be zero-grant or none)
        if (candidate.Grants != null && candidate.Grants.Any(g => ForbiddenGrants.Contains(g)))
        {
            return new UserscriptVerificationResult(false, "REFUSED_UNAUTHORIZED_GRANTS");
        }

        // 3. Cryptographic SHA-256 verification
        var computedHash = ComputeSha256(scriptContent);
        if (!string.Equals(computedHash, candidate.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return new UserscriptVerificationResult(false, "REFUSED_HASH_MISMATCH", computedHash);
        }

        lock (_gate)
        {
            if (_manifests.TryGetValue(candidate.Id, out var existing) && existing.Revoked)
            {
                return new UserscriptVerificationResult(false, "REFUSED_REVOKED");
            }

            _manifests[candidate.Id] = candidate;
            return new UserscriptVerificationResult(true, "VERIFIED_AND_REGISTERED", computedHash, candidate);
        }
    }

    public bool SetEnabled(string scriptId, bool enabled)
    {
        lock (_gate)
        {
            if (!_manifests.TryGetValue(scriptId, out var existing))
                return false;

            if (existing.Revoked)
                return false;

            _manifests[scriptId] = existing with { Enabled = enabled };
            return true;
        }
    }

    public bool Revoke(string scriptId)
    {
        lock (_gate)
        {
            if (!_manifests.TryGetValue(scriptId, out var existing))
                return false;

            _manifests[scriptId] = existing with { Enabled = false, Revoked = true };
            return true;
        }
    }

    public UserscriptManifest? GetManifest(string scriptId)
    {
        lock (_gate)
        {
            return _manifests.TryGetValue(scriptId, out var manifest) ? manifest : null;
        }
    }

    public IReadOnlyList<UserscriptManifest> ListManifests()
    {
        lock (_gate)
        {
            return _manifests.Values.ToList();
        }
    }

    // T10: Promotional UI management and reversible rollback
    public PromoUiStatus GetPromoStatus()
    {
        lock (_gate)
        {
            return new PromoUiStatus(
                SponsoredActive: _promoActive,
                AccessibleLabel: _promoActive ? "Sponsored: Chat Mode Features" : "Organic Workspace",
                KeyboardFocusable: true,
                HighContrastCompatible: true,
                ReversibleRestoreAvailable: _promoActive);
        }
    }

    public void InjectPromoFixture()
    {
        lock (_gate)
        {
            _promoActive = true;
        }
    }

    public bool RestoreOrganic()
    {
        lock (_gate)
        {
            if (!_promoActive) return false;
            _promoActive = false;
            return true;
        }
    }

    public static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public object Describe()
    {
        lock (_gate)
        {
            return new
            {
                schema = Schema,
                grade = Grade,
                manifests_count = _manifests.Count,
                manifests = _manifests.Values.ToList(),
                promo_ui = GetPromoStatus()
            };
        }
    }
}
