$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root '.data/tools'
$destination = Join-Path $tools 'inno-6.7.3'
$download = Join-Path $tools 'innosetup-6.7.3.exe'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
if (Test-Path -LiteralPath (Join-Path $destination 'ISCC.exe')) { return }
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download -UseBasicParsing
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
    throw 'Inno Setup publisher signature could not be verified. The downloaded file was not run.'
}
# Official portable mode: compiler stays inside the workspace, no machine-wide installation.
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/PORTABLE=1','/CURRENTUSER','/NOICONS',('/DIR="' + $destination + '"'))
$process = Start-Process -FilePath $download -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Compiler extraction failed: $($process.ExitCode)" }
if (!(Test-Path -LiteralPath (Join-Path $destination 'ISCC.exe'))) { throw 'Compiler extraction produced no ISCC.exe.' }
