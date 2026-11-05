using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Miu.Platform.Abstractions;

namespace Miu.Platform.MacOS;

/// <summary>
/// The macOS Trash, through Finder's own API (NSFileManager trashItemAtURL), so items show up in the Trash and
/// Finder's "Put Back" works too. Undo in Miu moves an item back if nothing else is in its place.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacTrash : IRecycleBin
{
    public bool IsSupported => true;

    public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var done = new List<RecycledItem>();
        var failed = new List<FailedItem>();
        var i = 0;
        foreach (var p in paths)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var size = Sizes.Of(p);
                var (ok, result, error) = ObjC.Trash(p);
                if (ok && result is not null) done.Add(new RecycledItem(p, result, size));
                else failed.Add(new FailedItem(p, error ?? "macOS couldn't move it to the Trash."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
            {
                failed.Add(new FailedItem(p, ex.Message));
            }
            progress?.Report(++i);
        }
        return new RecycleResult(done, failed);
    }, ct);

    public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) => Task.Run(() =>
    {
        var restored = new List<string>();
        var failed = new List<FailedItem>();
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath)) { failed.Add(new FailedItem(item.OriginalPath, "Something with that name already exists there.")); continue; }
                if (!File.Exists(item.TrashLocation) && !Directory.Exists(item.TrashLocation)) { failed.Add(new FailedItem(item.OriginalPath, "It is no longer in the Trash.")); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                if (Directory.Exists(item.TrashLocation)) Directory.Move(item.TrashLocation, item.OriginalPath);
                else File.Move(item.TrashLocation, item.OriginalPath);
                restored.Add(item.OriginalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(new FailedItem(item.OriginalPath, ex.Message)); }
        }
        return new RestoreResult(restored, failed);
    }, ct);

    private static class Sizes
    {
        public static long Of(string p)
        {
            if (File.Exists(p)) return new FileInfo(p).Length;
            if (!Directory.Exists(p)) return 0;
            long total = 0;
            try
            {
                foreach (var f in new DirectoryInfo(p).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    total += f.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return total;
        }
    }
}

/// <summary>Just enough Objective-C runtime to call NSFileManager.</summary>
[SupportedOSPlatform("macos")]
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";
    private static readonly Lazy<bool> Foundation = new(() =>
    {
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        return true;
    });

    [DllImport(Lib)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, out IntPtr resultingUrl, out IntPtr error);

    private static string? Utf8(IntPtr nsString) => nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, sel_registerName("UTF8String")));

    /// <summary>Moves a file or folder to the Trash. Returns where it went, or the system's error message.</summary>
    public static (bool Ok, string? TrashPath, string? Error) Trash(string path)
    {
        _ = Foundation.Value;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var nsPath = SendUtf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), path);
            var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), nsPath);
            var manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
            var ok = SendTrash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, out var resulting, out var error);
            if (!ok) return (false, null, error == IntPtr.Zero ? null : Utf8(Send(error, sel_registerName("localizedDescription"))));
            return (true, resulting == IntPtr.Zero ? null : Utf8(Send(resulting, sel_registerName("path"))), null);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }
}
