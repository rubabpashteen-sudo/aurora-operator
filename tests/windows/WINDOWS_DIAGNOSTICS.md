# Aurora Operator — Windows Diagnostics (Phase 7)

`windows-diagnostics.ps1` is a **read-only** environment check for the
Windows machine that will run Aurora Operator. It verifies the machine is
ready before the manual test pass in `WINDOWS_TESTING_GUIDE.md`. The only
thing it writes is its own report file; it never installs, starts, stops,
or changes anything.

> Note: the script is PowerShell and cannot run on the Linux VM where it
> was written. It has not been executed anywhere yet — its first successful
> run on Windows is itself part of the acceptance pass. If it fails to run,
> record the PowerShell error verbatim in the checklist and fix the script.

## How to run it

In PowerShell, from the `tests/windows/` folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-diagnostics.ps1
```

- No admin rights are needed.
- `-ExecutionPolicy Bypass` applies to that one invocation only; it does
  not change the machine's policy.
- Exit code `0` = no FAIL items; `1` = at least one FAIL. WARN items do
  not change the exit code.
- The report is written to the Desktop as
  `aurora-diagnostics-YYYYMMDD-HHmmss.txt` (falls back to `$env:TEMP` if
  the Desktop is not writable). The path is printed at the end of the run.
  Attach that file to the acceptance checklist.

## What each check means

| # | Check | PASS means | FAIL means (fix this first) |
|---|---|---|---|
| 1 | Windows version | Windows 10 22H2 (build 19045+) or Windows 11 | Older Windows — update before testing |
| 2 | RAM | 8 GB+ (`qwen3:4b` expected) | Under 4 GB — the app/model will not run reliably |
| 3 | Disk free (C:) | 20 GB+ free | Under 10 GB — free space (model ~2.5 GB + runtime + app) |
| 4 | .NET 8 Desktop Runtime | `Microsoft.WindowsDesktop.App 8.x` in `dotnet --list-runtimes` | Install the .NET 8 Desktop Runtime (or SDK) |
| 5 | Ollama installed | `ollama.exe` found (LocalAppData, Program Files, or PATH) | Install Ollama from https://ollama.com/download |
| 6 | Ollama process | An `ollama` process is running | Start Ollama and re-run (WARN, not FAIL — see below) |
| 7 | Ollama API | `GET http://localhost:11434/api/tags` answers | Ollama is installed but not answering: start it, check the port |
| 8 | Ollama models | At least one model installed; `qwen3:4b`/`qwen3:1.7b` present | Run `ollama pull qwen3:4b` (or `qwen3:1.7b` on < 8 GB) |
| 9 | Playwright Chromium | A `chromium-*` folder under `%LOCALAPPDATA%\ms-playwright` | Run `playwright install chromium` (only needed for guide item 11) |
| 10 | Aurora install location | `AuroraOperator.exe` exists under `%ProgramFiles%\Aurora Operator` | Install the app (or note the from-source path you ran instead) |
| 11 | Aurora shortcuts | Start Menu + Desktop shortcuts exist | Missing shortcuts (WARN — the app still runs from its folder) |
| 12 | Port 11434 loopback-only | Something listens on 11434 **only** on 127.0.0.1 / ::1 | A listener on 0.0.0.0 or another address — see below |
| 13 | Log / data locations | Lists `%LOCALAPPDATA%\AuroraOperator\Logs` and `aurora.db` | INFO only — these appear after the first app start |
| 14 | Uninstaller registered | Registry entry under `HKLM\...\Uninstall\Aurora Operator` | WARN when running from source (no installer was used) |

### WARN vs FAIL

- **WARN** means "usable, but note it": Ollama not running yet (checks 6,
  12), RAM under 8 GB (check 2 — expect `qwen3:1.7b`), a missing shortcut
  (check 11), or no uninstall entry for a from-source run (check 14).
  Record WARNs in the checklist, but they do not block the test pass.
- **FAIL** means "fix before testing": the environment cannot run the app
  or a test item as written. The guide's items assume every FAIL is fixed.

### Check 12 in detail — port 11434 must be loopback-only

This is the security-critical check. Ollama's API has no authentication:
anything that can reach port 11434 can use the model and read the local
models list. It must only ever listen on the loopback interface.

- The script first tries `Get-NetTCPConnection -LocalPort 11434 -State
  Listen` and falls back to `netstat -ano -p TCP` (both read-only).
- **PASS:** listeners are `127.0.0.1:11434` and/or `[::1]:11434` only.
- **FAIL:** any listener on `0.0.0.0:11434` (all interfaces) or a LAN
  address. Usual cause: the `OLLAMA_HOST` environment variable is set to
  `0.0.0.0` (or `0.0.0.0:11434`). Fix: remove that variable (or set it to
  `127.0.0.1:11434`), restart Ollama, and re-run the script.
- **WARN:** nothing listens on 11434 at all — Ollama is not running. Start
  it and re-run; this check cannot pass until Ollama is up.

To check by hand:

```powershell
Get-NetTCPConnection -LocalPort 11434 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess
# or
netstat -ano -p TCP | findstr ":11434"
```

## After the run

1. Fix every FAIL, then re-run the script until the summary shows 0 FAIL.
2. Copy the report file from the Desktop into `tests/windows/evidence/`
   and record its name in `WINDOWS_ACCEPTANCE_CHECKLIST.md`.
3. If a check reports something the table above does not explain, paste
   the report line into the checklist under the matching item — do not
   guess.
