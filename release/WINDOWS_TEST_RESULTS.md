# Aurora Operator 0.2.0 — Windows Test Results (final release)

**Build machine:** Linux Ubuntu 24.04.5 LTS, x86_64, .NET SDK 8.0.428,
2026-11-21, installer rebuilt 2026-11-22. **This is not a Windows machine.** Every Windows-only item
below is therefore BLOCKED or NOT TESTED — nothing here is a Windows
acceptance pass. A PASS means actual execution evidence on the stated
machine; code inspection and mocked tests are never a Windows PASS.

## First real Windows execution evidence (2026-11-22, user PC)

The user ran the first installer on their real Windows PC (Ollama
already installed and running — `ollama list` showed `qwen3.5:4b`,
3.3 GB — so the Ollama API did answer). The installer still failed
with **"Ollama did not start (its local API did not answer within
~30 seconds)"** and rolled back; its log showed the poll loop running
all 10 iterations (`cmd /c ping -n 4 127.0.0.1 > nul` repeated).
Root cause, verified against the official NSIS wiki ("NsExec
plug-in"): `nsExec::ExecToStack` pushes the return value on top of the
stack and the printed output below it, so the FIRST Pop is the exit
code — the installer popped them backwards at every call site. The
fixed installer below has never executed on Windows; the user's
re-run is its first execution test. Status of that first run:
installer execution on Windows — **FAIL (bug found and fixed)**,
superseded by the fixed build.

## Linux-executed results (evidence on this machine)

| Check | Result | Evidence |
| --- | --- | --- |
| Clean solution build | **PASS** — 0 errors, 40 warnings | `dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true --no-incremental`, 2026-11-21. 32× NU1603 PdfPig + 4× CS8600 + 2× CS0067 + 2× xUnit2013. Incremental rebuilds show 32 warnings (up-to-date projects do not re-emit); 40 is the clean-build count. |
| Custom TestRunner | **PASS** — 268 passed, 0 failed, 268 total | `dotnet run --project tests/AuroraOperator.TestRunner -c Release --no-build`, 2026-11-22. 248 Phase 8 + 15 final-release cases + 5 new NSIS pop-order regression cases (`NsisInstallerTests.cs`). |
| Official `dotnet test` | **FAIL (environment)** — 0 tests run, run aborted | `dotnet test tests/AuroraOperator.Tests -c Release --no-build`, 2026-11-21: "vstest.console process failed to connect to testhost process after 90 seconds. Test Run Aborted." Sandbox egress proxy redirects the testhost localhost channel to the proxy (see `TESTS.md`). Test-project config is correct. **Must be run on Windows; a 0-test run is not a pass.** |
| Fresh self-contained publish | **PASS (cross-compile only)** | `dotnet publish src/AuroraOperator.App -c Release -r win-x64 --self-contained true`, 2026-11-21: 270 MB, 815 files, `AuroraOperator.exe` is PE32+ GUI x86-64, `e_sqlite3.dll` at root, `.playwright/node/win32_x64/node.exe` present. Never executed. |
| Fresh framework-dependent publish | **PASS (cross-compile only)** | Same command with `--self-contained false`: 109 MB, 357 files, PE32+ exe, `e_sqlite3.dll` at root. Never executed. |
| NSIS installer compile | **PASS** | `makensis AuroraOperator.nsi` (NSIS 3.08-2 on Linux), 2026-11-22, exit 0, after the nsExec pop-order fix. Output `AuroraOperator-Setup-0.2.0-x64.exe`, 110,168,298 bytes, PE32 GUI, SHA-256 `4b9b361f8ddac147385dc593eb42c5ad39f9240f6400d02cf66b7176e6ffe50a`, CRC 0xB913284A. Install data 280,912,871 bytes uncompressed vs publish 280,652,815 bytes (difference = uninstaller stub). The previous binary (110,168,296 bytes, SHA-256 `a185abb1…`) is superseded — it could never pass its own Ollama check. Recompiled from the final script after a comment-only header edit; identical hash and CRC, confirming comments do not change the binary. |
| Installer contents | **PASS (by byte count)** | The installer's uncompressed data size matches the publish byte count; `File /r` would have failed the compile if the publish path were missing. Full content extraction was not possible here (7zr cannot open NSIS archives; full 7z was not installable). |
| Inno Setup compile | **BLOCKED** | `iscc` is Windows-only; no compiler on Linux. Script updated and consistent (0.2.0, model policy, health-check fix) but never compiled. |
| Ollama server present | **NOT TESTED** | `curl -m 3 http://localhost:11434/api/tags` → exit 7 (no server on this machine). No inference claimed. |
| Portable ZIP | **PASS** | `AuroraOperator-Portable-0.2.0-x64.zip`, 110,283,558 bytes, 870 entries, `AuroraOperator.exe` at the root, uncompressed total 280,652,815 bytes = the publish byte count exactly. SHA-256 `7b25faddd76f89ca97f6b21b250a8ce2d867a299c67ef18412a891bb9320ca54`. Never executed on Windows. |

## Windows acceptance checklist — all BLOCKED / NOT TESTED

From `tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md` (items 1–15, installer
checks I1–I8, Phase 8 additions P8-1–P8-10). None has Windows execution
evidence yet. The user test must fill these in.

| Item | Status |
| --- | --- |
| 1. Application launches without crashing | BLOCKED |
| 2. Ollama detection and recovery work | BLOCKED |
| 3. Model download works and reports progress | BLOCKED |
| 4. Actual local AI inference works in English and Chinese | BLOCKED |
| 5. Notepad launches | BLOCKED |
| 6. Text is entered correctly | BLOCKED |
| 7. Text read-back verification works | BLOCKED |
| 8. File saving works and saved content matches | BLOCKED |
| 9. Approval and rejection work correctly | BLOCKED |
| 10. Pause, resume and cancellation work | BLOCKED |
| 11. Browser research works with real sourced results | BLOCKED |
| 12. Memory persists across restarts | BLOCKED |
| 13. Offline behaviour is honest and safe | BLOCKED |
| 14. Application restart restores the expected state | BLOCKED |
| 15. Uninstallation behaves correctly | BLOCKED |
| Installer I1–I8 (fresh machine, existing Ollama, 4 GB model, interrupted download, model keep/remove, user-data preservation, Ollama left installed) | BLOCKED |
| P8-1 Trimmed publish launches | BLOCKED |
| P8-2 Chromium on the trimmed build | BLOCKED |
| P8-3 32 GB model selection | BLOCKED |
| P8-4 Inno forced health-check failure cancels installation | BLOCKED |
| P8-5 Ollama retry observable and bounded | BLOCKED |
| P8-6 App-launch bypass attempts rejected/approval-gated | BLOCKED |
| P8-7 Close-app protections work (PID-scoped, protected processes) | BLOCKED |
| P8-8 DPAPI preferences and database ACLs work on Windows | BLOCKED |
| P8-9 Ollama loopback binding verified (`netstat`) | BLOCKED |
| P8-10 Official `dotnet test` executes real tests | BLOCKED |

## Security items — code evidence only (not Windows acceptance)

- F5 app launch: code FIXED (allowlist + raw-primitive refusal), 15 new
  Linux tests pass. Live bypass attempts on Windows: BLOCKED.
- F20 close app: code FIXED (PID-scoped close, graceful first, force only
  when separately approved), Linux tests pass. Live test with real
  Notepad instances: BLOCKED.
- F13 log privacy: PARTIAL (improved). F14 database: PARTIAL (DPAPI
  preferences only; conversations/tasks plaintext by design). Windows
  DPAPI/ACL execution: BLOCKED.

## Verdict for this release

**CONDITIONAL — USER TESTING REQUIRED.** A genuine installer was compiled
and is downloadable, the code builds cleanly, and 268 Linux tests pass.
But no Windows runtime, installer-execution, Ollama, Chromium or Notepad
test has ever passed. The first Windows run (2026-11-22) FAILED on the
nsExec bug above and exposed it; the fixed installer awaits its first
execution test. This release candidate exists so the user can perform
those tests — it is not production-ready.
