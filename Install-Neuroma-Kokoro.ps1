param(
    [string]$AppDirectory = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-StrictMode -Version Latest
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding

$kokoroCommit = "f901ed81ff9910b80ab24f800a245873221a5445"
$shortCommit = $kokoroCommit.Substring(0, 12)
$neuromaInstallRoot = Join-Path $env:LOCALAPPDATA "Neuroma\Kokoro"
$neuroMdInstallRoot = Join-Path $env:LOCALAPPDATA "NeuroMD\Kokoro"
$installRoot = if (
    -not (Test-Path -LiteralPath (Join-Path $neuromaInstallRoot "installation.json") -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $neuroMdInstallRoot "installation.json") -PathType Leaf)
) { $neuroMdInstallRoot } else { $neuromaInstallRoot }
$repoDirectory = Join-Path $installRoot "Kokoro-FastAPI-$shortCommit"
$markerPath = Join-Path $installRoot "installation.json"
$modelSha256 = "496dba118d1a58f5f3db2efc88dbdc216e0483fc89fe6e47ee1f2c53f18ad1e4"
$configSha256 = "5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f"

function Write-Stage([string]$Message) {
    Write-Output "NEUROMA_SETUP: $Message"
}

function Write-SetupProgress([int]$Percent, [string]$Message) {
    Write-Output ("NEUROMA_PROGRESS|{0}|{1}" -f ([Math]::Max(0, [Math]::Min(100, $Percent))), $Message)
}

function Find-Uv {
    $command = Get-Command uv.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\uv.exe"),
        (Join-Path $env:USERPROFILE ".local\bin\uv.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $packageRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    if (Test-Path -LiteralPath $packageRoot) {
        $packageUv = Get-ChildItem -LiteralPath $packageRoot -Directory -Filter "astral-sh.uv_*" -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter "uv.exe" -Recurse -ErrorAction SilentlyContinue } |
            Select-Object -First 1
        if ($packageUv) { return $packageUv.FullName }
    }
    return $null
}

function Get-Sha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Test-Sha256([string]$Path, [string]$Expected) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-Sha256 $Path) -eq $Expected)
}

function Invoke-TrackedDownload(
    [string]$Uri,
    [string]$Destination,
    [int]$StartPercent,
    [int]$EndPercent,
    [string]$Label
) {
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $client.DefaultRequestHeaders.UserAgent.ParseAdd("Neuroma-Kokoro-Setup/1")
    $temporary = "$Destination.download"
    try {
        Write-SetupProgress $StartPercent $Label
        $response = $client.GetAsync($Uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode()
        $total = $response.Content.Headers.ContentLength
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $outputStream = [System.IO.File]::Open($temporary, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
        try {
            $buffer = New-Object byte[] (1024 * 1024)
            [long]$received = 0
            $lastOverall = -1
            while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $outputStream.Write($buffer, 0, $count)
                $received += $count
                if ($total -and $total -gt 0) {
                    $filePercent = [Math]::Min(100, [Math]::Floor(($received / [double]$total) * 100))
                    $overall = $StartPercent + [Math]::Floor(($filePercent / 100) * ($EndPercent - $StartPercent))
                    if ($overall -ne $lastOverall) {
                        Write-SetupProgress $overall ("{0} ({1}%)" -f $Label, $filePercent)
                        $lastOverall = $overall
                    }
                }
            }
        }
        finally {
            $outputStream.Dispose()
            $inputStream.Dispose()
            $response.Dispose()
        }
        Move-Item -LiteralPath $temporary -Destination $Destination -Force
        Write-SetupProgress $EndPercent ("{0} complete" -f $Label)
    }
    finally {
        $client.Dispose()
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

Write-SetupProgress 2 "Inspecting this Windows account"
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
if ($installRoot -eq $neuroMdInstallRoot) {
    Write-Stage "Reusing the compatible NeuroMD local Kokoro installation"
}

$modelPath = Join-Path $repoDirectory "api\src\models\v1_0\kokoro-v1_0.pth"
$environmentPath = Join-Path $repoDirectory ".venv\Scripts\python.exe"
$alreadyInstalled = (Test-Path -LiteralPath $markerPath -PathType Leaf) -and
    (Test-Path -LiteralPath $repoDirectory -PathType Container) -and
    (Test-Path -LiteralPath $environmentPath -PathType Leaf) -and
    (Test-Path -LiteralPath $modelPath -PathType Leaf)

$uvPath = $null
if ($alreadyInstalled) {
    try {
        $existingMarker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
        if ($existingMarker.uvPath -and (Test-Path -LiteralPath $existingMarker.uvPath -PathType Leaf)) {
            $uvPath = [string]$existingMarker.uvPath
        }
    } catch {
        $alreadyInstalled = $false
    }
}
if (-not $uvPath) { $uvPath = Find-Uv }
if (-not $uvPath) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw "WinGet is required to install Astral uv without administrator access. Install App Installer from Microsoft, then retry."
    }
    Write-SetupProgress 5 "Installing the Astral uv runtime with WinGet"
    & $winget.Source install --id astral-sh.uv -e --source winget --scope user --silent --disable-interactivity --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "WinGet could not install Astral uv (exit $LASTEXITCODE)." }
    $uvPath = Find-Uv
    if (-not $uvPath) { throw "uv installed, but Neuroma could not locate uv.exe." }
}
Write-SetupProgress 10 "Astral uv runtime ready"

if (-not $alreadyInstalled -and -not (Test-Path -LiteralPath $repoDirectory)) {
    Write-SetupProgress 12 "Downloading pinned Kokoro-FastAPI source"
    $staging = Join-Path $installRoot ("staging-" + [Guid]::NewGuid().ToString("N"))
    $archive = Join-Path $staging "kokoro.zip"
    $expanded = Join-Path $staging "expanded"
    New-Item -ItemType Directory -Path $expanded -Force | Out-Null
    try {
        $archiveUrl = "https://github.com/remsky/Kokoro-FastAPI/archive/$kokoroCommit.zip"
        Invoke-TrackedDownload $archiveUrl $archive 12 20 "Downloading Kokoro source"
        Write-SetupProgress 22 "Extracting Kokoro source"
        Expand-Archive -LiteralPath $archive -DestinationPath $expanded
        $archiveRoot = Get-ChildItem -LiteralPath $expanded -Directory | Select-Object -First 1
        if (-not $archiveRoot) { throw "The Kokoro archive did not contain a source directory." }
        Move-Item -LiteralPath $archiveRoot.FullName -Destination $repoDirectory
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}
Write-SetupProgress 25 "Kokoro source ready"

if (-not $alreadyInstalled) {
    Write-SetupProgress 30 "Installing Python and Kokoro dependencies"
    $env:UV_PROJECT_ENVIRONMENT = Join-Path $repoDirectory ".venv"
    $env:UV_PYTHON_INSTALL_DIR = Join-Path $installRoot "python"
    $env:UV_CACHE_DIR = Join-Path $installRoot "cache"
    $env:UV_NO_PROGRESS = "1"
    Push-Location $repoDirectory
    try {
        & $uvPath sync --extra cpu --python 3.12
        if ($LASTEXITCODE -ne 0) { throw "Kokoro dependencies failed to install (exit $LASTEXITCODE)." }

        Write-SetupProgress 68 "Python and Kokoro dependencies ready"
        $modelDirectory = Join-Path $repoDirectory "api\src\models\v1_0"
        $modelConfigPath = Join-Path $modelDirectory "config.json"
        New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null
        if (-not (Test-Sha256 $modelPath $modelSha256)) {
            $modelUrl = "https://github.com/remsky/Kokoro-FastAPI/releases/download/v0.1.4/kokoro-v1_0.pth"
            Invoke-TrackedDownload $modelUrl $modelPath 70 92 "Downloading Kokoro voice model"
        } else {
            Write-SetupProgress 92 "Existing Kokoro voice model verified"
        }
        if (-not (Test-Sha256 $modelConfigPath $configSha256)) {
            $configUrl = "https://github.com/remsky/Kokoro-FastAPI/releases/download/v0.1.4/config.json"
            Invoke-TrackedDownload $configUrl $modelConfigPath 92 94 "Downloading model configuration"
        }
        if (-not (Test-Sha256 $modelPath $modelSha256) -or -not (Test-Sha256 $modelConfigPath $configSha256)) {
            throw "Kokoro model verification failed."
        }
        Write-SetupProgress 95 "Kokoro model verified"
    }
    finally {
        Pop-Location
    }

    [ordered]@{
        commit = $kokoroCommit
        repositoryDirectory = $repoDirectory
        uvPath = $uvPath
        installedAt = (Get-Date).ToString("o")
    } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
} else {
    Write-SetupProgress 95 "Existing local Kokoro installation verified"
}

Write-SetupProgress 96 "Saving local Neuroma voice configuration"
$configPath = Join-Path $AppDirectory "neuroma.json"
$voice = "af_bella"
$speed = 1
if (Test-Path -LiteralPath $configPath) {
    try {
        $oldConfig = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
        if ($oldConfig.voice) { $voice = [string]$oldConfig.voice }
        if ($oldConfig.speed) { $speed = [double]$oldConfig.speed }
    } catch {
        Write-Stage "Replacing an unreadable Neuroma voice configuration"
    }
}
[ordered]@{
    kokoroUrl = "http://127.0.0.1:8880"
    voice = $voice
    speed = $speed
} | ConvertTo-Json | ForEach-Object {
    [System.IO.File]::WriteAllText($configPath, $_ + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}

Write-SetupProgress 97 "Starting the private localhost Kokoro service"
& (Join-Path $AppDirectory "Start-Neuroma-Kokoro.ps1") -InstallRoot $installRoot
if ($LASTEXITCODE -ne 0) { throw "Kokoro installed but did not start." }

Write-SetupProgress 100 "Local Kokoro is ready"
