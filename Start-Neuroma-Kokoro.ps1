param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Neuroma\Kokoro")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Kokoro {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8880/openapi.json" -TimeoutSec 2
        return $response.StatusCode -eq 200
    } catch {
        return $false
    }
}

if (Test-Kokoro) {
    Write-Output "Kokoro is already ready on http://127.0.0.1:8880"
    exit 0
}

$markerPath = Join-Path $InstallRoot "installation.json"
if (-not (Test-Path -LiteralPath $markerPath)) {
    throw "Local Kokoro has not been installed."
}
$installation = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
$repoDirectory = [string]$installation.repositoryDirectory
$uvPath = [string]$installation.uvPath
if (-not (Test-Path -LiteralPath $repoDirectory)) { throw "The Kokoro source directory is missing." }
if (-not (Test-Path -LiteralPath $uvPath)) { throw "The uv runtime is missing." }

$logDirectory = Join-Path $InstallRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$stdoutLog = Join-Path $logDirectory "kokoro.stdout.log"
$stderrLog = Join-Path $logDirectory "kokoro.stderr.log"

$env:PYTHONUTF8 = "1"
$env:PROJECT_ROOT = $repoDirectory
$env:USE_GPU = "false"
$env:PYTHONPATH = "$repoDirectory;$repoDirectory\api"
$env:MODEL_DIR = "src/models"
$env:VOICES_DIR = "src/voices/v1_0"
$env:WEB_PLAYER_PATH = Join-Path $repoDirectory "web"
$env:API_LOG_LEVEL = "WARNING"
$env:UV_PROJECT_ENVIRONMENT = Join-Path $repoDirectory ".venv"
$env:UV_PYTHON_INSTALL_DIR = Join-Path $InstallRoot "python"
$env:UV_CACHE_DIR = Join-Path $InstallRoot "cache"

$arguments = @(
    "run", "--no-sync", "uvicorn", "api.src.main:app",
    "--host", "127.0.0.1", "--port", "8880"
)
$process = Start-Process -FilePath $uvPath -ArgumentList $arguments -WorkingDirectory $repoDirectory `
    -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
$process.Id | Set-Content -LiteralPath (Join-Path $InstallRoot "kokoro.pid") -Encoding ASCII

$deadline = (Get-Date).AddMinutes(2)
while ((Get-Date) -lt $deadline) {
    if ($process.HasExited) {
        $detail = if (Test-Path -LiteralPath $stderrLog) {
            (Get-Content -LiteralPath $stderrLog -Tail 20) -join [Environment]::NewLine
        } else { "No error log was written." }
        throw "Kokoro exited during startup.$([Environment]::NewLine)$detail"
    }
    if (Test-Kokoro) {
        Write-Output "Kokoro is ready on http://127.0.0.1:8880"
        exit 0
    }
    Start-Sleep -Milliseconds 500
}

Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
throw "Kokoro did not become ready within two minutes. See $stderrLog"
