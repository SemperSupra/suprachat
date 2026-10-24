using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SupraChat.PeerLedgerRdte;

[SupportedOSPlatform("windows")]
internal static class CustodyChecks
{
    internal static string Phase { get; private set; } = "NOT_STARTED";
    internal sealed record Check(string Case, bool Passed);
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("CUSTODY_CHECK_FAILED"); }
    private static void Refused(Action action, string? expected = null)
    {
        try { action(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidDataException or CryptographicException)
        {
            Require(expected is null ? ex is CryptographicException : ex.Message == expected);
            return;
        }
        throw new InvalidOperationException("CUSTODY_REFUSAL_NOT_OBSERVED");
    }

    internal static int Reopen(string root)
    {
        try
        {
            using var custody = WindowsOwnerCustody.Open(root);
            using var key = custody.LoadIssuer();
            var proof = key.RestartProof();
            try { Console.WriteLine(Convert.ToBase64String(proof)); } // Redirected private host-to-host proof, never forwarded/exported.
            finally { CryptographicOperations.ZeroMemory(proof); }
            return 0;
        }
        catch { return 74; }
    }

    internal static async Task<IReadOnlyList<Check>> RunAsync(Func<string[], Process> spawn)
    {
        var checks = new List<Check>();
        void Add(string name) { checks.Add(new(name, true)); Phase = name; }
        using var root = WindowsOwnerCustody.Create();
        using var material = root.InitializeIssuer();
        using (var loaded = root.LoadIssuer())
        {
            var a = material.RestartProof(); var b = loaded.RestartProof();
            try { Require(CryptographicOperations.FixedTimeEquals(a, b)); }
            finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
        }
        Add("protected-root-key-roundtrip");
        using (var initial = material.CreateAuthority())
        using (var loaded = root.LoadIssuer())
        using (var reopened = loaded.CreateAuthority())
        {
            var session = Guid.NewGuid().ToString("N");
            var grant = initial.Issue("fixture-custody-actor", "automation", session, ["session.read"], TimeSpan.FromMinutes(2));
            Require(reopened.Validate(grant, session, "session.read").Allowed &&
                !reopened.Validate(grant, session, "session.mutate").Allowed);
        }
        Add("reloaded-issuer-scoped-grant");
        using (var child = spawn(["--custody-reopen", root.Root]))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var line = await child.StandardOutput.ReadLineAsync(timeout.Token);
                await child.WaitForExitAsync(timeout.Token);
                var expected = material.RestartProof();
                var actual = new byte[32];
                try
                {
                    Require(child.ExitCode == 0 && line is not null && line.Length <= 64 &&
                        Convert.TryFromBase64String(line, actual, out var count) && count == 32 &&
                        CryptographicOperations.FixedTimeEquals(expected, actual));
                }
                finally { CryptographicOperations.ZeroMemory(expected); CryptographicOperations.ZeroMemory(actual); }
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            }
        }
        Add("new-host-dpapi-reopen");

        var originalDirectory = new DirectoryInfo(root.Root).GetAccessControl().GetSecurityDescriptorBinaryForm();
        try
        {
            var loose = new DirectoryInfo(root.Root).GetAccessControl();
            loose.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root.Root).SetAccessControl(loose);
            Refused(() => { using var unexpected = WindowsOwnerCustody.Open(root.Root); }, "CUSTODY_OWNER_ACL_REFUSED");
            Refused(() => WindowsOwnerCustody.VerifyStore(Path.Combine(root.Root, "fixture.sqlite")), "CUSTODY_OWNER_ACL_REFUSED");
        }
        finally { RestoreDirectory(root.Root, originalDirectory); }
        Positive(root);
        Add("widened-directory-and-store-refused");

        try
        {
            using var owner = WindowsIdentity.GetCurrent();
            var stripped = new DirectorySecurity();
            stripped.SetOwner(owner.User!);
            stripped.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { owner.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Distinct())
                stripped.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new DirectoryInfo(root.Root).SetAccessControl(stripped);
            Refused(() => { using var unexpected = WindowsOwnerCustody.Open(root.Root); }, "CUSTODY_ACCESS_RULE_REFUSED");
            Refused(() => WindowsOwnerCustody.VerifyStore(Path.Combine(root.Root, "fixture.sqlite")), "CUSTODY_ACCESS_RULE_REFUSED");
        }
        finally { RestoreDirectory(root.Root, originalDirectory); }
        Positive(root);
        Add("non-inheriting-directory-refused");

        var originalFile = new FileInfo(root.IssuerPath).GetAccessControl().GetSecurityDescriptorBinaryForm();
        try
        {
            var loose = new FileInfo(root.IssuerPath).GetAccessControl();
            loose.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(root.IssuerPath).SetAccessControl(loose);
            Refused(() => { using var unexpected = root.LoadIssuer(); }, "CUSTODY_OWNER_ACL_REFUSED");
        }
        finally
        {
            var restored = new FileSecurity();
            restored.SetSecurityDescriptorBinaryForm(originalFile, AccessControlSections.Access);
            new FileInfo(root.IssuerPath).SetAccessControl(restored);
        }
        Positive(root);
        Add("widened-key-acl-refused");

        var encrypted = File.ReadAllBytes(root.IssuerPath);
        try
        {
            var corrupt = encrypted.ToArray(); corrupt[^1] ^= 0x80;
            File.WriteAllBytes(root.IssuerPath, corrupt);
            Refused(() => { using var unexpected = root.LoadIssuer(); });
        }
        finally { File.WriteAllBytes(root.IssuerPath, encrypted); }
        Positive(root);
        Add("corrupt-key-refused");
        try
        {
            using (var file = new FileStream(root.IssuerPath, FileMode.Open, FileAccess.Write)) file.SetLength(8193);
            Refused(() => { using var unexpected = root.LoadIssuer(); }, "CUSTODY_KEY_SIZE_REFUSED");
        }
        finally { File.WriteAllBytes(root.IssuerPath, encrypted); }
        Positive(root);
        Add("oversize-key-refused");
        var saved = Path.Combine(root.Root, "issuer.saved");
        File.Move(root.IssuerPath, saved);
        try
        {
            Refused(() => { using var unexpected = root.LoadIssuer(); }, "CUSTODY_HANDLE_REFUSED");
            Refused(() => { using var unexpected = root.InitializeIssuer(); }, "CUSTODY_INITIALIZATION_REFUSED");
            using var reopened = WindowsOwnerCustody.Open(root.Root);
            Refused(() => { using var unexpected = reopened.InitializeIssuer(); }, "CUSTODY_INITIALIZATION_REFUSED");
        }
        finally { File.Move(saved, root.IssuerPath); }
        Positive(root);
        Add("missing-key-no-implicit-reset");

        var alias = Path.Combine(root.Root, "issuer.alias");
        Require(CreateHardLinkW(alias, root.IssuerPath, IntPtr.Zero));
        try { Refused(() => { using var unexpected = root.LoadIssuer(); }, "CUSTODY_OBJECT_REFUSED"); }
        finally { File.Delete(alias); } // Exactly the fresh alias created above; never recursive.
        Positive(root);
        Add("hard-linked-key-refused");

        string junction;
        using (var empty = WindowsOwnerCustody.Create()) junction = empty.Root;
        SetJunction(junction, root.Root);
        Refused(() => { using var unexpected = WindowsOwnerCustody.Open(junction); }, "CUSTODY_REPARSE_PATH_REFUSED");
        Add("junction-root-refused");

        using var restricted = RestrictedToken();
        var control = Path.Combine(Path.GetTempPath(), "suprachat-custody-control-" + Guid.NewGuid().ToString("N"));
        var controlSecurity = WindowsOwnerCustody.OwnerSecurity();
        controlSecurity.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        CustodyNative.CreateDirectoryOnce(control, controlSecurity.GetSecurityDescriptorBinaryForm());
        var controlFile = Path.Combine(control, "synthetic-public.bin");
        File.WriteAllBytes(controlFile, new byte[16]); // Contains no key, grant, path, identity or credential.
        var positive = false; var readDenied = false; var createDenied = false;
        WindowsIdentity.RunImpersonated(restricted, () =>
        {
            using (var handle = CustodyNative.CreateFileW(controlFile, 0x80000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                positive = !handle.IsInvalid;
            using (var handle = CustodyNative.CreateFileW(root.IssuerPath, 0x80000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                readDenied = handle.IsInvalid && Marshal.GetLastWin32Error() == 5;
            using (var handle = CustodyNative.CreateFileW(Path.Combine(root.Root, "restricted-attempt.bin"), 0x40000000, 0, IntPtr.Zero, 1, 0, IntPtr.Zero))
                createDenied = handle.IsInvalid && Marshal.GetLastWin32Error() == 5;
        });
        Require(positive); Add("restricted-token-positive-control");
        Require(readDenied); Add("restricted-token-key-read-denied");
        Require(createDenied); Add("restricted-token-directory-create-denied");
        Positive(root);
        return checks;
    }

    private static void Positive(WindowsOwnerCustody root) { using var valid = root.LoadIssuer(); }
    private static void RestoreDirectory(string path, byte[] descriptor)
    {
        var restored = new DirectorySecurity();
        restored.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
        new DirectoryInfo(path).SetAccessControl(restored);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    private static SafeAccessTokenHandle RestrictedToken()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
        var sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var entry = new SidAndAttributes { Sid = pin.AddrOfPinnedObject() };
            if (!CreateRestrictedToken(identity.AccessToken, 1, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, ref entry, out var token))
                throw new InvalidOperationException("RESTRICTED_TOKEN_UNAVAILABLE");
            if (!IsTokenRestricted(token)) { token.Dispose(); throw new InvalidOperationException("RESTRICTED_TOKEN_NOT_RESTRICTED"); }
            return token;
        }
        finally { pin.Free(); }
    }

    private static void SetJunction(string path, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CustodyNative.CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Require(!handle.IsInvalid && DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero));
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags,
        uint disableCount, IntPtr disable, uint privilegeCount, IntPtr privileges, uint restrictCount, ref SidAndAttributes restrict, out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int size, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
}
