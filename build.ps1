$ErrorActionPreference = 'Stop'
$localSdk = 'C:\Users\dbwga\.dotnet-sdk\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) { $dotnet = $localSdk }
else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw 'The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0' }
    $dotnet = $command.Source
}
& $dotnet run --project tests\Neuroma.Tests\Neuroma.Tests.csproj -c Release
$publishDirectory = Join-Path $PSScriptRoot 'dist\.publish'
& $dotnet publish src\Neuroma\Neuroma.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory

$builtExecutable = Join-Path $publishDirectory 'Neuroma.exe'
$currentExecutable = Join-Path $PSScriptRoot 'dist\Neuroma.exe'
try {
    Copy-Item -LiteralPath $builtExecutable -Destination $currentExecutable -Force -ErrorAction Stop
    Write-Host 'Built dist\Neuroma.exe'
} catch [System.IO.IOException] {
    $nextExecutable = Join-Path $PSScriptRoot 'dist\Neuroma.next.exe'
    Copy-Item -LiteralPath $builtExecutable -Destination $nextExecutable -Force
    Write-Warning 'Neuroma.exe is running, so the update was saved as dist\Neuroma.next.exe.'
}
foreach ($supportFile in @('Install-Neuroma-Kokoro.ps1', 'Start-Neuroma-Kokoro.ps1')) {
    Copy-Item -LiteralPath (Join-Path $publishDirectory $supportFile) `
        -Destination (Join-Path $PSScriptRoot "dist\$supportFile") -Force
}
Write-Host 'Copied local Kokoro setup scripts to dist'
$localConfig = Join-Path $PSScriptRoot 'neuroma.json'
if (Test-Path -LiteralPath $localConfig) {
    Copy-Item -LiteralPath $localConfig -Destination (Join-Path $PSScriptRoot 'dist\neuroma.json') -Force
    Write-Host 'Copied local Kokoro settings to dist\neuroma.json'
}
