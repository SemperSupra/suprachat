using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

public sealed record SiwcRegistration(
    string ClientId,
    string HostId,
    string Subject,
    string? Email,
    DateTimeOffset UpdatedAt)
{
    public string DisplayLabel
    {
        get
        {
            var suffix = ClientId[..Math.Min(ClientId.Length, 12)];
            return string.IsNullOrWhiteSpace(Email)
                ? $"ChatGPT account · {suffix}"
                : $"{Email} · {suffix}";
        }
    }

    public override string ToString() => DisplayLabel;
}

public static class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SemperSupra/SupraChat/SIWC/v1");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string PathName => Path.Combine(AppState.DirectoryPath, "siwc-credential.dat");
    private static string RegistrationsPath => Path.Combine(AppState.DirectoryPath, "siwc-registrations.dat");
    private static string LeasePath => Path.Combine(AppState.DirectoryPath, "siwc-credential.lock");

    public static async Task SaveAsync(SiwcCredential credential)
    {
        await Gate.WaitAsync();
        try
        {
            var normalized = credential.Generation < 1
                ? credential with { Generation = 1 }
                : credential;

            var current = await ReadProtectedJsonAsync<SiwcCredential>(PathName);
            if (current is not null &&
                string.Equals(current.ClientId, normalized.ClientId, StringComparison.Ordinal) &&
                string.Equals(current.Subject, normalized.Subject, StringComparison.Ordinal) &&
                Math.Max(1, current.Generation) > normalized.Generation)
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite SIWC credential generation {current.Generation} " +
                    $"with stale generation {normalized.Generation}.");
            }

            await WriteProtectedJsonAsync(PathName, normalized);
            await RememberRegistrationUnlockedAsync(normalized);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<SiwcCredential?> TryLoadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var credential = await ReadProtectedJsonAsync<SiwcCredential>(PathName);
            return credential is { Generation: < 1 }
                ? credential with { Generation = 1 }
                : credential;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<IAsyncDisposable> AcquireExclusiveCredentialLeaseAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppState.DirectoryPath);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    LeasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                return new CredentialLease(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the local SIWC credential lease.");
        }
    }

    public static async Task<IReadOnlyList<SiwcRegistration>> ListRegistrationsAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var registrations =
                await ReadProtectedJsonAsync<List<SiwcRegistration>>(RegistrationsPath)
                ?? new List<SiwcRegistration>();
            return registrations
                .OrderBy(x => x.Email ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ClientId, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<SiwcRegistration?> FindRegistrationAsync(string clientId)
    {
        var registrations = await ListRegistrationsAsync();
        return registrations.FirstOrDefault(x =>
            string.Equals(x.ClientId, clientId, StringComparison.Ordinal));
    }

    public static async Task RememberRegistrationAsync(SiwcCredential credential)
    {
        await Gate.WaitAsync();
        try
        {
            await RememberRegistrationUnlockedAsync(credential);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task RememberRegistrationUnlockedAsync(SiwcCredential credential)
    {
        var registrations =
            await ReadProtectedJsonAsync<List<SiwcRegistration>>(RegistrationsPath)
            ?? new List<SiwcRegistration>();

        var next = new SiwcRegistration(
            credential.ClientId,
            credential.HostId,
            credential.Subject,
            credential.Email,
            DateTimeOffset.UtcNow);

        var index = registrations.FindIndex(x =>
            string.Equals(x.ClientId, credential.ClientId, StringComparison.Ordinal));
        if (index >= 0)
            registrations[index] = next;
        else
            registrations.Add(next);

        await WriteProtectedJsonAsync(RegistrationsPath, registrations);
    }

    public static void Clear()
    {
        // Signing out clears the renewable session locally but deliberately
        // retains the client registration so a later sign-in reuses the issued
        // client_id instead of silently creating a new registration.
        if (File.Exists(PathName))
            File.Delete(PathName);
    }

    public static void ForgetRegistration(string clientId)
    {
        Gate.Wait();
        try
        {
            var registrations =
                ReadProtectedJsonAsync<List<SiwcRegistration>>(RegistrationsPath)
                    .GetAwaiter().GetResult()
                ?? new List<SiwcRegistration>();

            registrations.RemoveAll(x =>
                string.Equals(x.ClientId, clientId, StringComparison.Ordinal));
            WriteProtectedJsonAsync(RegistrationsPath, registrations)
                .GetAwaiter().GetResult();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<T?> ReadProtectedJsonAsync<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        var payload = await File.ReadAllBytesAsync(path);
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

        await File.WriteAllBytesAsync(temp, payload);
        AppState.TryRestrict(temp);
        File.Move(temp, path, overwrite: true);
        AppState.TryRestrict(path);
    }

    private sealed class CredentialLease(FileStream stream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await stream.DisposeAsync().ConfigureAwait(false);
    }
}
