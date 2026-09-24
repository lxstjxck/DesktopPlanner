param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [string]$CompilerPath,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts/publish/win-x64'
if (!$CompilerPath) {
    $portable = Join-Path $root '.data/tools/inno-6.7.3/ISCC.exe'
    $installed = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
    if (Test-Path -LiteralPath $portable) { $CompilerPath = $portable }
    elseif (Test-Path -LiteralPath $installed) { $CompilerPath = $installed }
    else {
        & (Join-Path $PSScriptRoot 'Get-InstallerCompiler.ps1')
        $CompilerPath = $portable
    }
}
if (!(Test-Path -LiteralPath $CompilerPath)) { throw "Inno Setup compiler not found: $CompilerPath" }
if (!$SkipPublish) {
    # Publish to a fresh directory to prevent stale binaries entering an update.
    $stage = Join-Path $root ('artifacts/publish/build-' + [guid]::NewGuid().ToString('N'))
    & dotnet publish (Join-Path $root 'src/DesktopPlanner.App') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' "-p:Version=$Version" '-p:DebugType=None' '-p:DebugSymbols=false' -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    $publish = $stage
}
foreach ($required in @('DesktopPlanner.App.exe','DesktopPlanner.App.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll','e_sqlite3.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Incomplete self-contained payload: $required" }
}
& $CompilerPath /Q "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $root 'installer/DesktopPlanner.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $root "artifacts/installer/DesktopPlanner-Setup-$Version-win-x64.exe"
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($setup + '.sha256', "$hash  $([IO.Path]::GetFileName($setup))`n")
Write-Host "Installer ready: $setup"
