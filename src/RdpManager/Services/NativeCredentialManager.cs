using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RdpManager.Services;

/// <summary>
/// Thin P/Invoke wrapper around the Windows Credential Manager APIs (CredWrite/CredDelete).
/// Used instead of shelling out to cmdkey.exe so the plain-text password is never passed as a
/// process command-line argument, where any other process could observe it.
/// </summary>
internal static class NativeCredentialManager
{
    /// <summary>What cmdkey calls "LegacyGeneric": readable by apps, but never delegated by CredSSP.</summary>
    public const uint CredTypeGeneric = 1;

    /// <summary>
    /// What cmdkey calls "Domain", and what Windows itself writes for "remember me" RDP logins.
    /// CredSSP only delegates this type, so it is the one that actually logs a session in.
    /// </summary>
    public const uint CredTypeDomainPassword = 2;

    private const uint CRED_PERSIST_SESSION = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CredWriteW", CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    /// <summary>
    /// Stores a credential (e.g. target "TERMSRV/hostname") so mstsc.exe can log in silently.
    /// Returns false rather than throwing: the credential types have different validation rules,
    /// so a caller may legitimately try more than one.
    /// </summary>
    public static bool TrySave(string target, string username, string password, uint type)
    {
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var credentialBlob = Marshal.AllocHGlobal(passwordBytes.Length);
        var targetPtr = IntPtr.Zero;
        var userPtr = IntPtr.Zero;

        try
        {
            Marshal.Copy(passwordBytes, 0, credentialBlob, passwordBytes.Length);
            targetPtr = Marshal.StringToCoTaskMemUni(target);
            userPtr = Marshal.StringToCoTaskMemUni(username);

            var credential = new CREDENTIAL
            {
                Type = type,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = credentialBlob,
                Persist = CRED_PERSIST_SESSION,
                UserName = userPtr,
            };

            return CredWrite(ref credential, 0);
        }
        finally
        {
            // Zero the plain-text password out of unmanaged memory before freeing it.
            for (var i = 0; i < passwordBytes.Length; i++)
                Marshal.WriteByte(credentialBlob, i, 0);
            Array.Clear(passwordBytes, 0, passwordBytes.Length);

            Marshal.FreeHGlobal(credentialBlob);
            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
        }
    }

    public static void Delete(string target, uint type)
    {
        CredDelete(target, type, 0);
    }
}
