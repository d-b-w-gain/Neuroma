$ErrorActionPreference = 'Stop'
$localSdk = 'C:\Users\dbwga\.dotnet-sdk\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) { $dotnet = $localSdk }
else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw 'The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0' }
    $dotnet = $command.Source
}
& $dotnet run --project tests\Neuroma.Tests\Neuroma.Tests.csproj -c Release
& $dotnet publish src\Neuroma\Neuroma.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o dist
Write-Host 'Built dist\Neuroma.exe'
