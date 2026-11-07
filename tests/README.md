# ScancodeMap tests

Run from the repository root:

```powershell
./tests/ScancodeMap.Smoke/Run.ps1 -Architecture x64
./tests/ScancodeMap.Smoke/Run.ps1 -Architecture x86
dotnet publish tests/ScancodeMap.Smoke -c Release -r win-x64 -p:PublishAot=true --artifacts-path .artifacts/aot-x64
# Use win-x86 and .artifacts/aot-x86 for 32-bit NativeAOT.
```

The runner accepts `DevRoot` and `AutoHotkeyPath` overrides. Managed outputs
are isolated under `.artifacts/x64` and `.artifacts/x86`, including dependency
outputs. Running the smoke executable without arguments skips AHK comparison;
pass the interpreter path and Dev root to include it.

Tests use a unique temporary HKCU subtree through internal seams, never live
HKLM. Original AHK comparisons use fixed hex fixtures and the same temporary
registry data: omitted/empty fallback, raw four-byte chunks, incomplete
remainders, zero-length values, read/write/delete, and missing-key creation.
The AHK process is hidden and explicitly joined before its scripts are removed.

Other checks cover absent/read/type failures (including REG_NONE), malformed
bytes preserved unchanged, missing/deleted/recreated keys and churn, no watcher
key creation, target-only notifications, delegate reference identity,
append/prepend/replacement, true stopping one batch, no initial callback,
captured-batch ordering, serial execution, self/external disposal, per-subscriber
callback faults, last-removal cleanup, and startup/terminal watch retry.

Captured batches retain callbacks removed/replaced during delivery. Dispose
waits for captured callbacks except on the callback queue; do not block a
callback on another thread calling Dispose. Native notifications can coalesce,
and target/ancestor rename is unsupported.
