using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupraChat.Core;

public sealed record PortableCredentialBundleEnvelope(
    string Schema,
    string Mode,
    DateTimeOffset ExportedAt,
    string ClientIdSuffix,
    string Kdf,
    int KdfIterations,
    string Cipher,
    string Salt,
    string Nonce,
    string Tag,
    string Ciphertext);

public sealed record PortableCredentialBundleSummary(
    string Schema,
    string Mode,
    DateTimeOffset ExportedAt,
    string ClientIdSuffix,
    string Cipher);

public static class PortableCredentialBundle
{
    public const string Schema = "suprachat-siwc-credential-bundle/v1";
    public const string CopyMode = "copy";
    public const int KdfIterations = 210_000;

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static PortableCredentialBundleEnvelope Protect(
        SiwcCredential credential,
        string passphrase,
        string mode = CopyMode,
        DateTimeOffset? exportedAt = null)
    {
        ValidatePassphrase(passphrase);
        if (!string.Equals(mode, CopyMode, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(mode), "Only explicit copy-mode export is qualified in this tranche.");

        var timestamp = exportedAt ?? DateTimeOffset.UtcNow;
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
        var ciphertext = new byte[plaintext.Length];
        var key = DeriveKey(passphrase, salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(mode, timestamp));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return new(
            Schema,
            mode,
            timestamp,
            Suffix(credential.ClientId),
            "pbkdf2-sha256",
            KdfIterations,
            "aes-256-gcm",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(ciphertext));
    }

    public static SiwcCredential Unprotect(
        PortableCredentialBundleEnvelope envelope,
        string passphrase)
    {
        ValidateEnvelope(envelope);
        ValidatePassphrase(passphrase);

        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(passphrase, salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                AssociatedData(envelope.Mode, envelope.ExportedAt));

            return JsonSerializer.Deserialize<SiwcCredential>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Credential bundle payload is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static PortableCredentialBundleSummary Inspect(PortableCredentialBundleEnvelope envelope)
    {
        ValidateEnvelope(envelope);
        return new(
            envelope.Schema,
            envelope.Mode,
            envelope.ExportedAt,
            envelope.ClientIdSuffix,
            envelope.Cipher);
    }

    public static async Task WriteAsync(
        string path,
        SiwcCredential credential,
        string passphrase,
        string mode = CopyMode)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Credential bundle output path is required.", nameof(path));

        var envelope = Protect(credential, passphrase, mode);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        var temp = fullPath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

        await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
        AppState.TryRestrict(temp);
        File.Move(temp, fullPath, overwrite: true);
        AppState.TryRestrict(fullPath);
    }

    public static async Task<PortableCredentialBundleEnvelope> ReadEnvelopeAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Credential bundle path is required.", nameof(path));

        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(path)).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<PortableCredentialBundleEnvelope>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Credential bundle is empty.");
        ValidateEnvelope(envelope);
        return envelope;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            KdfIterations,
            HashAlgorithmName.SHA256,
            KeySize);

    private static byte[] AssociatedData(string mode, DateTimeOffset exportedAt) =>
        Encoding.UTF8.GetBytes($"{Schema}\n{mode}\n{exportedAt.ToUniversalTime():O}");

    private static string Suffix(string clientId) =>
        clientId[^Math.Min(clientId.Length, 12)..];

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
            throw new ArgumentException(
                "Portable credential bundles require a passphrase of at least 12 characters.",
                nameof(passphrase));
    }

    private static void ValidateEnvelope(PortableCredentialBundleEnvelope envelope)
    {
        if (!string.Equals(envelope.Schema, Schema, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported credential bundle schema: {envelope.Schema}");
        if (!string.Equals(envelope.Mode, CopyMode, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported credential bundle mode: {envelope.Mode}");
        if (!string.Equals(envelope.Kdf, "pbkdf2-sha256", StringComparison.Ordinal) ||
            envelope.KdfIterations != KdfIterations)
            throw new InvalidDataException("Unsupported credential bundle KDF.");
        if (!string.Equals(envelope.Cipher, "aes-256-gcm", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported credential bundle cipher.");
    }
}
