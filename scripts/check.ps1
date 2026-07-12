[CmdletBinding()]
param(
    [ValidateSet('All', 'Windows', 'Android')]
    [string] $Target = 'All'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ToolsRoot = Join-Path $RepoRoot '.tools'
$DotnetPath = Join-Path $ToolsRoot 'dotnet\dotnet.exe'
$JavaHome = Join-Path $ToolsRoot 'jdk17'
$ExtractedJdkRoot = Join-Path $ToolsRoot 'jdk-17'
$AndroidSdkRoot = Join-Path $ToolsRoot 'android-sdk'
$GradleHome = Join-Path $ToolsRoot 'gradle-9.4.1'
$GradlePath = Join-Path $GradleHome 'bin\gradle.bat'
$WindowsChecksProject = Join-Path $RepoRoot 'tests\windows\Bob.Windows.Checks\Bob.Windows.Checks.csproj'
$AndroidProject = Join-Path $RepoRoot 'src\android'
$NuGetConfig = Join-Path $RepoRoot 'NuGet.Config'

function Assert-File {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label not found: $Path"
    }
}

function Initialize-BobEnvironment {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('All', 'Windows', 'Android')]
        [string] $Target
    )

    $toolPaths = @()

    if ($Target -in @('All', 'Windows')) {
        Assert-File -Path $DotnetPath -Label '.NET SDK'

        $env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
        $env:DOTNET_CLI_HOME = Join-Path $RepoRoot '.dotnet-home'
        $env:NUGET_PACKAGES = Join-Path $RepoRoot '.nuget\packages'
        $env:APPDATA = Join-Path $RepoRoot '.build-user\Roaming'
        $env:LOCALAPPDATA = Join-Path $RepoRoot '.build-user\Local'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_NOLOGO = '1'
        New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA | Out-Null

        $toolPaths += $env:DOTNET_ROOT
    }

    if ($Target -in @('All', 'Android')) {
        if (-not (Test-Path -LiteralPath (Join-Path $JavaHome 'bin\java.exe'))) {
            $DetectedJdk = Get-ChildItem -LiteralPath $ExtractedJdkRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin\java.exe') } |
                Select-Object -First 1
            if ($DetectedJdk) {
                $JavaHome = $DetectedJdk.FullName
            }
        }

        Assert-File -Path (Join-Path $JavaHome 'bin\java.exe') -Label 'JDK 17'
        Assert-File -Path $GradlePath -Label 'Gradle'

        if (-not (Test-Path -LiteralPath $AndroidSdkRoot -PathType Container)) {
            throw "Android SDK not found: $AndroidSdkRoot"
        }

        $env:JAVA_HOME = $JavaHome
        $env:ANDROID_HOME = $AndroidSdkRoot
        $env:ANDROID_SDK_ROOT = $AndroidSdkRoot
        $env:GRADLE_HOME = $GradleHome
        $env:GRADLE_USER_HOME = Join-Path $RepoRoot '.gradle-user-home'

        $toolPaths += @(
            (Join-Path $JavaHome 'bin')
            (Join-Path $AndroidSdkRoot 'platform-tools')
            (Join-Path $GradleHome 'bin')
        )
    }

    $env:Path = ($toolPaths + $env:Path) -join [IO.Path]::PathSeparator
}

function Assert-LastExitCode {
    param([Parameter(Mandatory)][string] $Action)

    if ($LASTEXITCODE -ne 0) {
        throw "$Action failed with exit code $LASTEXITCODE."
    }
}

Initialize-BobEnvironment -Target $Target

Push-Location $RepoRoot
try {
    if ($Target -in @('All', 'Windows')) {
        Assert-File -Path $WindowsChecksProject -Label 'Windows checks project'
        Assert-File -Path $NuGetConfig -Label 'Repository NuGet config'
        & $DotnetPath restore $WindowsChecksProject --configfile $NuGetConfig --nologo
        Assert-LastExitCode -Action 'Windows checks restore'
        & $DotnetPath run --project $WindowsChecksProject --configuration Debug --no-restore
        Assert-LastExitCode -Action 'Windows checks'
    }

    if ($Target -in @('All', 'Android')) {
        if (-not (Test-Path -LiteralPath $AndroidProject -PathType Container)) {
            throw "Android project not found: $AndroidProject"
        }

        & $GradlePath --project-dir $AndroidProject --console=plain check
        Assert-LastExitCode -Action 'Android checks'
    }
}
finally {
    Pop-Location
}
