param(
    [string]$DotNet = 'dotnet',
    [string]$MakeNsis = 'makensis',
    [string]$Python = 'python',
    [string]$OutputDir = 'release-output',
    [string]$RestoreConfig,
    [string]$StageDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo 'src/AuroraOperator.App'
$assets = Join-Path $app 'Assets'
if (!$StageDirectory) { $StageDirectory = [IO.Path]::GetTempPath() }
$stage = Join-Path $StageDirectory ('aurora-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$publish = Join-Path $stage 'app'
$release = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force $release | Out-Null
$setup = Join-Path $release 'AuroraOperator-Setup-0.3.0-rc.1-x64.exe'
$zip = Join-Path $release 'AuroraOperator-Portable-0.3.0-rc.1-x64.zip'
$manifest = Join-Path $stage 'uninstall-files.nsh'

Push-Location $assets
try {
    & $Python build-icon.py
    if ($LASTEXITCODE) { throw 'Icon generation failed.' }
} finally { Pop-Location }

$publishArgs = @('publish', $app, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish, '-m:1')
if ($RestoreConfig) { $publishArgs += ('-p:RestoreConfigFile=' + $RestoreConfig) }
& $DotNet @publishArgs
if ($LASTEXITCODE) { throw 'Self-contained Windows publish failed.' }
foreach ($required in 'AuroraOperator.exe','AuroraOperator.dll','coreclr.dll','e_sqlite3.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) {
        throw "Published payload is missing $required."
    }
}

$files = @(Get-ChildItem -LiteralPath $publish -File -Recurse -Force)
$directories = @(Get-ChildItem -LiteralPath $publish -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending)
$deleteCommands = foreach ($file in $files) {
    $relative = $file.FullName.Substring($publish.Length + 1)
    if ($relative -match '[$"\r\n]') { throw "Invalid payload file name: $relative" }
    '  Delete "$INSTDIR\' + $relative + '"'
}
$removeCommands = foreach ($directory in $directories) {
    $relative = $directory.FullName.Substring($publish.Length + 1)
    if ($relative -match '[$"\r\n]') { throw "Invalid payload directory name: $relative" }
    '  RMDir "$INSTDIR\' + $relative + '"'
}
[IO.File]::WriteAllLines($manifest, [string[]]($deleteCommands + $removeCommands))
$installer = Join-Path $repo 'src/AuroraOperator.Installer/AuroraOperatorRc.nsi'
& $MakeNsis '/V3' "/DRELEASE_DIR=$release" "/DPUBLISH_DIR=$publish" "/DMANIFEST=$manifest" $installer
if ($LASTEXITCODE -or !(Test-Path -LiteralPath $setup)) { throw 'NSIS compiler failed.' }

& tar.exe -a -cf $zip -C $publish .
if ($LASTEXITCODE -or !(Test-Path -LiteralPath $zip)) { throw 'Could not create portable ZIP.' }
$hashes = foreach ($file in $setup, $zip) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $file
    $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($file)
}
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), [string[]]$hashes)
Get-Item -LiteralPath $setup, $zip | Select-Object Name, Length
Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt')
