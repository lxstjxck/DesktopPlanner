param([switch]$Fullscreen, [switch]$DesktopInput)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
$previousDataDirectory = $env:DESKTOPPLANNER_DATA_DIR
try {
    $smokeBuild = Join-Path $projectRoot '.data/smoke-build'
    dotnet build src/DesktopPlanner.App --no-restore --output $smokeBuild
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $env:DESKTOPPLANNER_DATA_DIR = Join-Path $projectRoot ('.data/smoke-' + [Guid]::NewGuid().ToString('N'))
    $executable = Join-Path $smokeBuild 'DesktopPlanner.App.exe'
    $arguments = if ($Fullscreen) { @('--smoke-test', '--fullscreen-smoke') } else { @('--smoke-test') }
    if ($DesktopInput) { $arguments += '--desktop-input-smoke' }
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw "Smoke test timed out; inspect process $($process.Id) and logs in $env:DESKTOPPLANNER_DATA_DIR" }
    if ($process.ExitCode -ne 0) { throw "Smoke test exit code: $($process.ExitCode)" }
    foreach ($name in @('Todo', 'Week', 'Completed', 'Inbox', 'Notes')) {
        if (-not (Test-Path -LiteralPath (Join-Path $env:DESKTOPPLANNER_DATA_DIR "$name.png"))) { throw "Missing image: $name" }
    }
    $textLogs = @(Get-ChildItem -LiteralPath (Join-Path $env:DESKTOPPLANNER_DATA_DIR 'logs') -Filter '*.txt')
    if ($textLogs.Count -eq 0) { throw 'Missing text logs' }
    $logText = Get-Content -LiteralPath $textLogs[0].FullName -Raw
    if ($logText -notmatch 'Data reset cancelled' -or $logText -notmatch 'All widget data and undo history cleared') { throw 'Missing reset diagnostics' }
    Write-Output "Smoke passed. Database, logs and window images: $env:DESKTOPPLANNER_DATA_DIR"
}
finally {
    $env:DESKTOPPLANNER_DATA_DIR = $previousDataDirectory
    Pop-Location
}

