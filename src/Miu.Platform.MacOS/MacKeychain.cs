using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Miu.Platform.Abstractions;

namespace Miu.Platform.MacOS;

/// <summary>Secrets in your login Keychain (service "Miu Smart Storage"), protected by your Mac account.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychain(string service = "Miu Smart Storage") : ISecretStore
{
    private const string Sec = "/System/Library/Frameworks/Security.framework/Security";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;

    [DllImport(Sec)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        uint passwordLength, byte[] password, IntPtr itemRef);

    [DllImport(Sec)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Sec)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);
    [DllImport(Sec)] private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
    [DllImport(Sec)] private static extern int SecKeychainItemDelete(IntPtr itemRef);
    [DllImport(CF)] private static extern void CFRelease(IntPtr cf);

    private readonly byte[] _service = Encoding.UTF8.GetBytes(service);

    public string? Get(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service, (uint)account.Length, account, out var len, out var data, out var item);
        if (rc == ErrSecItemNotFound) return null;
        Check(rc, "read");
        try
        {
            var bytes = new byte[len];
            Marshal.Copy(data, bytes, 0, (int)len);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public void Set(string key, string value)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var secret = Encoding.UTF8.GetBytes(value);
        var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service, (uint)account.Length, account, out _, out var data, out var item);
        if (rc == 0)
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            try { Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret), "update"); }
            finally { CFRelease(item); }
            return;
        }
        if (rc != ErrSecItemNotFound) Check(rc, "read");
        Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)_service.Length, _service, (uint)account.Length, account, (uint)secret.Length, secret, IntPtr.Zero), "save");
    }

    public void Delete(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service, (uint)account.Length, account, out _, out var data, out var item);
        if (rc == ErrSecItemNotFound) return;
        Check(rc, "read");
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        try { Check(SecKeychainItemDelete(item), "remove"); }
        finally { CFRelease(item); }
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new IOException($"The Keychain couldn't {what} Miu's secret (error {rc}).");
    }
}
