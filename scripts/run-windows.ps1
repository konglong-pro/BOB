[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$DotnetRoot = Join-Path $RepoRoot '.tools\dotnet'
$DotnetPath = Join-Path $DotnetRoot 'dotnet.exe'
$AppPath = Join-Path $RepoRoot "src\windows\Bob.Windows\bin\$Configuration\net10.0-windows\BOB.dll"

if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw ".NET SDK not found: $DotnetPath. Run scripts\setup-toolchain.ps1 first."
}

if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
    throw "BOB has not been built for $Configuration. Run scripts\build.ps1 -Target Windows -Configuration $Configuration first."
}

$env:DOTNET_ROOT = $DotnetRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:Path = $DotnetRoot + [IO.Path]::PathSeparator + $env:Path

& $DotnetPath $AppPath
exit $LASTEXITCODE
