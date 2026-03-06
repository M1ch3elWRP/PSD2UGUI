param(
    [string]$Source = "$PSScriptRoot\ExportToPNG.jsx",
    [string]$Destination = "C:\Program Files\Adobe\Adobe Photoshop 2026\Presets\Scripts\ExportToPNG.jsx",
    [switch]$Watch
)

function Sync-Once {
    $destDir = Split-Path -Parent $Destination
    if (!(Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Copy-Item -Path $Source -Destination $Destination -Force
    Write-Host ("Synced: {0} -> {1}" -f $Source, $Destination)
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
