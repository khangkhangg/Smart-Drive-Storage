using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Windows;

/// <summary>Windows Credential Manager (generic credentials named "Miu/&lt;key&gt;"), protected by your Windows sign-in.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore(string prefix = "Miu") : ISecretStore
{
    private const int CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private string Target(string key) => $"{prefix}/{key}";

    public string? Get(string key)
    {
        if (!CredReadW(Target(key), CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0) return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(ptr); }
    }

    public void Set(string key, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC, TargetName = Target(key), CredentialBlobSize = bytes.Length, CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE, UserName = Environment.UserName,
            };
            if (!CredWriteW(ref cred, 0)) throw new IOException($"Couldn't save to Credential Manager (error {Marshal.GetLastWin32Error()}).");
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public void Delete(string key) => CredDeleteW(Target(key), CRED_TYPE_GENERIC, 0);
}
