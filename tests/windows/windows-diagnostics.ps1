<#
.SYNOPSIS
    Aurora Operator — Windows diagnostics (Phase 7).
    Read-only system checks; the ONLY thing this script writes is its own
    report file. It never installs, uninstalls, starts, stops or changes
    anything on the machine.

.DESCRIPTION
    Checks, each reported PASS / FAIL / WARN / INFO:
      1. Windows version / build (10 22H2+ or 11)
      2. RAM (8 GB+ expected for qwen3:4b; 4 GB+ minimum for qwen3:1.7b)
      3. Free disk space on C:
      4. .NET 8 Desktop Runtime (Microsoft.WindowsDesktop.App 8.x)
      5. Ollama executable present
      6. Ollama process running
      7. Ollama local API answers (GET http://localhost:11434/api/tags)
      8. Installed models (qwen3:4b / qwen3:1.7b expected)
      9. Playwright Chromium present (%LOCALAPPDATA%\ms-playwright)
     10. Aurora Operator install location + AuroraOperator.exe
     11. Start Menu + Desktop shortcuts
     12. Port 11434 bound to loopback only (127.0.0.1 / ::1) — FAIL on 0.0.0.0
     13. Log / data locations (%LOCALAPPDATA%\AuroraOperator)
     14. Uninstaller registry entry

    Usage (from PowerShell, in the folder containing this script):
        powershell -ExecutionPolicy Bypass -File .\windows-diagnostics.ps1

    The report is written to the Desktop as
    aurora-diagnostics-YYYYMMDD-HHmmss.txt (falls back to $env:TEMP when
    the Desktop is not writable). Exit code 0 = no FAIL; 1 = at least one
    FAIL. WARN means "usable, but note it" (e.g. Ollama not running yet).

.NOTES
    Requires Windows PowerShell 5.1+ or PowerShell 7. No admin rights needed.
    Companion docs: WINDOWS_DIAGNOSTICS.md, WINDOWS_TESTING_GUIDE.md,
    WINDOWS_ACCEPTANCE_CHECKLIST.md (tests/windows/).
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$script:Results = [System.Collections.Generic.List[PSCustomObject]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Check,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'WARN', 'INFO')][string]$Result,
        [Parameter(Mandatory)][string]$Details
    )
    $script:Results.Add([PSCustomObject]@{
        Check   = $Check
        Result  = $Result
        Details = $Details
    })
    Write-Host ("[{0}] {1}: {2}" -f $Result, $Check, $Details)
}

function Get-OllamaExe {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'),
        (Join-Path $env:ProgramFiles 'Ollama\ollama.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    $cmd = Get-Command ollama -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

Write-Host 'Aurora Operator — Windows diagnostics (read-only)'
Write-Host ('Started: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Host ''

# ---------------------------------------------------------------- 1. OS --
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $build = [int]$os.BuildNumber
    $caption = $os.Caption.Trim()
    if ($build -ge 22000) {
        Add-Result 'Windows version' 'PASS' ("{0}, build {1} (Windows 11)" -f $caption, $build)
    }
    elseif ($build -ge 19045) {
        Add-Result 'Windows version' 'PASS' ("{0}, build {1} (Windows 10 22H2+)" -f $caption, $build)
    }
    elseif ($build -ge 19041) {
        Add-Result 'Windows version' 'WARN' ("{0}, build {1}: Windows 10, older than 22H2 — update to 22H2 or later" -f $caption, $build)
    }
    else {
        Add-Result 'Windows version' 'FAIL' ("{0}, build {1}: needs Windows 10 22H2 (build 19045) or Windows 11" -f $caption, $build)
    }
}
catch {
    Add-Result 'Windows version' 'FAIL' ("Could not read OS version: {0}" -f $_.Exception.Message)
}

# ---------------------------------------------------------------- 2. RAM --
try {
    $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $gb = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
    if ($gb -ge 8) {
        Add-Result 'RAM' 'PASS' ("{0} GB total — qwen3:4b is the expected model" -f $gb)
    }
    elseif ($gb -ge 4) {
        Add-Result 'RAM' 'WARN' ("{0} GB total — below 8 GB, so qwen3:1.7b is the expected model, not qwen3:4b" -f $gb)
    }
    else {
        Add-Result 'RAM' 'FAIL' ("{0} GB total — below the 4 GB minimum; the app and model will not run reliably" -f $gb)
    }
}
catch {
    Add-Result 'RAM' 'FAIL' ("Could not read memory: {0}" -f $_.Exception.Message)
}

# --------------------------------------------------------- 3. Disk free --
try {
    $drive = Get-PSDrive -Name 'C' -ErrorAction Stop
    $freeGb = [math]::Round($drive.Free / 1GB, 1)
    if ($freeGb -ge 20) {
        Add-Result 'Disk free (C:)' 'PASS' ("{0} GB free on C: (>= 20 GB)" -f $freeGb)
    }
    elseif ($freeGb -ge 10) {
        Add-Result 'Disk free (C:)' 'WARN' ("{0} GB free on C:: enough for the app + one model, but tight" -f $freeGb)
    }
    else {
        Add-Result 'Disk free (C:)' 'FAIL' ("{0} GB free on C:: need at least 10 GB (model ~2.5 GB + .NET runtime + app)" -f $freeGb)
    }
}
catch {
    Add-Result 'Disk free (C:)' 'FAIL' ("Could not read C: free space: {0}" -f $_.Exception.Message)
}

# ------------------------------------------- 4. .NET 8 Desktop Runtime --
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Add-Result '.NET 8 Desktop Runtime' 'FAIL' 'dotnet is not on PATH — install the .NET 8 Desktop Runtime (or SDK) from https://dotnet.microsoft.com/download/dotnet/8.0'
}
else {
    $runtimes = (& dotnet --list-runtimes 2>$null) -join "`n"
    $desktop8 = $runtimes -split "`n" | Where-Object { $_ -match 'Microsoft\.WindowsDesktop\.App 8\.' }
    if ($desktop8) {
        Add-Result '.NET 8 Desktop Runtime' 'PASS' ("Found: {0}" -f ($desktop8 -join '; ').Trim())
    }
    else {
        $anyNet = $runtimes -split "`n" | Where-Object { $_ -match 'Microsoft\.(NETCore|WindowsDesktop)\.App' } | Select-Object -First 3
        Add-Result '.NET 8 Desktop Runtime' 'FAIL' ("No Microsoft.WindowsDesktop.App 8.x runtime found. Installed runtimes: {0}" -f (($anyNet -join '; ').Trim()))
    }
}

# ------------------------------------------------- 5. Ollama executable --
$ollamaExe = Get-OllamaExe
if ($ollamaExe) {
    Add-Result 'Ollama installed' 'PASS' ("ollama.exe found at {0}" -f $ollamaExe)
}
else {
    Add-Result 'Ollama installed' 'FAIL' 'ollama.exe not found under %LOCALAPPDATA%\Programs\Ollama, %ProgramFiles%\Ollama, or PATH — install from https://ollama.com/download'
}

# -------------------------------------------------- 6. Ollama process ---
$ollamaProc = Get-Process ollama -ErrorAction SilentlyContinue
if ($ollamaProc) {
    Add-Result 'Ollama process' 'PASS' ("Ollama is running (PID {0})" -f (($ollamaProc | Select-Object -First 1).Id))
}
else {
    Add-Result 'Ollama process' 'WARN' 'No ollama process is running — start Ollama (it normally starts automatically) and re-run this script'
}

# ------------------------------------------- 7/8. Ollama API + models --
$models = $null
try {
    $tags = Invoke-RestMethod -Uri 'http://localhost:11434/api/tags' -Method Get -TimeoutSec 5 -ErrorAction Stop
    Add-Result 'Ollama API' 'PASS' 'GET http://localhost:11434/api/tags answered (HTTP 200)'
    $models = @($tags.models | ForEach-Object { $_.name })
    if ($models.Count -eq 0) {
        Add-Result 'Ollama models' 'FAIL' 'API answered but no models are installed — run: ollama pull qwen3:4b  (or qwen3:1.7b on < 8 GB RAM)'
    }
    else {
        $expected = if ($models -contains 'qwen3:4b') { 'qwen3:4b present' }
                    elseif ($models -contains 'qwen3:1.7b') { 'qwen3:1.7b present' }
                    else { 'expected model qwen3:4b/qwen3:1.7b NOT present' }
        $level = if ($expected -like '*NOT present*') { 'WARN' } else { 'PASS' }
        Add-Result 'Ollama models' $level ("Installed: {0} — {1}" -f ($models -join ', '), $expected)
    }
}
catch {
    Add-Result 'Ollama API' 'FAIL' ("GET http://localhost:11434/api/tags failed: {0}" -f $_.Exception.Message)
    Add-Result 'Ollama models' 'FAIL' 'Could not list models because the Ollama API did not answer'
}

# ---------------------------------------- 9. Playwright Chromium --------
$playwrightDir = Join-Path $env:LOCALAPPDATA 'ms-playwright'
$chromium = @()
if (Test-Path -LiteralPath $playwrightDir) {
    $chromium = @(Get-ChildItem -LiteralPath $playwrightDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'chromium*' })
}
if ($chromium.Count -gt 0) {
    Add-Result 'Playwright Chromium' 'PASS' ("Found under {0}: {1}" -f $playwrightDir, (($chromium | ForEach-Object { $_.Name }) -join ', '))
}
else {
    Add-Result 'Playwright Chromium' 'FAIL' ("No chromium-* folder under {0} — install with: playwright install chromium (only needed for the browser-automation test, item 11)" -f $playwrightDir)
}

# ------------------------------- 10. Aurora Operator install location --
$installDir = Join-Path $env:ProgramFiles 'Aurora Operator'
$exe = Join-Path $installDir 'AuroraOperator.exe'
if (Test-Path -LiteralPath $exe) {
    $ver = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    Add-Result 'Aurora install location' 'PASS' ("AuroraOperator.exe found at {0} (version {1})" -f $exe, $ver)
}
else {
    Add-Result 'Aurora install location' 'FAIL' ("AuroraOperator.exe not found at {0} — install the app first (or run from the built output and note the path)" -f $exe)
}

# --------------------------------------------------- 11. Shortcuts ------
$startMenuLink = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Aurora Operator\Aurora Operator.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Aurora Operator.lnk'
$missing = @()
if (-not (Test-Path -LiteralPath $startMenuLink)) { $missing += 'Start Menu' }
if (-not (Test-Path -LiteralPath $desktopLink)) { $missing += 'Desktop' }
if ($missing.Count -eq 0) {
    Add-Result 'Aurora shortcuts' 'PASS' 'Start Menu and Desktop shortcuts both exist'
}
else {
    Add-Result 'Aurora shortcuts' 'WARN' ("Missing shortcut(s): {0} (app still runs from {1})" -f ($missing -join ', '), $exe)
}

# --------------------------- 12. Port 11434 bound to loopback only -----
$listeners = @()
$listenerSource = 'none'
try {
    $listeners = @(Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction Stop)
    $listenerSource = 'Get-NetTCPConnection'
}
catch {
    # Older systems / restricted hosts: fall back to netstat (read-only).
    $listenerSource = 'netstat'
    foreach ($line in (netstat -ano -p TCP 2>$null)) {
        if ($line -match '^\s*TCP\s+(\S+):11434\s+\S+\s+LISTENING\s+(\d+)') {
            $listeners += [PSCustomObject]@{ LocalAddress = ($Matches[1] -replace '^\[|\]$', ''); OwningProcess = $Matches[2] }
        }
    }
}
if ($listeners.Count -eq 0) {
    Add-Result 'Port 11434 loopback-only' 'WARN' 'Nothing is listening on port 11434 (Ollama is not running) — re-run with Ollama started; this check cannot pass until it is'
}
else {
    $bad = @($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') })
    $summary = ($listeners | ForEach-Object { "$($_.LocalAddress):11434" }) -join ', '
    if ($bad.Count -gt 0) {
        Add-Result 'Port 11434 loopback-only' 'FAIL' ("Ollama is reachable off-machine: listening on {0} (via {1}). Expected loopback only (127.0.0.1 / ::1) — check the OLLAMA_HOST environment variable" -f $summary, $listenerSource)
    }
    else {
        Add-Result 'Port 11434 loopback-only' 'PASS' ("Listening on loopback only: {0} (via {1})" -f $summary, $listenerSource)
    }
}

# --------------------------------------------- 13. Logs / data locations --
$appData = Join-Path $env:LOCALAPPDATA 'AuroraOperator'
$logsDir = Join-Path $appData 'Logs'
$db = Join-Path $appData 'aurora.db'
$notes = @()
if (Test-Path -LiteralPath $logsDir) {
    $logs = @(Get-ChildItem -LiteralPath $logsDir -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 3)
    $notes += ("Logs dir exists ({0}); newest: {1}" -f $logsDir, (($logs | ForEach-Object { $_.Name }) -join ', '))
}
else {
    $notes += ("Logs dir not found yet ({0}) — it is created on first app start" -f $logsDir)
}
if (Test-Path -LiteralPath $db) {
    $notes += ("Database exists: {0} ({1} KB)" -f $db, [math]::Round((Get-Item -LiteralPath $db).Length / 1KB, 1))
}
else {
    $notes += ("Database not found yet ({0}) — it is created on first app start" -f $db)
}
Add-Result 'Log / data locations' 'INFO' ($notes -join '; ')

# ----------------------------------------- 14. Uninstaller registry -----
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Aurora Operator'
if (Test-Path -LiteralPath $uninstallKey) {
    $display = (Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue).DisplayName
    Add-Result 'Uninstaller registered' 'PASS' ("Registry entry present ({0})" -f $display)
}
else {
    Add-Result 'Uninstaller registered' 'WARN' 'No uninstall registry entry under HKLM\...\Uninstall\Aurora Operator — expected after a proper install; a from-source run has none'
}

# ---------------------------------------------------------------- report --
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportName = "aurora-diagnostics-$stamp.txt"
$reportPath = Join-Path ([Environment]::GetFolderPath('Desktop')) $reportName
try {
    $null = New-Item -ItemType File -Path $reportPath -Force -ErrorAction Stop
}
catch {
    $reportPath = Join-Path $env:TEMP $reportName
}

$report = [System.Collections.Generic.List[string]]::new()
$report.Add('Aurora Operator — Windows diagnostics report')
$report.Add(('Generated: {0} (local time)' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')))
$report.Add(('Computer: {0}   User: {1}' -f $env:COMPUTERNAME, $env:USERNAME))
$report.Add('')
$pass = @($script:Results | Where-Object { $_.Result -eq 'PASS' }).Count
$fail = @($script:Results | Where-Object { $_.Result -eq 'FAIL' }).Count
$warn = @($script:Results | Where-Object { $_.Result -eq 'WARN' }).Count
$info = @($script:Results | Where-Object { $_.Result -eq 'INFO' }).Count
$report.Add(('Summary: {0} PASS, {1} FAIL, {2} WARN, {3} INFO' -f $pass, $fail, $warn, $info))
$report.Add('')
foreach ($r in $script:Results) {
    $report.Add(('[{0}] {1}' -f $r.Result, $r.Check))
    $report.Add(('    {0}' -f $r.Details))
}
$report.Add('')
$report.Add('This script is read-only: it changed nothing on the machine except writing this report.')
$report | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ''
Write-Host ("Summary: {0} PASS, {1} FAIL, {2} WARN, {3} INFO" -f $pass, $fail, $warn, $info)
Write-Host ("Report written to: {0}" -f $reportPath)
if ($fail -gt 0) {
    Write-Host 'Result: FAIL — fix the FAIL items above before running the acceptance checklist.'
    exit 1
}
Write-Host 'Result: no FAIL items.'
exit 0
