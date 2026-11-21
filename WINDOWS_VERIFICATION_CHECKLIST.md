# Aurora Operator — Windows Verification Checklist (Test 10 / manual pass)

Prepared 2026-11-21 (Asia/Karachi) after Phases 1–6. This is the first thing
that cannot be verified on the Linux VM and needs a real Windows 10 22H2+
or Windows 11 machine/VM. Source of every step: `TESTS.md` ("Manual Windows
pass (Test 10)"), `LIMITATIONS.md` §E, and `PHASES_1-6_REPORT.md` §§5–7.

Do NOT claim Windows compatibility until this checklist passes. Linux results
so far: build 0 errors / 32 warnings (all NU1603 PdfPig), TestRunner
102 passed / 0 failed; `dotnet test` ran 0 tests on this VM because the
sandbox egress proxy redirects the testhost localhost channel — an environment
defect, not a code bug. Re-run `dotnet test` on Windows.

## Prerequisites — decide before you start

- [ ] Windows machine/VM identified: ____________________  RAM: ______ GB
      (8 GB+ → `qwen3:4b`; under 8 GB → `qwen3:1.7b`)
- [ ] .NET 8 SDK installed (Visual Studio 2022 + .NET desktop workload recommended)
- [ ] Ollama installed from ollama.com, then `ollama pull qwen3:4b`
      (or `qwen3:1.7b` on < 8 GB). Model download needs internet; after that
      the app's core local features should work offline.
- [ ] Source transferred to Windows — **clean first**: the Linux repo is
      ~638 MB, almost all of it `bin/` + `obj/` build output. Do not ZIP
      those folders. Transfer source + docs + `src/AuroraOperator.Installer/`
      only, or run `dotnet clean` first. No ZIP has been packaged yet.
- [ ] Playwright Chromium installed if testing web research:
      `playwright install chromium`

## Build & automated tests on Windows

- [ ] `dotnet build AuroraOperator.sln -c Release` — result: __________
      (expect 0 errors; 32 NU1603 PdfPig warnings are known — pin stable
      PdfPig 0.1.9 on real nuget.org if they bother you)
- [ ] `dotnet test tests/AuroraOperator.Tests -c Release` — result: ___ / 102
      (this is the run that could not happen on Linux; record the exact count)

## Manual pass — Test 10

| # | Check | Pass / Fail | Notes |
|---|-------|-------------|-------|
| 1 | Launch app; Ollama status dot green; model list shows pulled model | | |
| 2 | Same task in Ask, Plan, Autonomous: Ask changes nothing; Plan stops at WaitingApproval with plan shown; Autonomous executes | | Known wart: Plan mode prompts per step at review, then the engine prompts again at execution for each sensitive step (double prompting, intentional, no scoped plan-approval token yet) |
| 3 | Approve AND deny a Sensitive step (e.g. organise a folder, overwrite an existing file) from Approvals panel; denied step does not execute; audit log records both decisions. Approval is bound to TaskId + ActionId + SHA256 of exact params, 5-min expiry | | |
| 4 | Stop / Pause / Resume from UI; paused shows Paused and resumes; stopped ends Cancelled (incl. cancel while an approval is pending — pending action must never run) | | Pause deadlock was fixed in Phase 5 (async TaskCompletionSource gate) — this is the UI proof |
| 5 | **Notepad flow:** `Open Notepad and write Hello World` — Notepad launches, text is typed, read-back verifies exact match; with a save path, Save dialog saves the file and content is verified byte-for-byte | | **Highest-risk item:** Windows 11 tabbed Notepad may behave differently from the classic Save dialog. If dialog automation is unreliable, the documented fallback is verified direct-write — record which path actually ran |
| 6 | Theme toggle dark/light; model switch in Model Manager (takes effect for new engine runs — restart app); attach a file; double-click a recent task to reload it; restart app and confirm recent tasks persist | | |
| 7 | Web research with Chromium: real report with sources; a CAPTCHA page is recorded as blocked, never bypassed | | No test covers `ResearchAsync` at all on Linux |
| 8 | Kill Ollama mid-task: app reports Ollama offline, does not crash, Ask mode falls back to local summary | | |

## Installer checks (manual, Windows only — never run)

- [ ] Fresh VM, no Ollama: installer downloads Ollama, pulls the right model for the VM's RAM, app launches, status dot green
- [ ] VM with Ollama already installed: installer skips the download
- [ ] VM with 4 GB RAM: `qwen3:1.7b` is pulled, not `qwen3:4b`
- [ ] Kill network mid-download: install aborts and rolls back the app directory (no half-installed app)
- [ ] Uninstall with "remove models": `%USERPROFILE%\.ollama\models` is gone; with "keep models": it remains
- [ ] Installer scripts are in `src/AuroraOperator.Installer/` (`AuroraOperator.nsi`, `InnoSetup.iss`) — never executed; `makensis`/`iscc` were not installed on Linux

## What to report back after the run

For each failed item: the exact step, what happened vs. expected, any error
text, and whether it blocks packaging. A single failed Notepad Save-dialog
step does not invalidate the 102 Linux tests — it narrows the fix to
`WindowsAppControl.SaveNotepadFile` / `NotepadWorkflow`.

## Decision still open

1. Do you have a Windows machine/VM ready now, and how much RAM does it have?
2. When this checklist passes (or the failures are triaged), package the
   cleaned source as a ZIP for transfer — or build the NSIS/Inno installer
   directly on Windows instead?
