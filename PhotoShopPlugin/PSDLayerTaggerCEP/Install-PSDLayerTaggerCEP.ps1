$ErrorActionPreference = "Stop"

$Source = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExtensionId = "com.tzui.psdtools.layer-tagger"
$CepRoot = Join-Path $env:APPDATA "Adobe\CEP\extensions"
$Destination = Join-Path $CepRoot $ExtensionId

New-Item -ItemType Directory -Force $CepRoot | Out-Null

$resolvedSource = (Resolve-Path $Source).Path
$resolvedDestination = $null
if (Test-Path $Destination) {
    $resolvedDestination = (Resolve-Path $Destination).Path
}

if ($resolvedDestination -and $resolvedSource -ieq $resolvedDestination) {
    Write-Host "Extension is already running from the CEP extension directory."
}
else {
    if (Test-Path $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

$csxsVersions = @("9", "10", "11")
foreach ($version in $csxsVersions) {
    $key = "HKCU:\Software\Adobe\CSXS.$version"
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name PlayerDebugMode -Value "1" -PropertyType String -Force | Out-Null
}

Write-Host "Installed PSD Layer Tagger CEP:"
Write-Host "  $Destination"
Write-Host ""
Write-Host "Restart Photoshop, then open: Window > Extensions > PSD Layer Tagger"
