using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

public static class AuthModes
{
    public const string Subscription = "subscription";
    public const string Siwc = "siwc";
    public const string ApiKey = "api_key";

    public static readonly string[] DefaultOrder = { Subscription, Siwc, ApiKey };

    public static string NormalizeMode(string mode)
    {
        var m = mode.Trim().ToLowerInvariant().Replace('-', '_');
        return m switch
        {
            "subscription" or "chatgpt" or "sub" or "chatgpt_sub" => Subscription,
            "siwc" or "chatgpt_plan" or "plan" => Siwc,
            "api_key" or "apikey" or "key" => ApiKey,
            _ => throw new ArgumentException($"Unknown authentication mode '{mode}'. Supported modes: '{Subscription}', '{Siwc}', '{ApiKey}'.")
        };
    }
}

public sealed record AuthPolicy(
    string[] Order,
    bool PreferCodexAuthJson = true,
    string? CustomApiKeyEnv = null)
{
    public static readonly AuthPolicy Default = new(AuthModes.DefaultOrder, true, null);

    public static string[] NormalizeOrder(IEnumerable<string>? order)
    {
        if (order is null)
            return (string[])AuthModes.DefaultOrder.Clone();

        var list = new List<string>();
        foreach (var item in order)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            var normalized = AuthModes.NormalizeMode(item);
            if (!list.Contains(normalized, StringComparer.Ordinal))
                list.Add(normalized);
        }

        return list.Count > 0 ? list.ToArray() : (string[])AuthModes.DefaultOrder.Clone();
    }
}

public sealed record SubscriptionCredential(
    string AccessToken,
    string? RefreshToken = null,
    string? IdToken = null,
    string? AccountId = null,
    string? Email = null,
    DateTimeOffset? ExpiresAt = null,
    string Source = "subscription-store")
{
    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;

    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Email)
            ? $"ChatGPT subscription ({Email})"
            : !string.IsNullOrWhiteSpace(AccountId)
                ? $"ChatGPT subscription ({AccountId})"
                : "ChatGPT subscription";
}

public sealed record ApiKeyCredential(
    string ApiKey,
    string Source = "protected-api-key")
{
    public string RedactedKey
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ApiKey)) return "(empty)";
            if (ApiKey.Length <= 8) return "sk-...";
            return $"{ApiKey[..4]}...{ApiKey[^4..]}";
        }
    }

    public string DisplayLabel => $"API Key ({RedactedKey})";
}

public sealed record ResolvedAuthCredential(
    string AuthMode,
    string AccessToken,
    string PrincipalLabel,
    bool HasPlanUsage,
    string Source,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null,
    SiwcCredential? SiwcBacking = null,
    SubscriptionCredential? SubscriptionBacking = null,
    ApiKeyCredential? ApiKeyBacking = null);

public sealed record ProviderEvaluation(
    string AuthMode,
    bool Available,
    string? Reason,
    string? PrincipalLabel);

public sealed record AuthResolutionReport(
    string[] ConfiguredOrder,
    ResolvedAuthCredential? Resolved,
    IReadOnlyList<ProviderEvaluation> Evaluations);

public sealed class AuthResolutionException(AuthResolutionReport report)
    : InvalidOperationException(FormatMessage(report))
{
    public AuthResolutionReport Report { get; } = report;

    private static string FormatMessage(AuthResolutionReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Failed to resolve authentication credentials from configured provider order: " +
                      string.Join(" -> ", report.ConfiguredOrder));
        foreach (var ev in report.Evaluations)
        {
            sb.AppendLine($"  - [{ev.AuthMode}]: {(ev.Available ? "Available" : "Unavailable")} ({ev.Reason ?? "n/a"})");
        }
        sb.Append("Configure a valid authentication provider via GUI, CLI ('suprachat-cli auth-login'), or set OPENAI_API_KEY.");
        return sb.ToString();
    }
}

public static class AuthPolicyStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string PolicyPath => Path.Combine(AppState.DirectoryPath, "auth-policy.json");

    public static async Task<AuthPolicy> LoadAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(PolicyPath))
                return AuthPolicy.Default;

            var json = await File.ReadAllBytesAsync(PolicyPath).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var order = new List<string>();
            if (root.TryGetProperty("order", out var orderProp) && orderProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in orderProp.EnumerateArray())
                {
                    if (el.GetString() is { } str && !string.IsNullOrWhiteSpace(str))
                        order.Add(str);
                }
            }

            var preferCodex = !root.TryGetProperty("prefer_codex_auth_json", out var pcProp) || pcProp.GetBoolean();
            var customEnv = root.TryGetProperty("custom_api_key_env", out var envProp) ? envProp.GetString() : null;

            return new AuthPolicy(AuthPolicy.NormalizeOrder(order), preferCodex, customEnv);
        }
        catch
        {
            return AuthPolicy.Default;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task SaveAsync(AuthPolicy policy)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(AppState.DirectoryPath);
            var normalized = policy with { Order = AuthPolicy.NormalizeOrder(policy.Order) };
            var temp = PolicyPath + ".tmp";

            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("order");
                foreach (var mode in normalized.Order)
                    writer.WriteStringValue(mode);
                writer.WriteEndArray();
                writer.WriteBoolean("prefer_codex_auth_json", normalized.PreferCodexAuthJson);
                if (!string.IsNullOrWhiteSpace(normalized.CustomApiKeyEnv))
                    writer.WriteString("custom_api_key_env", normalized.CustomApiKeyEnv);
                writer.WriteEndObject();
            }

            AppState.TryRestrict(temp);
            File.Move(temp, PolicyPath, overwrite: true);
            AppState.TryRestrict(PolicyPath);
        }
        finally
        {
            Gate.Release();
        }
    }
}

public static class SubscriptionCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SemperSupra/SupraChat/Subscription/v1");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string SubscriptionPath => Path.Combine(AppState.DirectoryPath, "subscription-credential.dat");

    public static async Task<SubscriptionCredential?> TryLoadAsync(bool preferCodex = true)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 1. Try local protected store first
            var local = await ReadProtectedJsonAsync<SubscriptionCredential>(SubscriptionPath).ConfigureAwait(false);
            if (local is not null && !local.IsExpired)
                return local;

            // 2. Try ~/.codex/auth.json if preferred or local is missing
            if (preferCodex)
            {
                var codexCred = TryLoadFromCodex();
                if (codexCred is not null)
                    return codexCred;
            }

            return local;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static SubscriptionCredential? TryLoadFromCodex()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var codexAuthPath = Path.Combine(home, ".codex", "auth.json");
            if (!File.Exists(codexAuthPath))
                return null;

            var raw = File.ReadAllText(codexAuthPath);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var authMode = root.TryGetProperty("auth_mode", out var am) ? am.GetString() : null;
            if (!string.Equals(authMode, "chatgpt", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return null;

            var accessToken = tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            var refreshToken = tokens.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var idToken = tokens.TryGetProperty("id_token", out var it) ? it.GetString() : null;
            var accountId = tokens.TryGetProperty("account_id", out var aid) ? aid.GetString() : null;

            string? email = null;
            if (!string.IsNullOrWhiteSpace(idToken))
                email = ExtractEmailFromJwt(idToken);

            return new SubscriptionCredential(
                accessToken,
                refreshToken,
                idToken,
                accountId,
                email,
                ExpiresAt: null,
                Source: "codex-auth-json");
        }
        catch
        {
            return null;
        }
    }

    public static async Task SaveAsync(SubscriptionCredential credential)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteProtectedJsonAsync(SubscriptionPath, credential).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Clear()
    {
        if (File.Exists(SubscriptionPath))
            File.Delete(SubscriptionPath);
    }

    private static string? ExtractEmailFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<T?> ReadProtectedJsonAsync<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        var payload = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var json = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(payload, Entropy, DataProtectionScope.CurrentUser)
            : payload;

        return JsonSerializer.Deserialize<T>(json);
    }

    private static async Task WriteProtectedJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(AppState.DirectoryPath);
        var temp = path + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(
            value,
            new JsonSerializerOptions { WriteIndented = !OperatingSystem.IsWindows() });

        var payload = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser)
            : json;

        await File.WriteAllBytesAsync(temp, payload).ConfigureAwait(false);
        AppState.TryRestrict(temp);
        File.Move(temp, path, overwrite: true);
        AppState.TryRestrict(path);
    }
}

public static class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SemperSupra/SupraChat/ApiKey/v1");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string ApiKeyPath => Path.Combine(AppState.DirectoryPath, "api-key.dat");

    public static async Task<ApiKeyCredential?> TryLoadAsync(string? customEnvName = null)
    {
        // 1. Environment variable takes highest precedence for automation / headless CI
        var envVar = !string.IsNullOrWhiteSpace(customEnvName) ? customEnvName : "OPENAI_API_KEY";
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new ApiKeyCredential(fromEnv.Trim(), $"env:{envVar}");

        // 2. Protected store on disk
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var saved = await ReadProtectedJsonAsync<string>(ApiKeyPath).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(saved))
                return new ApiKeyCredential(saved.Trim(), "protected-api-key");
        }
        finally
        {
            Gate.Release();
        }

        // 3. Fallback: check ~/.codex/auth.json for OPENAI_API_KEY
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var codexAuthPath = Path.Combine(home, ".codex", "auth.json");
            if (File.Exists(codexAuthPath))
            {
                var raw = File.ReadAllText(codexAuthPath);
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("OPENAI_API_KEY", out var keyProp) &&
                    keyProp.ValueKind == JsonValueKind.String)
                {
                    var k = keyProp.GetString();
                    if (!string.IsNullOrWhiteSpace(k))
                        return new ApiKeyCredential(k.Trim(), "codex-auth-json");
                }
            }
        }
        catch { }

        return null;
    }

    public static async Task SaveAsync(string apiKey)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteProtectedJsonAsync(ApiKeyPath, apiKey.Trim()).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Clear()
    {
        if (File.Exists(ApiKeyPath))
            File.Delete(ApiKeyPath);
    }

    private static async Task<T?> ReadProtectedJsonAsync<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        var payload = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var json = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(payload, Entropy, DataProtectionScope.CurrentUser)
            : payload;

        return JsonSerializer.Deserialize<T>(json);
    }

    private static async Task WriteProtectedJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(AppState.DirectoryPath);
        var temp = path + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(
            value,
            new JsonSerializerOptions { WriteIndented = !OperatingSystem.IsWindows() });

        var payload = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser)
            : json;

        await File.WriteAllBytesAsync(temp, payload).ConfigureAwait(false);
        AppState.TryRestrict(temp);
        File.Move(temp, path, overwrite: true);
        AppState.TryRestrict(path);
    }
}

public static class AuthResolver
{
    public static async Task<ResolvedAuthCredential> ResolveAsync(
        AuthPolicy? policy = null,
        bool requirePlanUsage = false,
        SiwcClient? siwcClient = null,
        CancellationToken cancellationToken = default)
    {
        var report = await EvaluateAsync(policy, requirePlanUsage, siwcClient, cancellationToken).ConfigureAwait(false);
        return report.Resolved ?? throw new AuthResolutionException(report);
    }

    public static async Task<AuthResolutionReport> EvaluateAsync(
        AuthPolicy? policy = null,
        bool requirePlanUsage = false,
        SiwcClient? siwcClient = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= await AuthPolicyStore.LoadAsync().ConfigureAwait(false);
        var evaluations = new List<ProviderEvaluation>();
        ResolvedAuthCredential? selected = null;

        foreach (var mode in policy.Order)
        {
            switch (mode)
            {
                case AuthModes.Subscription:
                {
                    var sub = await SubscriptionCredentialStore.TryLoadAsync(policy.PreferCodexAuthJson).ConfigureAwait(false);
                    if (sub is not null && !string.IsNullOrWhiteSpace(sub.AccessToken) && !sub.IsExpired)
                    {
                        var resolved = new ResolvedAuthCredential(
                            AuthModes.Subscription,
                            sub.AccessToken,
                            sub.DisplayLabel,
                            HasPlanUsage: true,
                            sub.Source,
                            sub.RefreshToken,
                            sub.ExpiresAt,
                            SubscriptionBacking: sub);

                        evaluations.Add(new ProviderEvaluation(
                            AuthModes.Subscription,
                            Available: true,
                            Reason: $"Valid subscription session from {sub.Source}",
                            sub.DisplayLabel));

                        selected ??= resolved;
                    }
                    else
                    {
                        evaluations.Add(new ProviderEvaluation(
                            AuthModes.Subscription,
                            Available: false,
                            Reason: sub is null ? "No subscription tokens found" : "Subscription token expired",
                            PrincipalLabel: null));
                    }
                    break;
                }

                case AuthModes.Siwc:
                {
                    var siwc = await CredentialStore.TryLoadAsync().ConfigureAwait(false);
                    if (siwc is not null && !string.IsNullOrWhiteSpace(siwc.AccessToken))
                    {
                        if (siwcClient is not null && siwc.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                        {
                            try
                            {
                                siwc = await siwcClient.RefreshIfNeededAsync(siwc).ConfigureAwait(false);
                                await CredentialStore.SaveAsync(siwc).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                evaluations.Add(new ProviderEvaluation(
                                    AuthModes.Siwc,
                                    Available: false,
                                    Reason: $"SIWC refresh failed: {ex.Message}",
                                    PrincipalLabel: null));
                                break;
                            }
                        }

                        if (requirePlanUsage && !siwc.HasPlanUsage)
                        {
                            evaluations.Add(new ProviderEvaluation(
                                AuthModes.Siwc,
                                Available: false,
                                Reason: $"SIWC credential present but lacks required '{SiwcProtocol.RequiredPlanScope}' scope",
                                PrincipalLabel: siwc.Email ?? siwc.Subject));
                        }
                        else
                        {
                            var resolved = new ResolvedAuthCredential(
                                AuthModes.Siwc,
                                siwc.AccessToken,
                                siwc.Email ?? $"SIWC client {siwc.ClientId[..Math.Min(siwc.ClientId.Length, 8)]}",
                                HasPlanUsage: siwc.HasPlanUsage,
                                Source: "siwc-store",
                                siwc.RefreshToken,
                                siwc.ExpiresAt,
                                SiwcBacking: siwc);

                            evaluations.Add(new ProviderEvaluation(
                                AuthModes.Siwc,
                                Available: true,
                                Reason: "Active SIWC token",
                                resolved.PrincipalLabel));

                            selected ??= resolved;
                        }
                    }
                    else
                    {
                        evaluations.Add(new ProviderEvaluation(
                            AuthModes.Siwc,
                            Available: false,
                            Reason: "No SIWC authorization saved",
                            PrincipalLabel: null));
                    }
                    break;
                }

                case AuthModes.ApiKey:
                {
                    var apiKey = await ApiKeyStore.TryLoadAsync(policy.CustomApiKeyEnv).ConfigureAwait(false);
                    if (apiKey is not null && !string.IsNullOrWhiteSpace(apiKey.ApiKey))
                    {
                        if (requirePlanUsage)
                        {
                            evaluations.Add(new ProviderEvaluation(
                                AuthModes.ApiKey,
                                Available: false,
                                Reason: "API keys provide direct platform API inference, not ChatGPT-plan subscription entitlement",
                                PrincipalLabel: apiKey.DisplayLabel));
                        }
                        else
                        {
                            var resolved = new ResolvedAuthCredential(
                                AuthModes.ApiKey,
                                apiKey.ApiKey,
                                apiKey.DisplayLabel,
                                HasPlanUsage: false,
                                Source: apiKey.Source,
                                ApiKeyBacking: apiKey);

                            evaluations.Add(new ProviderEvaluation(
                                AuthModes.ApiKey,
                                Available: true,
                                Reason: $"API key available from {apiKey.Source}",
                                apiKey.DisplayLabel));

                            selected ??= resolved;
                        }
                    }
                    else
                    {
                        evaluations.Add(new ProviderEvaluation(
                            AuthModes.ApiKey,
                            Available: false,
                            Reason: $"No API key found in protected store or environment variable ({policy.CustomApiKeyEnv ?? "OPENAI_API_KEY"})",
                            PrincipalLabel: null));
                    }
                    break;
                }
            }
        }

        return new AuthResolutionReport(policy.Order, selected, evaluations);
    }
}
