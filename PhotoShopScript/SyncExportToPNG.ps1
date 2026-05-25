param(
    [string]$Source = "$PSScriptRoot\ExportToPNG.jsx",
    [string[]]$Destination,
    [switch]$Watch
)

function Get-DefaultDestinations {
    $adobeRoot = "C:\Program Files\Adobe"
    if (!(Test-Path $adobeRoot)) {
        return @()
    }

    $destinations = Get-ChildItem -Path $adobeRoot -Directory -Filter "Adobe Photoshop *" -ErrorAction SilentlyContinue |
        Sort-Object Name |
        ForEach-Object { Join-Path $_.FullName "Presets\Scripts\ExportToPNG.jsx" }

    return @($destinations)
}

function Resolve-Destinations {
    if ($Destination -and $Destination.Count -gt 0) {
        return @($Destination)
    }

    $auto = Get-DefaultDestinations
    if ($auto.Count -eq 0) {
        throw "No Photoshop installation found under C:\Program Files\Adobe"
    }

    return $auto
}

function Sync-Once {
    $destinations = Resolve-Destinations
    foreach ($dest in $destinations) {
        $destDir = Split-Path -Parent $dest
        if (!(Test-Path $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }

        Copy-Item -Path $Source -Destination $dest -Force
        Write-Host ("Synced: {0} -> {1}" -f $Source, $dest)
    }
}

Sync-Once

if ($Watch) {
    Write-Host "Watching for changes. Press Ctrl+C to stop."
    $watcher = New-Object System.IO.FileSystemWatcher
    $watcher.Path = Split-Path -Parent $Source
    $watcher.Filter = Split-Path -Leaf $Source
    $watcher.NotifyFilter = [System.IO.NotifyFilters]::LastWrite
    $watcher.EnableRaisingEvents = $true

    $handler = Register-ObjectEvent -InputObject $watcher -EventName Changed -Action {
        Start-Sleep -Milliseconds 200
        try { Sync-Once } catch {}
    }

    try {
        while ($true) { Wait-Event | Out-Null }
    } finally {
        Unregister-Event -SourceIdentifier $handler.Name -ErrorAction SilentlyContinue
        $watcher.Dispose()
    }
}
