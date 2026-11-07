using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SevenKeyboard;

// Kept internal so tests can use unique HKCU paths without touching the production HKLM value.
internal sealed partial class RegistryStore(RegistryHive root, string path, RegistryView view)
{
    private const string ValueName = "Scancode Map";
    private const uint RegBinary = 3;
    private const int MoreData = 234;

    internal unsafe bool TryRead(out byte[]? data)
    {
        data = null;
        int error = Open(0x0001, out SafeRegistryHandle key);
        using (key)
        {
            if (IsMissing(error))
                return true;
            if (error != 0)
                return false;

            uint size = 0;
            error = RegQueryValueEx(key, ValueName, 0, out uint type, null, ref size);
            if (IsMissing(error))
                return true;
            if (error != 0 || type != RegBinary)
                return false;

            while (true)
            {
                if (size > Array.MaxLength)
                    return false;
                // A non-null buffer distinguishes reading a zero-byte value from another size query.
                byte[] buffer = new byte[Math.Max((int)size, 1)];
                uint actual = (uint)buffer.Length;
                fixed (byte* pointer = buffer)
                    error = RegQueryValueEx(key, ValueName, 0, out type, pointer, ref actual);
                if (IsMissing(error))
                    return true;
                if (error == MoreData && type == RegBinary)
                {
                    size = actual;
                    continue;
                }
                if (error != 0 || type != RegBinary)
                    return false;
                Array.Resize(ref buffer, (int)actual);
                data = buffer;
                return true;
            }
        }
    }

    internal unsafe bool Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        int error = RegCreateKeyEx((nint)(int)root, path, 0, 0, 0, 0x0002 | (int)view, 0,
            out SafeRegistryHandle key, out _);
        using (key)
        {
            if (error != 0)
                return false;
            fixed (byte* pointer = data)
                return RegSetValueEx(key, ValueName, 0, RegBinary, pointer, (uint)data.Length) == 0;
        }
    }

    internal bool Delete()
    {
        int error = Open(0x0002, out SafeRegistryHandle key);
        using (key)
            return error == 0 && RegDeleteValue(key, ValueName) == 0;
    }

    internal bool TryGetList(out byte[][] chunks, byte[]? data = null)
    {
        chunks = [];
        if (data is null || data.Length == 0)
        {
            if (!TryRead(out data))
                return false;
            if (data is null)
                return true;
        }
        chunks = ScancodeMap.Split(data);
        return true;
    }

    // The four-argument API is non-subtree in 2.0.0 and defaults to false in 2.1.0.
    internal IRegistryWatch Watch(Action changed) =>
        new RegistryWatch(new OnRegKeyLocalNotify(root, path, view, changed));

    private int Open(int access, out SafeRegistryHandle key) =>
        RegOpenKeyEx((nint)(int)root, path, 0, access | (int)view, out key);

    private static bool IsMissing(int error) => error is 2 or 3 or 1018;

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int RegOpenKeyEx(nint root, string path, uint options, int access, out SafeRegistryHandle key);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int RegCreateKeyEx(nint root, string path, uint reserved, nint keyClass,
        uint options, int access, nint securityAttributes, out SafeRegistryHandle key, out uint disposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved,
        out uint type, byte* data, ref uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int RegSetValueEx(SafeRegistryHandle key, string name, uint reserved,
        uint type, byte* data, uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int RegDeleteValue(SafeRegistryHandle key, string name);
}

internal interface IRegistryWatch : IDisposable
{
    Task Completion { get; }
}

internal sealed class RegistryWatch(OnRegKeyLocalNotify watch) : IRegistryWatch
{
    public Task Completion => watch.Completion;
    public void Dispose() => watch.Dispose();
}
