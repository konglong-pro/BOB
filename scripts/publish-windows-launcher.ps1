[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$BuildScript = Join-Path $PSScriptRoot 'build.ps1'
$LauncherSource = Join-Path $RepoRoot 'src\windows\Bob.Launcher\Program.cs'
$LauncherIcon = Join-Path $RepoRoot 'src\windows\Bob.Windows\Assets\BOB.ico'
$OutputPath = Join-Path $RepoRoot 'BOB.exe'
$ReleaseApp = Join-Path $RepoRoot 'src\windows\Bob.Windows\bin\Release\net10.0-windows\BOB.dll'
$CompilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)

foreach ($requiredFile in @($BuildScript, $LauncherSource, $LauncherIcon)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file not found: $requiredFile"
    }
}

$Compiler = $CompilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $Compiler) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

& $BuildScript -Target Windows -Configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Windows Release build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $ReleaseApp -PathType Leaf)) {
    throw "Windows Release output not found: $ReleaseApp"
}

& $Compiler `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:x64 `
    "/win32icon:$LauncherIcon" `
    /reference:System.dll `
    /reference:System.Windows.Forms.dll `
    "/out:$OutputPath" `
    $LauncherSource
if ($LASTEXITCODE -ne 0) {
    throw "BOB launcher compilation failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
    throw "BOB launcher was not created: $OutputPath"
}

$Launcher = Get-Item -LiteralPath $OutputPath
Write-Host "BOB quick launcher created: $($Launcher.FullName) ($($Launcher.Length) bytes)"
