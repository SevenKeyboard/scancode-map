# ScancodeMap

```csharp
using SevenKeyboard;

if (ScancodeMap.TryRead(out byte[]? data) && data is not null)
{
    bool written = ScancodeMap.Write(data);
}

using var changes = ScancodeMap.Subscribe(() =>
{
    Console.WriteLine("Scancode Map changed.");
    return false; // true stops the current callback batch.
});
// Keep the subscription alive while watching; dispose when finished.
```

Version 2.0.0; `net10.0-windows`, x86/x64, NativeAOT compatible.
Reads/writes the HKLM `SYSTEM\CurrentControlSet\Control\Keyboard Layout`
value `Scancode Map` as raw REG_BINARY bytes.

`TryRead` returns true/null for absence, false/null for read/type errors.
`Write` creates missing key paths and returns false on failure; it may require
elevation. `Delete()` returns false on failure or absence. Reads/watching
never create keys.

`TryGetList(out byte[][] chunks, byte[]? data = null)` returns raw four-byte
chunks including headers, terminators and any remainder. Null/empty input reads
the registry, as in AHK. Absence succeeds with empty chunks; read/type errors
return false with empty chunks.

Subscriptions share one watch, with no initial callback. The same delegate
object replaces/repositions its subscription; `prepend: true` places it first.
`Completion` faults only that subscriber on callback exceptions; watch failures
end affected subscriptions, and later subscriptions retry. Missing/recreated
keys recover automatically. Last removal stops watching. Do not wait for
`Completion` inside a callback.

Requires external OnRegKeyLocalNotify 2.0.0+ (2.x) and library-build's version
checker. Override `OnRegKeyLocalNotifyProjectPath` and
`LibraryBuildTargetsPath` for other layouts. [Tests and build commands](tests/README.md).
