param(
    [ValidateSet('x64', 'x86')][string]$Architecture = 'x64',
    [string]$DevRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..')),
    [string]$AutoHotkeyPath
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ScancodeMap.Smoke.csproj'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts = Join-Path $repository ".artifacts/$Architecture"
if (!$AutoHotkeyPath) {
    $exe = if ($Architecture -eq 'x86') { 'AutoHotkeyU32.exe' } else { 'AutoHotkeyU64.exe' }
    $AutoHotkeyPath = "C:/Program Files/AutoHotkey/v1.1.37.02/$exe"
}
dotnet build $project -c Release "-p:PlatformTarget=$Architecture" -p:UseAppHost=false --nologo --disable-build-servers --artifacts-path $artifacts
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$hostPath = if ($Architecture -eq 'x86') { 'C:/Program Files (x86)/dotnet/dotnet.exe' } else { 'C:/Program Files/dotnet/dotnet.exe' }
& $hostPath (Join-Path $artifacts 'bin/ScancodeMap.Smoke/release/ScancodeMap.Smoke.dll') $AutoHotkeyPath $DevRoot
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
