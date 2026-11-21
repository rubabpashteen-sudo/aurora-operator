# Aurora Operator — Windows Acceptance Checklist (Phase 7)

Manual acceptance pass on a real Windows machine. **Every item below is
NOT VERIFIED until it is run on Windows and ticked here.** The Linux build
(0 errors, 248 tests passing via the in-process TestRunner as of Phase 8)
proves nothing about Windows behaviour.

Status vocabulary (Phase 8): PASS / FAIL / BLOCKED / NOT TESTED /
ACCEPTED RISK. An item is PASS only with evidence recorded here
(command, date, Windows build, result, log/screenshot path). Code
inspection alone is never a PASS.

How to use: run the items in order following
`WINDOWS_TESTING_GUIDE.md`. For each item, tick the box, write PASS or FAIL,
and note the evidence (file, log line, screenshot). Leave the status as
`NOT VERIFIED` until the item has actually been run.

- Tester: ____________________
- Machine: ____________________ (Windows edition/build, RAM, CPU)
- Date: ____________________
- App version / source: ____________________ (installer version or commit)
- Diagnostics report: `aurora-diagnostics-________.txt` (Desktop)

## Pre-flight

- [ ] Diagnostics script run: `powershell -ExecutionPolicy Bypass -File .\windows-diagnostics.ps1`
      Status: NOT VERIFIED — Result: ______ (___ PASS / ___ FAIL / ___ WARN)
      Report file: ____________________

## The 15 acceptance items

- [ ] **1. App launch** — window opens, no crash, single instance enforced.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **2. Ollama detection** — status green when Ollama is up, offline (no crash) when stopped, recovers without restart.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **3. Model download** — a not-yet-installed model downloads with progress and appears in `ollama list`; a failed download is reported honestly.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **4. Real AI inference** — Ask-mode answers in English and Chinese come from the local model (no local-summary fallback).
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **5. Notepad launch** — `Open Notepad and write …` opens a visible Notepad window within 10 s.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **6. Actual text entry** — `Hello Aurora, this is a Windows test 123.` appears in Notepad character-for-character.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **7. Text verification** — typing step reports read-back exact match; a deliberately altered text is reported as a mismatch.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **8. File saving** — saved file content matches the typed text byte-for-byte; which save path ran (Save dialog vs. verified direct-write fallback) is recorded.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
      Save path that ran: ______ (Save dialog / direct-write fallback)
- [ ] **9. Approval and rejection** — overwriting an existing file asks first; Deny leaves the file unchanged, Approve writes it; both decisions are in `audit.log`.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **10. Task cancellation** — Stop ends the task as Cancelled promptly; Pause/Resume works; Stop during a pending approval never runs the action.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **11. Browser automation** — research report has real sourced findings; CAPTCHA pages are recorded as Blocked, never bypassed.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **12. Memory persistence** — recent tasks and remembered notes survive a full app restart; double-click reloads a task.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **13. Offline operation** — local Ask/Notepad tasks work with the network off; research fails honestly with no fabricated report.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **14. App restart** — clean close leaves no process behind; reopen restores model, recent tasks, and settings.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **15. Uninstallation** — uninstall removes the app, shortcuts, and registry entry; model keep/remove follows the prompt; user data in `%LOCALAPPDATA%\AuroraOperator` survives.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________

## Installer / uninstaller checks

- [ ] **I1. Fresh machine, no Ollama** — installer downloads Ollama, pulls the right model for the machine's RAM, app launches, status green.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **I2. Ollama already installed** — installer skips the Ollama download.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **I3. 4 GB RAM machine** — `qwen3:0.6b` is pulled, not `qwen3:1.7b` or `qwen3:4b`. (Corrected in the final release: a 4 GB machine cannot spare the RAM for 1.7b alongside Windows and the app; app, both installers and the model policy all agree — see RELEASE_NOTES.md.)
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **I4. Network killed mid-download** — install aborts; no half-installed app directory is left behind.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **I5. Uninstall, "remove models"** — `%USERPROFILE%\.ollama\models` is gone.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **I6. Uninstall, "keep models"** — `%USERPROFILE%\.ollama\models` remains.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **I7. Uninstall leaves user data** — `%LOCALAPPDATA%\AuroraOperator` (aurora.db, logs) still exists after uninstall.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________
- [ ] **I8. Ollama left installed** — uninstalling Aurora Operator does not remove Ollama itself.
      Status: NOT VERIFIED — Result: ______ Evidence: ____________________

## Build & automated tests on Windows

- [ ] `dotnet build AuroraOperator.sln -c Release` — result: ______ (expect 0 errors; 40 warnings on a clean rebuild as of Phase 8: 32 NU1603 PdfPig + 4 CS8600 + 2 CS0067 + 2 xUnit2013)
      Status: NOT VERIFIED
- [ ] `dotnet test tests/AuroraOperator.Tests -c Release` — result: ______ / ______ (Linux TestRunner: 248 passed / 0 failed as of Phase 8; re-run `dotnet test` on Windows and record the exact count — it must execute real tests, unlike the Linux sandbox where it runs 0)
      Status: NOT VERIFIED

## Sign-off

- Items passed: ___ / 15
- Installer checks passed: ___ / 8
- Failures (list each with the step, what happened vs. expected, and whether it blocks packaging):

  1. ________________________________________________________________________
  2. ________________________________________________________________________
  3. ________________________________________________________________________

- Accepted for packaging: Yes / No — Signed: ____________________ Date: __________

## Phase 8 additions (2026-11-21) — all BLOCKED until run on Windows

Status vocabulary: PASS / FAIL / BLOCKED / NOT TESTED / ACCEPTED RISK.
Linux evidence exists for the code behind each item (see
`PHASE8_REPORT.md`); a PASS here still needs the Windows run below.

- [ ] **P8-1. Trimmed publish launches** — launch the 270 MB
      self-contained publish; window opens; SQLite loads from the
      flattened layout (`e_sqlite3.dll` at root); no missing-DLL
      errors. Then repeat the Chromium test (P8-2) against the
      trimmed build.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-2. Chromium on the trimmed build** — `playwright install
      chromium`, research one query, download one file, verify the
      file exists and is non-zero; confirm the trimmed build did not
      remove the Windows Playwright runtime.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-3. 32 GB model selection** — on a 32 GB machine the app
      auto-selects qwen3:8b (and the installer pulls 8b); on a
      32 GB machine with < 10 GB free it falls back to 4b/1.7b.
      Below 8 GB it selects qwen3:0.6b.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-4. Inno forced health-check failure** — temporarily break
      the health check (rename the app exe after install files are
      copied, or point the model check at a missing model) and run
      the Inno installer: setup must CANCEL (not report success)
      and the installed app files must be gone afterwards.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-5. Ollama retry observable** — stop Ollama mid-request
      (or block port 11434 briefly): the app must retry briefly and
      then report a clean failure, never hang and never retry
      forever. A 4xx (e.g. unknown model) must fail at once.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-6. Launch allowlist bypass attempts** — ask the agent to
      launch a URL, a `.bat`/`.ps1`/`.msi`, and an unknown `.exe`:
      URL/script must be refused outright; the unknown `.exe` must
      ask for approval first and show the exact target.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-7. Close-app guard** — ask the agent to close
      `explorer` / `svchost`: must be refused. Close ordinary
      Notepad: must ask for approval; denying must close nothing;
      approving must close only Notepad. Confirm no other app closed.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-8. DPAPI preferences + DB ACL** — set a theme/model
      preference, close the app, confirm the value persists; open
      `%LOCALAPPDATA%\AuroraOperator\aurora.db` in a hex viewer and
      confirm the preference value is NOT readable plaintext;
      run `icacls %LOCALAPPDATA%\AuroraOperator\aurora.db` and
      confirm only the current user has access.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-9. Ollama loopback binding (real network test)** —
      `netstat -ano | findstr 11434` shows 127.0.0.1 only; a
      connection attempt to port 11434 from another machine on the
      LAN fails.
      Status: BLOCKED — Result: ______ Evidence: ____________________
- [ ] **P8-10. `dotnet test` executes real tests** — record the
      exact pass/fail count; it must not be 0.
      Status: BLOCKED — Result: ______ Evidence: ____________________
