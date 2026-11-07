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
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("revoked")] bool Revoked = false,
    [property: JsonPropertyName("promo_ui_supported")] bool PromoUiSupported = false);

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
/// Holds an inert synthetic-only placeholder; no privately qualified userscript is imported or installed.
/// </summary>
public sealed class ManagedUserscriptManager
{
    public const string Schema = "suprachat-userscript-manifest/v1";
    public const string Grade = "SYNTHETIC_CONTRACT_ONLY";
    public const string CanonicalOrigin = "https://chatgpt.com";

    // Synthetic-only placeholder. Not the private userscripts #97 artifact,
    // never a released, installed or automatically enabled userscript.
    public const string SyntheticChatLockFixture = """
        // ==UserScript==
        // @name         Synthetic Chat Mode Fixture — Not Installed
        // @namespace    https://github.com/SupraShellScripts/userscripts
        // @version      0.0.0
        // @description  Source-safe synthetic contract fixture without browser actions.
        // @match        https://chatgpt.com/*
        // @run-at       document-start
        // @grant        none
        // ==/UserScript==
        (() => { /* inert synthetic fixture */ })();
        """;

    private readonly object _gate = new();
    private readonly Dictionary<string, UserscriptManifest> _manifests = new(StringComparer.Ordinal);
    private bool _promoActive = false;

    public static ManagedUserscriptManager Default { get; } = CreateDefault();

    public static ManagedUserscriptManager CreateDefault()
    {
        var manager = new ManagedUserscriptManager();
        var canonicalHash = ComputeSha256(SyntheticChatLockFixture);

        var canonicalManifest = new UserscriptManifest(
            Id: "chatgpt-lock-chat-mode",
            Name: "Synthetic Chat Mode Placeholder (Not Installed)",
            Version: "0.0.0",
            Origin: CanonicalOrigin,
            RunAt: "document-start",
            ContentHash: canonicalHash,
            Grants: new[] { "none" },
            Enabled: false,
            Revoked: false,
            PromoUiSupported: false);

        manager.Register(canonicalManifest);
        return manager;
    }

    private void Register(UserscriptManifest manifest)
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

        // Exact HTTPS origin only. Prefix matching admits suffix-host impostors.
        if (!Uri.TryCreate(candidate.Origin, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(origin.IdnHost, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
            !origin.IsDefaultPort || !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            return new UserscriptVerificationResult(false, "REFUSED_ORIGIN_MISMATCH");
        }

        // Fail-closed zero-grant contract: do not use an incomplete blacklist.
        if (candidate.Grants is null || candidate.Grants.Count != 1 ||
            !string.Equals(candidate.Grants[0], "none", StringComparison.Ordinal))
        {
            return new UserscriptVerificationResult(false, "REFUSED_UNAUTHORIZED_GRANTS");
        }
        if (!string.Equals(candidate.RunAt, "document-start", StringComparison.Ordinal))
        {
            return new UserscriptVerificationResult(false, "REFUSED_RUN_AT");
        }

        // Cryptographic artifact-integrity check is not source authority.
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

            // No independently approved external userscript artifact digest,
            // human publication approval or native WebView execution receipt yet.
            // A caller-provided matching hash MUST NOT promote itself.
            return new UserscriptVerificationResult(false, "REFUSED_SOURCE_AUTHORITY_MISSING", computedHash);
        }
    }

    public bool SetEnabled(string scriptId, bool enabled)
    {
        lock (_gate)
        {
            if (!_manifests.TryGetValue(scriptId, out var existing))
                return false;

            if (existing.Revoked || enabled)
                return false; // no authorized source can be enabled in this synthetic-only stage

            _manifests[scriptId] = existing with { Enabled = false };
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
                AccessibleLabel: _promoActive
                    ? "Synthetic promo fixture active (no DOM observed)"
                    : "Synthetic promo fixture inactive (no DOM observed)",
                KeyboardFocusable: false,
                HighContrastCompatible: false,
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
