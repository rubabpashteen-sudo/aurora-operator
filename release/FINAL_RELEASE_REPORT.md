# Final Release Report — Aurora Operator 0.2.0 (Release Candidate)

**Date:** 2026-11-21 · **Built on:** Linux Ubuntu 24.04.5 LTS, .NET SDK 8.0.428
· **Deliverable:** genuine compiled NSIS installer in `release/`.
**Verdict: CONDITIONAL — USER TESTING REQUIRED** (see §7).

## 1. Audit findings (final tree, verified against code — not reports)

- **RAM/model contradiction — RESOLVED.** Phase 8 policy (below 8 GB →
  `qwen3:0.6b`, 8 GB → `1.7b`, 16 GB → `4b` with ≥ 6 GB free, 32 GB → `8b`
  with ≥ 10 GB free) is what `ModelCatalog.Profiles`, `SelectBestModel`
  and both installer scripts implement. The old checklist line I3 ("4 GB
  machine → `qwen3:1.7b`") contradicted it and was wrong: a 1.4 GB model
  plus Windows plus the app leaves a 4 GB machine with almost no headroom.
  The checklist line now says `qwen3:0.6b`, and the reasoning is in
  `release/RELEASE_NOTES.md`. App, NSIS, Inno, docs and tests now agree
  on one policy.
- **Warnings — reconciled.** Clean rebuild: **40 warnings, 0 errors**
  (32× NU1603 PdfPig + 4× CS8600 + 2× CS0067 + 2× xUnit2013). The "32
  warnings" in one security report is the incremental-rebuild count
  (up-to-date projects do not re-emit). Nothing was suppressed; a new
  CA1416 introduced by the first version of the new tests was fixed with
  a platform guard, not a suppression.
- **Test counts — reconciled.** TestRunner **263 passed / 0 failed**
  (248 Phase 8 + 15 new final-release cases in `Phase9Tests.cs`).
  `dotnet test` executed **0 tests** on this machine (run aborted after
  90 s; sandbox proxy defect, re-confirmed on the final tree).
- **Publish sizes — re-measured from the final tree, not cited from old
  reports.** Self-contained 270 MB / 815 files; framework-dependent
  109 MB / 357 files. Both PE32+ GUI executables, `e_sqlite3.dll` at
  root, Windows Playwright Node (`win32_x64/node.exe`) present.
- **Inno health check:** verified from the official Inno docs in Phase 8
  that bare `Abort()` in `ssPostInstall` silently continues; the fix
  (`HealthCheckFailed` + wizard close + cancel handler) is in the script
  but has never executed anywhere.
- **GitHub Actions:** no git repository exists in this workspace
  (`git remote -v` → "not a git repository") and no remote or
  credentials are available, so no Windows-runner workflow could be
  created or pushed. That is the exact blocker; the local NSIS installer
  in `release/` is the deliverable instead. If the user later creates a
  repository, a `windows-latest` workflow should build, run `dotnet
  test`, publish win-x64 and compile both installers there.

## 2. Fixes made in this final release

| Area | Change | Tests |
| --- | --- | --- |
| F20 close-app | **PID-scoped close** (`AppProcessCloser`, `Tools/AppControl.cs`): a name resolves to matching processes; exactly one safe match may close, two or more close NOTHING (failure lists the PIDs). Graceful close first; no force-kill unless the approved args contain `force=true`; force kills only that one process, never the tree. `CloseProcessById` verifies the PID's real name matches the named app. Both controls (`ProcessAppControl`, `WindowsAppControl`) delegate to it; `AppTools` close-app accepts `pid=`/`force=` and reports exactly which PID closed or why not. | `Phase9Tests.cs`: ambiguity closes nothing, PID close closes only that PID, graceful failure without force, wrong-name PID refused, unknown PID refused, AppTools ambiguity lists PIDs, AppTools by-PID force flow, `FindProcesses` identity. |
| F20 approval dialog | `ApprovalService.CreateRequest` now puts the canonical parameters in `ExactCommandOrChange`, so the dialog shows the exact PID and `force=true` — previously it showed only title + description. The hash still binds the approval. | `ApprovalRequest_ExactText_NamesPidAndForce` |
| F5 launch | Defense in depth: `ProcessAppControl.LaunchApp` itself now refuses `Rejected` targets (URLs, shell protocols, scripts, installers, shortcuts). Previously only `AppTools`/`WindowsAppControl` checked; the raw primitive was documented as unchecked. `RequiresApproval` targets remain gated by the engine. | `ProcessAppControl_LaunchApp_RejectsDangerousTargets` (7 cases) |
| F13/F14 | No change to behaviour. Redaction improvements kept; full user requests are not logged; conversations/tasks remain plaintext by design and are documented as such; DPAPI preference protection + DB ACL unchanged, Windows execution BLOCKED. | Existing Phase 8 tests |
| RAM policy | Checklist I3 corrected to `qwen3:0.6b` for a 4 GB machine; NSIS/Inno size comments corrected 384 MB → 270 MB; NSIS `OutFile` is now the release name `AuroraOperator-Setup-0.2.0-x64.exe`; README/BUILD.md installer names corrected. | Model-selection tests (Phase 8) still pass unchanged |

## 3. Final build / test / publish numbers (final tree)

- `dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true --no-incremental` → **Build succeeded, 0 errors, 40 warnings** (2026-11-21).
- `dotnet run --project tests/AuroraOperator.TestRunner -c Release --no-build` → **263 passed, 0 failed, 263 total**.
- `dotnet test tests/AuroraOperator.Tests -c Release --no-build` → **Test Run Aborted, 0 tests** (vstest testhost connection, sandbox proxy).
- Fresh publish (deleted old outputs first): self-contained **270 MB / 815 files**, framework-dependent **109 MB / 357 files**; default installer source `src/AuroraOperator.App/bin/Release/net8.0-windows/win-x64/publish/` re-published and byte-identical to `publish/win-x64-self-contained/` (`diff -rq` clean).
- NSIS compile: `makensis AuroraOperator.nsi` (NSIS 3.08-2, installed on Linux from the Ubuntu noble pool after the sandbox `apt-get update` was killed twice by the runtime — see §6) → exit 0. Output `AuroraOperator-Setup-0.2.0-x64.exe`, **110,168,296 bytes**, PE32 GUI, SHA-256 `a185abb1e47303a7801f1b45c694b2aef3b86777ef16e54b0ee590523c807f97`. Install data 280,912,871 bytes uncompressed vs publish 280,652,815 bytes (difference = uninstaller stub). Recompiled from the final tree after comment-only edits; identical hash and CRC.
- Portable ZIP: `AuroraOperator-Portable-0.2.0-x64.zip`, **110,283,558 bytes**, 870 entries, `AuroraOperator.exe` at root, uncompressed total exactly the publish byte count. SHA-256 `7b25faddd76f89ca97f6b21b250a8ce2d867a299c67ef18412a891bb9320ca54`.

## 4. Installer — what was verified and what was not

Verified on Linux: compiles cleanly; output is a genuine NSIS PE32 installer; its uncompressed payload size matches the publish byte count; `File /r` would have failed the compile had the publish path been missing; version 0.2.0; distinct output names (NSIS primary, Inno separate); Ollama loopback pinning and model thresholds in both scripts; rollback and health-check logic by code inspection.
**Not verified (Windows only):** installer execution, Ollama download/install, model pull and verification, shortcuts, health-check pass/fail paths, Inno forced-failure cancellation, uninstall and model keep/remove, user-data preservation. The Inno installer was not compiled (`iscc` is Windows-only).

## 5. Security status

**10 PASS, 2 PARTIAL (F13, F14), 8 FIXED, 0 FAIL** — code-review findings, not
Windows acceptance. F5 and F20 fixes above are code + Linux-test evidence
only; live Windows bypass/close attempts are BLOCKED. F13/F14 residuals are
disclosed in `release/RELEASE_NOTES.md` and `SECURITY_REVIEW.md`. Ollama
loopback is enforced in app, client and both installers; real `netstat`
proof needs Windows.

## 6. Environment notes / blockers encountered

- The sandbox killed the first `apt-get update` (SIGTERM after ~254 s) and
  the package lists were empty, so `apt-get install nsis` reported "Unable
  to locate package". Fix: the Ubuntu `ubuntu.sources` mirror list exists;
  `apt-get update` was re-run in the background and was progressing
  through the mirrors, but the NSIS debs were fetched directly from the
  Ubuntu pool instead (`nsis-common_3.08-2_all.deb` + `nsis_3.08-2_amd64.deb`,
  azure.archive.ubuntu.com) and installed with `dpkg -i`. A `pkill -f
  apt-get` attempt killed the agent's own shell (its command line matched)
  — no harm done; the apt chain was already stopped.
- `7zr` cannot open NSIS archives, so installer-content verification rests
  on makensis's install-data byte count (see §3); the full `7z` package was
  not installable from the pool path tried.
- No git repository, no remote, no credentials — no GitHub Actions workflow
  could be created or run.

## 7. Remaining blockers and final verdict

**Remaining blockers:** every Windows runtime/installer/Ollama/Chromium/
Notepad/DPAPI check in `release/WINDOWS_TEST_RESULTS.md` is BLOCKED until a
real Windows machine runs the installer and the acceptance checklist.
**Verdict: CONDITIONAL — USER TESTING REQUIRED.** The installer is genuine
and downloadable, the code builds cleanly and 263 tests pass, but no Windows
execution evidence exists. This is a release candidate for the user to test,
not a production release.

## 8. Files changed in this final release

- Code: `src/AuroraOperator.Core/Tools/AppControl.cs` (`AppProcessCloser`,
  `ProcessInfo`, PID close, launch-policy primitive check),
  `src/AuroraOperator.Core/Tools/AppTools.cs` (close-app pid/force flow),
  `src/AuroraOperator.App/Services/WindowsAppControl.cs` (delegates to
  `AppProcessCloser`), `src/AuroraOperator.Core/Security/ApprovalService.cs`
  (exact parameters in the dialog text),
  `src/AuroraOperator.Core/Security/ProcessSafety.cs` (doc update).
- Tests: `tests/AuroraOperator.Tests/Phase9Tests.cs` (new, 15 cases).
- Installers: `src/AuroraOperator.Installer/AuroraOperator.nsi` (OutFile →
  `AuroraOperator-Setup-0.2.0-x64.exe`, validation status, 270 MB figures),
  `src/AuroraOperator.Installer/InnoSetup.iss` (270 MB comments).
- Docs: `tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md` (I3 corrected),
  `SECURITY_REVIEW.md` (F5/F20 final-release addenda),
  `publish/README.md` (final-release header, installer names, model
  policy), `README.md`, `BUILD.md` (installer names).
- Release: `release/` — installer (×2 names, same file), portable ZIP,
  `SHA256SUMS.txt`, `RELEASE_NOTES.md`, `INSTALLATION_GUIDE.md`,
  `WINDOWS_TEST_RESULTS.md`, this report.
- `build.log` appended (history preserved).

## 9. Download links (2026-11-21)

- Installer upload via Muse built-in storage **FAILED: HTTP 413 Payload
  Too Large** — the 105 MB installer exceeds that service's payload
  limit (the portable ZIP, also ~105 MB, exceeds it too). The installer
  is therefore delivered as the workspace file
  `release/AuroraOperator-Setup-0.2.0-x64.exe` (sandbox download link;
  SHA-256 `a185abb1…807f97`). No GitHub Release exists (no repository,
  no remote, no credentials) — that remains the one step that would give
  a public download URL: create a repository, push this tree, and build
  the installer on a `windows-latest` runner.
- Small release documents uploaded successfully (expire 2026-11-23T18:42Z):
  SHA256SUMS.txt, RELEASE_NOTES.md, INSTALLATION_GUIDE.md,
  WINDOWS_TEST_RESULTS.md, FINAL_RELEASE_REPORT.md — URLs recorded in
  the parent agent's delivery.

## 10. Addendum 2026-11-22 — NSIS nsExec pop-order bug (found on the user's real Windows PC)

**What happened:** the user ran the §3 installer on their real Windows
PC. Their Ollama was already installed and running — `ollama list`
showed `qwen3.5:4b`, 3.3 GB — so the Ollama API did answer. The
installer still failed with "Ollama did not start (its local API did
not answer within ~30 seconds)" and rolled back; its log showed the
poll loop running all 10 iterations.

**Verified root cause:** `nsExec::ExecToStack` pushes the return value
on top of the stack and the printed output below it, so the FIRST Pop
is the exit code and the SECOND Pop is the output — official NSIS
wiki, "NsExec plug-in": `Pop $0 # return value/error/timeout`,
`Pop $1 # printed text`; "ExecToStack will push up to
${NSIS_MAX_STRLEN} characters of output onto the stack after the
return value." The shipped script had them backwards at EVERY
ExecToStack call site (10 sites). Consequences, each verified against
the old script: the Ollama poll compared the empty output to "0" and
could never break out (the exact observed failure); RAM detection
received the exit code "0" and always fell back to 4 GB → `qwen3:0.6b`;
the disk-space check compared the output to "0" and always warned and
skipped; the Ollama size check, model verification and health check
compared printed output to "0" and could never pass — the installer
could never have completed successfully on any machine.

**Fix:** all 10 ExecToStack sites in `AuroraOperator.nsi` now pop `$0`
(exit code) first and the output second, and compare the exit code
against 0; the wrong comment is corrected and the fix is documented
in the script header. `ExecToLog` (model pull) was already correct —
it pushes only the return code — and is unchanged. **InnoSetup.iss
audited: no equivalent bug.** Inno's `Exec()` returns a Boolean and
writes the exit code to its `Code` var parameter; every use checks
`Code = 0` / `Code <> 0` or the contents of a redirected output file
(`OllamaOnPath`, `DotNetDesktopRuntimeInstalled`,
`OllamaListShowsModel`, `OllamaServerResponds`, size check, pull,
smoke test). No Inno change was needed.

**Regression test:** new `tests/AuroraOperator.Tests/NsisInstallerTests.cs`
(5 cases): the pop-order contract comment is present and the old wrong
comment is gone; every ExecToStack site pops `$0` first; no output
variable is ever compared to "0" as an exit code; the poll loop breaks
on exit code 0 (both poll sites); ExecToLog still pops only the return
code. If the pops go backwards again, this test fails.

**Rebuild / retest (2026-11-22):** clean build 0 errors, 40 warnings
(same composition as §3); TestRunner **268 passed, 0 failed** (263 +
5 new NSIS cases); `dotnet test` still 0 run (sandbox proxy). The
publish output is unchanged (no Core/App code changed) — self-contained
270 MB / 815 files, verified present before compiling.

**Recompiled installer:** `makensis AuroraOperator.nsi` (3.08-2),
exit 0. Output `AuroraOperator-Setup-0.2.0-x64.exe`, **110,168,298
bytes**, PE32 GUI, SHA-256
`4b9b361f8ddac147385dc593eb42c5ad39f9240f6400d02cf66b7176e6ffe50a`,
CRC 0xB913284A, install data unchanged (110,037,720 / 280,912,871
bytes). Recompiled once more after a comment-only header edit;
identical hash and CRC, confirming comments do not change the binary.
The old binary (110,168,296 bytes, SHA-256 `a185abb1…`) is SUPERSEDED.

**Drive re-upload:** the fixed installer REPLACED the existing Google
Drive file (same file ID `1bT77LhB9fFTfy6zQfbTwj1U3rOVXatJH`, same
link `https://drive.google.com/file/d/1bT77LhB9fFTfy6zQfbTwj1U3rOVXatJH/view?usp=drivesdk`).
Verified after the update: Drive reports size **110168298**, matching
the new local file byte-for-byte. No second Drive file was created.
`SHA256SUMS.txt`, `RELEASE_NOTES.md` and `WINDOWS_TEST_RESULTS.md`
updated with the new size/SHA-256. The portable ZIP is unchanged.

**Status:** the fixed installer is still **UNEXECUTED on Windows**
(compile PASS only). The user's re-run from the same Drive link is
its first execution test. Verdict unchanged: **CONDITIONAL — USER
TESTING REQUIRED.**

Files changed in this fix: `src/AuroraOperator.Installer/AuroraOperator.nsi`
(all 10 ExecToStack sites + header), new
`tests/AuroraOperator.Tests/NsisInstallerTests.cs`, `release/` (installer
×2 names, `SHA256SUMS.txt`, `RELEASE_NOTES.md`,
`WINDOWS_TEST_RESULTS.md`, this report), `build.log` appended.
