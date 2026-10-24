using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SupraChat.Automation;

namespace SupraChat.PeerLedgerRdte;

/// <summary>Disposable Windows custody candidate. Does not establish human identity or resist the owner/admin.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsOwnerCustody : IDisposable
{
    private const string Prefix = "suprachat-w9-owned-custody-";
    private readonly SafeFileHandle _directory;
    private readonly bool _fresh;
    private bool _initialized;
    public string Root { get; }
    internal string IssuerPath => Path.Combine(Root, "issuer.dpapi");

    private WindowsOwnerCustody(string root, bool fresh)
    {
        Root = Path.GetFullPath(root);
        if (!Path.GetFileName(Root).StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(Root)[Prefix.Length..], "N", out _))
            throw new UnauthorizedAccessException("CUSTODY_ROOT_REFUSED");
        RefuseReparseAncestors(Root);
        _directory = CustodyNative.OpenMetadata(Root, directory: true, denyDelete: true);
        try { Verify(); } catch { _directory.Dispose(); throw; }
        _fresh = fresh;
    }

    public static WindowsOwnerCustody Create()
    {
        var root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        RefuseReparseAncestors(Path.GetDirectoryName(root)!);
        CustodyNative.CreateDirectoryOnce(root, OwnerSecurity().GetSecurityDescriptorBinaryForm());
        return new(root, fresh: true);
    }

    public static WindowsOwnerCustody Open(string root) => new(root, fresh: false);

    internal static DirectorySecurity OwnerSecurity()
    {
        using var owner = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(owner.User!);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { owner.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static void RefuseReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (!current.Exists || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("CUSTODY_REPARSE_PATH_REFUSED");
    }

    public void Verify()
    {
        RefuseReparseAncestors(Root);
        CustodyNative.VerifyHandle(_directory, Root, directory: true);
    }

    public IssuerMaterial InitializeIssuer()
    {
        Verify();
        if (!_fresh || _initialized) throw new UnauthorizedAccessException("CUSTODY_INITIALIZATION_REFUSED");
        _initialized = true; // Failure never admits an implicit reset in this owner instance.
        var plain = new byte[52];
        Encoding.ASCII.GetBytes("SC14").CopyTo(plain, 0);
        Guid.NewGuid().TryWriteBytes(plain.AsSpan(4, 16));
        RandomNumberGenerator.Fill(plain.AsSpan(20, 32));
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy(), DataProtectionScope.CurrentUser);
            using (var file = CustodyNative.WithUserFileOwner(() => new FileStream(IssuerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)))
            {
                CustodyNative.VerifyHandle(file.SafeFileHandle, IssuerPath, directory: false);
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
                CustodyNative.VerifyHandle(file.SafeFileHandle, IssuerPath, directory: false);
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return LoadIssuer();
    }

    public IssuerMaterial LoadIssuer()
    {
        Verify();
        using var handle = CustodyNative.OpenMetadata(IssuerPath, directory: false, denyDelete: true, readData: true);
        CustodyNative.VerifyHandle(handle, IssuerPath, directory: false);
        using var file = new FileStream(handle, FileAccess.Read);
        if (file.Length is < 64 or > 8192) throw new InvalidDataException("CUSTODY_KEY_SIZE_REFUSED");
        var encrypted = new byte[checked((int)file.Length)];
        file.ReadExactly(encrypted);
        CustodyNative.VerifyHandle(handle, IssuerPath, directory: false);
        var plain = ProtectedData.Unprotect(encrypted, Entropy(), DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length != 52 || !plain.AsSpan(0, 4).SequenceEqual("SC14"u8))
                throw new InvalidDataException("CUSTODY_KEY_FORMAT_REFUSED");
            return new(plain.AsSpan(20, 32));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private byte[] Entropy() => SHA256.HashData(Encoding.UTF8.GetBytes("suprachat-synthetic-custody/v1:" + Path.GetFileName(Root)));

    internal static void VerifyStore(string path, SafeFileHandle? ownerLock = null)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using var custody = Open(parent);
        foreach (var suffix in new[] { "", "-wal", "-shm", ".w9owner" })
        {
            var item = path + suffix;
            if (suffix == ".w9owner" && ownerLock is not null)
            { CustodyNative.VerifyHandle(ownerLock, item, directory: false); continue; }
            if (!File.Exists(item)) continue;
            using var handle = CustodyNative.OpenMetadata(item, directory: false, denyDelete: false);
            CustodyNative.VerifyHandle(handle, item, directory: false);
        }
    }

    public void Dispose() => _directory.Dispose();

    internal sealed class IssuerMaterial(ReadOnlySpan<byte> key) : IDisposable
    {
        private readonly byte[] _key = key.ToArray();
        private bool _disposed;
        public W12ScopedGrantAuthority CreateAuthority(Func<DateTimeOffset>? clock = null)
        { ObjectDisposedException.ThrowIf(_disposed, this); return new(_key, clock); }
        // Fixed-domain proof is not a grant signature or a general signing endpoint.
        public byte[] RestartProof()
        { ObjectDisposedException.ThrowIf(_disposed, this); return HMACSHA256.HashData(_key, "suprachat-custody-restart-proof/v1"u8); }
        public void Dispose() { _disposed = true; CryptographicOperations.ZeroMemory(_key); }
    }
}

[SupportedOSPlatform("windows")]
internal static class CustodyNative
{
    // Scope ONLY synchronous filesystem work. RunAsClient reverts impersonation and must not run inside this scope.
    internal static T WithUserFileOwner<T>(Func<T> action)
    {
        using var original = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
        if (!DuplicateTokenEx(original.AccessToken, 0x8E, IntPtr.Zero, 2, 2, out var copy))
            throw new UnauthorizedAccessException("CUSTODY_OWNER_TOKEN_REFUSED");
        using (copy)
        {
            var bytes = new byte[original.User!.BinaryLength]; original.User.GetBinaryForm(bytes, 0);
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var owner = pin.AddrOfPinnedObject();
                if (!SetTokenInformation(copy, 4, ref owner, IntPtr.Size))
                    throw new UnauthorizedAccessException("CUSTODY_OWNER_TOKEN_REFUSED");
            }
            finally { pin.Free(); }
            return WindowsIdentity.RunImpersonated(copy, () =>
            {
                using var current = WindowsIdentity.GetCurrent();
                if (current.User != original.User || current.Owner != current.User)
                    throw new UnauthorizedAccessException("CUSTODY_OWNER_TOKEN_REFUSED");
                return action();
            });
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    internal static void CreateDirectoryOnce(string path, byte[] descriptor)
    {
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            if (!WithUserFileOwner(() => CreateDirectoryW(path, ref attributes))) throw new IOException("CUSTODY_CREATE_NEW_REFUSED");
        }
        finally { pin.Free(); }
    }

    internal static SafeFileHandle OpenMetadata(string path, bool directory, bool denyDelete, bool readData = false)
    {
        var handle = CreateFileW(path, 0x20080u | (readData ? 0x80000000u : 0),
            denyDelete ? 3u : 7u, IntPtr.Zero, 3, 0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new UnauthorizedAccessException("CUSTODY_HANDLE_REFUSED"); }
        return handle;
    }

    internal static void VerifyHandle(SafeFileHandle handle, string expected, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
            throw new UnauthorizedAccessException("CUSTODY_OBJECT_REFUSED");
        var path = new StringBuilder(32768);
        var size = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (size == 0 || size >= path.Capacity || !string.Equals(path.ToString(), @"\\?\" + Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CUSTODY_FINAL_PATH_REFUSED");
        if (GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var descriptor) != 0)
            throw new UnauthorizedAccessException("CUSTODY_SECURITY_UNAVAILABLE");
        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is < 20 or > 65536) throw new UnauthorizedAccessException("CUSTODY_SECURITY_SIZE_REFUSED");
            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            using var owner = WindowsIdentity.GetCurrent();
            var allowed = new HashSet<SecurityIdentifier> { owner.User!, new(WellKnownSidType.LocalSystemSid, null) };
            if (security.Owner != owner.User || security.DiscretionaryAcl is not { } acl || acl.Count != allowed.Count ||
                (directory && !security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)))
                throw new UnauthorizedAccessException("CUSTODY_OWNER_ACL_REFUSED");
            foreach (GenericAce entry in acl)
                if (entry is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed ||
                    ace.AccessMask != (int)FileSystemRights.FullControl || ace.AceFlags.HasFlag(AceFlags.InheritOnly) ||
                    (directory && ace.AceFlags != (AceFlags.ObjectInherit | AceFlags.ContainerInherit)) ||
                    !allowed.Remove(ace.SecurityIdentifier))
                    throw new UnauthorizedAccessException("CUSTODY_ACCESS_RULE_REFUSED");
            if (allowed.Count != 0) throw new UnauthorizedAccessException("CUSTODY_ACCESS_RULE_REFUSED");
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint info,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr security, int level, int type, out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int kind, ref IntPtr owner, int size);
}
