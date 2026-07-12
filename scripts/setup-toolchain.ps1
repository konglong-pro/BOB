param(
    [Parameter(Mandatory = $true)]
    [string]$ToolsRoot
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-UnderRoot([string]$Path, [string]$Root) {
    $fullPath = Get-FullPath $Path
    $fullRoot = (Get-FullPath $Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes tool root: $fullPath"
    }

    # ToolsRoot itself may intentionally be a junction, but no descendant
    # ancestor may redirect a managed operation outside that trust boundary.
    $relative = $fullPath.Substring($fullRoot.Length)
    $segments = @($relative.Split(
        [char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar),
        [System.StringSplitOptions]::RemoveEmptyEntries))
    $current = $fullRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    for ($index = 0; $index -lt $segments.Count - 1; $index++) {
        $current = Join-Path $current $segments[$index]
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Managed path contains a reparse-point ancestor: $current"
            }
        }
    }
}

function Get-Download([string]$Uri, [string]$Destination) {
    Assert-UnderRoot $Destination $ToolsRoot
    if (Test-Path -LiteralPath $Destination) {
        if ((Get-Item -LiteralPath $Destination).Length -gt 0) {
            Write-Host "Using cached $Destination"
            return
        }

        Remove-Item -LiteralPath $Destination -Force
    }

    $partial = "$Destination.part"
    Assert-UnderRoot $partial $ToolsRoot
    if (Test-Path -LiteralPath $partial) {
        Remove-Item -LiteralPath $partial -Force
    }

    Write-Host "Downloading $Uri"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $partial
        if (-not (Test-Path -LiteralPath $partial) -or (Get-Item -LiteralPath $partial).Length -eq 0) {
            throw "Download produced an empty file: $Uri"
        }
        Move-Item -LiteralPath $partial -Destination $Destination -Force
    }
    catch {
        if (Test-Path -LiteralPath $partial) {
            Remove-Item -LiteralPath $partial -Force
        }
        throw
    }
}

function Assert-Sha256([string]$Archive, [string]$ChecksumFile) {
    $expected = ((Get-Content -LiteralPath $ChecksumFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        throw "SHA-256 mismatch for $Archive"
    }
}

function Assert-Sha1([string]$Archive, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $Archive -Algorithm SHA1).Hash.ToLowerInvariant()
    if ($Expected.ToLowerInvariant() -ne $actual) {
        throw "SHA-1 mismatch for $Archive"
    }
}

function Assert-Sha256Value([string]$File, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Expected.ToLowerInvariant() -ne $actual) {
        throw "SHA-256 mismatch for $File"
    }
}

function Assert-NativeSuccess([string]$Action, [int]$ExitCode) {
    if ($ExitCode -ne 0) {
        throw "$Action failed with exit code $ExitCode."
    }
}

function Remove-SafeDirectory([string]$Path) {
    Assert-UnderRoot $Path $ToolsRoot
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Remove-ReparsePoint $item
        }
        else {
            Remove-DirectoryTreeSafely $Path
        }
    }
}

function Remove-ReparsePoint([System.IO.FileSystemInfo]$Item) {
    if ($Item.PSIsContainer) {
        [System.IO.Directory]::Delete($Item.FullName, $false)
    }
    else {
        [System.IO.File]::Delete($Item.FullName)
    }
}

function Remove-DirectoryTreeSafely([string]$Path) {
    foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
        if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Remove-ReparsePoint $child
        }
        elseif ($child.PSIsContainer) {
            Remove-DirectoryTreeSafely $child.FullName
        }
        else {
            Remove-Item -LiteralPath $child.FullName -Force
        }
    }

    Remove-Item -LiteralPath $Path -Force
}

function Assert-NoReparsePointsInTree([string]$Path) {
    Assert-UnderRoot (Join-Path $Path '.path-safety-probe') $ToolsRoot
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push((Get-FullPath $Path))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Managed directory tree contains a reparse point: $($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pending.Push($child.FullName)
            }
        }
    }
}

function Remove-SafeFile([string]$Path) {
    Assert-UnderRoot $Path $ToolsRoot
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.PSIsContainer -and
            ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw "Expected a file but found a directory: $Path"
        }
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Remove-ReparsePoint $item
        }
        else {
            Remove-Item -LiteralPath $Path -Force
        }
    }
}

function Get-VerifiedSha256Pair(
    [string]$ArchiveUri,
    [string]$ChecksumUri,
    [string]$Archive,
    [string]$ChecksumFile) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        Get-Download $ArchiveUri $Archive
        Get-Download $ChecksumUri $ChecksumFile
        try {
            Assert-Sha256 $Archive $ChecksumFile
            return
        }
        catch {
            if ($attempt -eq 1) {
                throw
            }
            Write-Warning "Discarding a corrupt download cache and retrying: $Archive"
            Remove-SafeFile $Archive
            Remove-SafeFile $ChecksumFile
        }
    }
}

function Get-VerifiedPinnedDownload(
    [string]$Uri,
    [string]$Destination,
    [ValidateSet('SHA1', 'SHA256')]
    [string]$Algorithm,
    [string]$Expected) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        Get-Download $Uri $Destination
        try {
            if ($Algorithm -eq 'SHA1') {
                Assert-Sha1 $Destination $Expected
            }
            else {
                Assert-Sha256Value $Destination $Expected
            }
            return
        }
        catch {
            if ($attempt -eq 1) {
                throw
            }
            Write-Warning "Discarding a corrupt or unexpected download and retrying: $Destination"
            Remove-SafeFile $Destination
        }
    }
}

function Test-DotnetSdk([string]$Root) {
    $executable = Join-Path $Root 'dotnet.exe'
    Assert-UnderRoot $executable $ToolsRoot
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        return $false
    }

    try {
        $output = & $executable --version 2>$null
        $lines = @($output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
        return $LASTEXITCODE -eq 0 -and $lines.Count -gt 0 -and $lines[-1] -eq '10.0.301'
    }
    catch {
        return $false
    }
}

function Find-JdkHome([string]$Root) {
    Assert-UnderRoot (Join-Path $Root '.path-safety-probe') $ToolsRoot
    return Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'bin\java.exe')
    } | Select-Object -First 1 -ExpandProperty FullName
}

function Test-Jdk17Home([string]$InstallRoot) {
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        return $false
    }

    $releaseFile = Join-Path $InstallRoot 'release'
    $java = Join-Path $InstallRoot 'bin\java.exe'
    Assert-UnderRoot $java $ToolsRoot
    if (-not (Test-Path -LiteralPath $java -PathType Leaf) -or
        -not (Test-Path -LiteralPath $releaseFile -PathType Leaf) -or
        (Get-Content -LiteralPath $releaseFile -Raw) -notmatch '(?m)^JAVA_VERSION="17\.') {
        return $false
    }

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $java -version 2>&1 | Out-Null
        $javaExitCode = $LASTEXITCODE
    }
    catch {
        return $false
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return $javaExitCode -eq 0
}

function Test-GradleHome([string]$InstallRoot) {
    $gradle = Join-Path $InstallRoot 'bin\gradle.bat'
    Assert-UnderRoot $gradle $ToolsRoot
    if (-not (Test-Path -LiteralPath $gradle -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $InstallRoot 'lib\gradle-core-9.4.1.jar') -PathType Leaf)) {
        return $false
    }

    try {
        $output = & $gradle --version 2>$null
        $text = [string]::Join([Environment]::NewLine, @($output))
        return $LASTEXITCODE -eq 0 -and $text -match '(?m)^Gradle 9\.4\.1\s*$'
    }
    catch {
        return $false
    }
}

function Test-AndroidCommandLineTools([string]$SdkManager) {
    Assert-UnderRoot $SdkManager $ToolsRoot
    if (-not (Test-Path -LiteralPath $SdkManager -PathType Leaf)) {
        return $false
    }

    try {
        $output = & $SdkManager --version 2>$null
        $lines = @($output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
        return $LASTEXITCODE -eq 0 -and $lines.Count -gt 0 -and $lines[-1] -eq '20.0'
    }
    catch {
        return $false
    }
}

$ToolsRoot = Get-FullPath $ToolsRoot
New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
$downloads = Join-Path $ToolsRoot 'downloads'
Assert-UnderRoot (Join-Path $downloads '.path-safety-probe') $ToolsRoot
New-Item -ItemType Directory -Force -Path $downloads | Out-Null

# .NET 10 LTS SDK
$dotnetRoot = Join-Path $ToolsRoot 'dotnet'
$dotnetInstall = Join-Path $downloads 'dotnet-install.ps1'
# Pinned to the Microsoft-hosted script fetched on 2026-07-11. A changed
# upstream script must be reviewed and have this hash deliberately updated.
$dotnetInstallerDownload = @{
    Uri = 'https://dot.net/v1/dotnet-install.ps1'
    Destination = $dotnetInstall
    Algorithm = 'SHA256'
    Expected = '6585899aed55ff6ae13dbe1e8c3b878f2d00433520e7efbe250b75db948b7da9'
}
Get-VerifiedPinnedDownload @dotnetInstallerDownload
if (-not (Test-DotnetSdk $dotnetRoot)) {
    $dotnetStaging = Join-Path $ToolsRoot 'dotnet.installing'
    Remove-SafeDirectory $dotnetStaging
    New-Item -ItemType Directory -Force -Path $dotnetStaging | Out-Null
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dotnetInstall -Version '10.0.301' -InstallDir $dotnetStaging -NoPath
    Assert-NativeSuccess '.NET SDK installation' $LASTEXITCODE
    if (-not (Test-DotnetSdk $dotnetStaging)) {
        throw '.NET SDK installation did not produce SDK 10.0.301.'
    }
    Remove-SafeDirectory $dotnetRoot
    Move-Item -LiteralPath $dotnetStaging -Destination $dotnetRoot
}

# Microsoft Build of OpenJDK 17 LTS (portable ZIP)
$jdkArchive = Join-Path $downloads 'microsoft-jdk-17-windows-x64.zip'
$jdkChecksum = Join-Path $downloads 'microsoft-jdk-17-windows-x64.zip.sha256sum.txt'
$jdkDownload = @{
    ArchiveUri = 'https://aka.ms/download-jdk/microsoft-jdk-17-windows-x64.zip'
    ChecksumUri = 'https://aka.ms/download-jdk/microsoft-jdk-17-windows-x64.zip.sha256sum.txt'
    Archive = $jdkArchive
    ChecksumFile = $jdkChecksum
}
Get-VerifiedSha256Pair @jdkDownload
$jdkExtract = Join-Path $ToolsRoot 'jdk-17'
$jdkHome = Find-JdkHome $jdkExtract
if (-not (Test-Jdk17Home $jdkHome)) {
    $jdkStaging = Join-Path $ToolsRoot 'jdk-17.installing'
    Remove-SafeDirectory $jdkStaging
    New-Item -ItemType Directory -Force -Path $jdkStaging | Out-Null
    Expand-Archive -LiteralPath $jdkArchive -DestinationPath $jdkStaging
    $stagedJdkHome = Find-JdkHome $jdkStaging
    if (-not (Test-Jdk17Home $stagedJdkHome)) {
        throw 'The JDK archive did not contain a valid JDK 17 installation.'
    }
    Remove-SafeDirectory $jdkExtract
    Move-Item -LiteralPath $jdkStaging -Destination $jdkExtract
    $jdkHome = Find-JdkHome $jdkExtract
}

$env:JAVA_HOME = $jdkHome

# Android command-line tools and API 36 SDK
$androidSdk = Join-Path $ToolsRoot 'android-sdk'
$commandLineArchive = Join-Path $downloads 'commandlinetools-win-14742923_latest.zip'
# Google publishes this checksum alongside command-line tools revision 14742923.
$androidToolsDownload = @{
    Uri = 'https://dl.google.com/android/repository/commandlinetools-win-14742923_latest.zip'
    Destination = $commandLineArchive
    Algorithm = 'SHA1'
    Expected = '16b3f45ddb3d85ea6bbe6a1c0b47146daf0db450'
}
Get-VerifiedPinnedDownload @androidToolsDownload
$sdkManager = Join-Path $androidSdk 'cmdline-tools\latest\bin\sdkmanager.bat'
if (-not (Test-AndroidCommandLineTools $sdkManager)) {
    $commandLineStaging = Join-Path $ToolsRoot 'android-commandline.installing'
    Remove-SafeDirectory $commandLineStaging
    New-Item -ItemType Directory -Force -Path $commandLineStaging | Out-Null
    Expand-Archive -LiteralPath $commandLineArchive -DestinationPath $commandLineStaging
    $commandLineSource = Join-Path $commandLineStaging 'cmdline-tools'
    $stagedSdkManager = Join-Path $commandLineSource 'bin\sdkmanager.bat'
    if (-not (Test-Path -LiteralPath $stagedSdkManager -PathType Leaf)) {
        throw 'The Android command-line tools archive did not contain sdkmanager.bat.'
    }
    $commandLineTarget = Join-Path $androidSdk 'cmdline-tools\latest'
    Remove-SafeDirectory $commandLineTarget
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $commandLineTarget) | Out-Null
    Move-Item -LiteralPath $commandLineSource -Destination $commandLineTarget
    Remove-SafeDirectory $commandLineStaging
    if (-not (Test-AndroidCommandLineTools $sdkManager)) {
        throw 'Android command-line tools installation did not produce revision 20.0.'
    }
}

$env:ANDROID_HOME = $androidSdk
$env:ANDROID_SDK_ROOT = $androidSdk
Assert-NoReparsePointsInTree $androidSdk
$licenseAnswers = ((1..100 | ForEach-Object { 'y' }) -join [Environment]::NewLine)
$licenseAnswers | & $sdkManager --sdk_root=$androidSdk --licenses | Out-Host
Assert-NativeSuccess 'Android SDK license acceptance' $LASTEXITCODE
& $sdkManager --sdk_root=$androidSdk 'platform-tools' 'platforms;android-36' 'build-tools;36.0.0'
Assert-NativeSuccess 'Android SDK package installation' $LASTEXITCODE

# Gradle required by AGP 9.2.1
$gradleArchive = Join-Path $downloads 'gradle-9.4.1-bin.zip'
$gradleChecksum = Join-Path $downloads 'gradle-9.4.1-bin.zip.sha256'
$gradleDownload = @{
    ArchiveUri = 'https://services.gradle.org/distributions/gradle-9.4.1-bin.zip'
    ChecksumUri = 'https://services.gradle.org/distributions/gradle-9.4.1-bin.zip.sha256'
    Archive = $gradleArchive
    ChecksumFile = $gradleChecksum
}
Get-VerifiedSha256Pair @gradleDownload
$gradleHome = Join-Path $ToolsRoot 'gradle-9.4.1'
if (-not (Test-GradleHome $gradleHome)) {
    $gradleStaging = Join-Path $ToolsRoot 'gradle-9.4.1.installing'
    Remove-SafeDirectory $gradleStaging
    New-Item -ItemType Directory -Force -Path $gradleStaging | Out-Null
    Expand-Archive -LiteralPath $gradleArchive -DestinationPath $gradleStaging
    $stagedGradleHome = Join-Path $gradleStaging 'gradle-9.4.1'
    if (-not (Test-GradleHome $stagedGradleHome)) {
        throw 'The Gradle archive did not contain Gradle 9.4.1.'
    }
    Remove-SafeDirectory $gradleHome
    Move-Item -LiteralPath $stagedGradleHome -Destination $gradleHome
    Remove-SafeDirectory $gradleStaging
}

# A process interruption may leave a verified staging directory behind even
# when the final installation is already healthy. It is never part of runtime.
@(
    'dotnet.installing'
    'jdk-17.installing'
    'android-commandline.installing'
    'gradle-9.4.1.installing'
) | ForEach-Object {
    Remove-SafeDirectory (Join-Path $ToolsRoot $_)
}

$versions = @(
    "DOTNET_ROOT=$dotnetRoot"
    "JAVA_HOME=$jdkHome"
    "ANDROID_SDK_ROOT=$androidSdk"
    "GRADLE_HOME=$gradleHome"
)
$versionsPath = Join-Path $ToolsRoot 'toolchain-paths.txt'
$versionsTemporaryPath = "$versionsPath.part"
Remove-SafeFile $versionsTemporaryPath
Set-Content -LiteralPath $versionsTemporaryPath -Value $versions -Encoding UTF8
Remove-SafeFile $versionsPath
Move-Item -LiteralPath $versionsTemporaryPath -Destination $versionsPath

if (-not (Test-DotnetSdk $dotnetRoot)) {
    throw '.NET SDK verification did not find SDK 10.0.301.'
}
& (Join-Path $dotnetRoot 'dotnet.exe') --version
Assert-NativeSuccess '.NET SDK verification' $LASTEXITCODE
if (-not (Test-Jdk17Home $jdkHome)) {
    throw 'JDK verification did not find a JDK 17 runtime.'
}
$javaVersionOutput = Join-Path $ToolsRoot 'java-version.tmp'
Remove-SafeFile $javaVersionOutput
try {
    $javaProcessArguments = @{
        FilePath = Join-Path $jdkHome 'bin\java.exe'
        ArgumentList = '-version'
        NoNewWindow = $true
        Wait = $true
        PassThru = $true
        RedirectStandardError = $javaVersionOutput
    }
    $javaProcess = Start-Process @javaProcessArguments
    Get-Content -LiteralPath $javaVersionOutput | Write-Host
    $javaExitCode = $javaProcess.ExitCode
}
finally {
    Remove-SafeFile $javaVersionOutput
}
Assert-NativeSuccess 'JDK verification' $javaExitCode
if (-not (Test-GradleHome $gradleHome)) {
    throw 'Gradle verification did not find Gradle 9.4.1.'
}
& (Join-Path $gradleHome 'bin\gradle.bat') --version
Assert-NativeSuccess 'Gradle verification' $LASTEXITCODE
if (-not (Test-AndroidCommandLineTools $sdkManager)) {
    throw 'Android command-line tools verification did not find revision 20.0.'
}
& $sdkManager --sdk_root=$androidSdk --list_installed
Assert-NativeSuccess 'Android SDK verification' $LASTEXITCODE

Write-Host "BOB toolchain is ready in $ToolsRoot"
