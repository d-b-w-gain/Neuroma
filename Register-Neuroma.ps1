$ErrorActionPreference = 'Stop'
$exePath = Join-Path $PSScriptRoot 'Neuroma.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    $distPath = Join-Path $PSScriptRoot 'dist\Neuroma.exe'
    if (Test-Path -LiteralPath $distPath) { $exePath = $distPath }
    else { throw 'Neuroma.exe was not found. Run build.ps1 first.' }
}
$command = '"' + $exePath + '" "%1"'; $classes = 'HKCU:\Software\Classes'
New-Item -Path "$classes\Neuroma.epub\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$classes\Neuroma.epub" -Name '(Default)' -Value 'Neuroma EPUB'
New-Item -Path "$classes\Neuroma.epub\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$classes\Neuroma.epub\DefaultIcon" -Name '(Default)' -Value ('"' + $exePath + '",0')
Set-ItemProperty -Path "$classes\Neuroma.epub\shell\open\command" -Name '(Default)' -Value $command
New-Item -Path "$classes\.epub\OpenWithProgids" -Force | Out-Null
New-ItemProperty -Path "$classes\.epub\OpenWithProgids" -Name 'Neuroma.epub' -Value ([byte[]]@()) -PropertyType Binary -Force | Out-Null
Write-Host 'Neuroma is registered as an EPUB reader for this Windows account.'
Write-Host 'Use Open with > Choose another app on an EPUB file to make it the default.'

