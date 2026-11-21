# PHASE 7 REPORT — Aurora Operator 0.2.0: Windows installation readiness

Date: 2026-11-21. No ZIP packaged. Existing repository stabilised in place;
no new project, no unrelated features.

## 0. How Phase 7 was done

Three parallel workers (non-overlapping file scopes) plus a coordinator
integration pass. Worker A: build audit + win-x64 publish + Ollama tag
verification. Worker B: browser readiness + installers. Worker C: Windows
testing package + security review. The coordinator then resolved the
cross-worker open items (below), rebuilt, re-ran the TestRunner, and
wrote this report. All build/test/publish output is appended to
`build.log` (history preserved, never overwritten).

## 1. Build audit (7.1)

Full report: [BUILD_AUDIT.md](BUILD_AUDIT.md).

- Pre-existing `build.log` (1,040 lines, Phases 1–6): 190 NU1603 lines
  (the same 4 PdfPig warnings re-emitted per project/build), 2× CS0414 +
  6× CA1416 from older builds only — both gone in current code.
- Fresh Phase 7 build: **0 errors, 38 warnings** (MSBuild summary).
  Integration build after the fixes below: **0 errors, 40 warnings**
  (the +2 are pre-existing test-code warnings surfaced by the rebuild;
  no warnings were suppressed anywhere).
- Warning breakdown: NU1603 PdfPig (bulk, see below), 2× CS0067 +
  2× xUnit2013 in test code (pre-existing, left alone), CS8600 in
  `BrowserPhase7Tests` (pre-existing, test code only).
- **PdfPig NU1603 — pin NOT changed, deliberately.** Stable 0.1.9
  exists on real nuget.org (latest seen 0.1.13), but this sandbox's
  NuGet feed offers only PdfPig `0.1.9-alpha001-patch1` and
  `1.7.0-custom-5` sub-packages. Scratch-copy restores of stable `0.1.9`
  and `0.1.10` both failed with NU1103. Fix on real nuget.org: pin
  stable 0.1.9 — a Windows-integrator step, recorded in BUILD_AUDIT.md
  §7.1 and DEPENDENCIES.md. No warning was suppressed to fake a clean
  build.
- Resolved versions confirmed via `dotnet list package`: PdfPig
  `0.1.9-alpha001-patch1` + sub-packages `1.7.0-custom-5`, Playwright
  `1.50.0`, ClosedXML 0.105.0, OpenXml 3.1.1, CsvHelper 33.0.1,
  Microsoft.Data.Sqlite 8.0.8. See [DEPENDENCIES.md](DEPENDENCIES.md).

## 2. Windows x64 Release publish (7.2)

Version: **0.2.0** (App csproj + new `Directory.Build.props`, stamped
`0.2.0.0` in `AuroraOperator.dll`). Icon verified: valid ICO, 4 sizes
(16×16/32×32). runtimeconfig: `Microsoft.NETCore.App` +
`Microsoft.WindowsDesktop.App 8.0.0`, no explicit RollForward (default
Minor); self-contained bundles .NET 8.0.34.

| Output | Path | Size | Contents verified |
|---|---|---|---|
| Framework-dependent win-x64 | `publish/win-x64-framework-dependent/` | **224 MB**, 31 entries | `AuroraOperator.exe` is PE32+ GUI x86-64 (`file`), + `.dll`, `.deps.json`, `.runtimeconfig.json` |
| Self-contained win-x64 | `publish/win-x64-self-contained/` | **384 MB**, 281 entries | Same checks; `e_sqlite3.dll` at publish root, Playwright driver under `.playwright/node/win32_x64` |
| Self-contained, default location | `src/AuroraOperator.App/bin/Release/net8.0-windows/win-x64/publish/` | **384 MB** | Same output; this is the path the installer scripts copy from |

**Recommendation: ship the self-contained publish.** It needs no .NET
runtime on the target machine, so the install cannot fail on a missing
runtime. Framework-dependent (224 MB) is the alternative only if the
installer also installs the .NET 8 Desktop Runtime — the detection code
for that exists in both installers but is dormant (`DOTNET_REQUIRED=0` /
`DotNetRequired=False`).

**Two publish findings:**
- `runtimes/` folder is absent in both outputs — native DLLs are
  flattened to the publish root. Native loading must be confirmed by
  launching on Windows.
- **205 MB of the 224 MB framework-dependent output is `.playwright`**,
  shipping both linux-x64 and win32_x64 Node — about half dead weight on
  Windows. Trimming the Linux Playwright runtime from the Windows
  publish is a pre-release optimisation (not done here: it changes the
  publish layout the installer copies).

**Windows runtime verification: NOT VERIFIED.** Both outputs were
produced by Linux cross-compilation and have never been launched. A PE
header is not a Windows test. See `publish/README.md` and §8 below.

## 3. Ollama integration (7.3)

Code paths verified present in code (file:line table in BUILD_AUDIT.md
§7.3): detection (`OllamaInstaller.cs:18-74`), `/api/tags`
(`OllamaClient.cs:67-92`), `/api/pull` with progress (`:163-195`),
unload `keep_alive=0` (`:201-210`), `/api/chat` + `/api/generate`
(`:94-127`), streaming NDJSON parsing (`:130-160`),
`CancellationToken` throughout. Single-model loading confirmed
(`ModelManager.CurrentModel` is a single string).

**Gaps found (not fixed in Phase 7, recorded here):** no retry/backoff
on `OllamaClient` HTTP calls (AgentEngine step-retry is not HTTP retry),
and no explicit model warm-up ("load") call — loading is implicit on
first inference. `curl -m 3 http://localhost:11434/api/tags` → exit 7:
**no Ollama server on this VM**; no model was downloaded, no inference
result is claimed anywhere.

### Model tags verified against ollama.com/library, 2026-11-21

| Tag | Exists | Size | Role |
|---|---|---|---|
| qwen3 / qwen3:8b | Yes | 5.2 GB | advanced (32 GB) |
| qwen3:4b | Yes | 2.5 GB, 256K ctx | primary (16 GB) |
| qwen3:1.7b | Yes | 1.4 GB | fallback (8 GB) |
| qwen3:0.6b | Yes | 523 MB | ultra-light (<8 GB) |
| qwen3.5:2b | **Yes** | **2.7–3.1 GB, 256K, multimodal** | vision |
| qwen2.5-coder:3b | Yes | 1.9 GB | coding |

The suspect tag **qwen3.5:2b exists** — no tag needed replacing. But the
catalog's size for it (1.6 GB) was wrong; the integrator corrected it to
2.9 GB and MinRamGb 6 → 8 in `ModelCatalog.cs`.

### Hardware profiles (in `ModelCatalog.Profiles` / `SelectProfile`)

- 8 GB: Lightweight — qwen3:1.7b, 4K–8K context, CPU-friendly.
- 16 GB: Recommended — qwen3:4b, 8K context.
- 32 GB: Advanced — qwen3:8b, 8K context.
- Below 8 GB: qwen3:0.6b. Never more than one model loaded at a time.

**Integration decision (8 GB tension, resolved):** the new profiles
recommended 1.7b at 8 GB while `SelectBestModel` returned 4b at ≥8 GB
total / ≥4 GB free. The coordinator aligned them explicitly:
`SelectBestModel` now returns qwen3:4b only at **16 GB total and ≥6 GB
free**; 8 GB machines get qwen3:1.7b. A user may still pick 4b manually
on 8 GB from Model Manager. Existing tests (16 GB→4b, 4 GB→1.7b) still
pass; new `Phase7IntegrationTests` pin the 8 GB behaviour.

## 4. Browser automation readiness (7.4)

Playwright **1.50.0 verified as a real stable release** (2025-02-03,
~2.6M downloads, bundles Chromium 133.0.6943.16). Pin unchanged; newer
versions exist (up to 1.64.1) — upgrading is out of scope.

Present and wired in `BrowserTools.cs`: headless Chromium launch (60 s
timeout), Bing navigation (`DOMContentLoaded`, 30 s), text extraction
(title + first `p` paragraphs), per-query error recovery (one failed
query never aborts the report), page `CloseAsync` in `finally`,
`await using (browser)`, `using (playwright)`, 8 CAPTCHA/verification
markers — blocked pages recorded as "Blocked", **no auth/CAPTCHA/anti-bot
bypass anywhere**, honest failure when Chromium is missing.

Added in Phase 7: `IsChromiumInstalled()` / `CheckBrowserReadiness()`
(Playwright `chromium-*` cache detection), `ChromiumInstallCommand` /
`ChromiumInstallHint` (`playwright install chromium`, incl. the Windows
`playwright.ps1` form), `BuildSearchUrl()`, public `IsBlockedPage()`,
`TryValidateDownloadUrl()` (http(s) only; rejects `file:` /
`javascript:` / `data:` / `ftp:` and embedded credentials),
`ValidateDownloadPath()` (absolute, NUL-free, must name a file, must
stay inside its directory), and `action=download` (`DownloadAsync` with
non-empty-file check). Two real bugs fixed: `GetFullPath` threw on
NUL-byte download paths before validation ran.

**Missing / limited:** no automatic Chromium bootstrap (the install
command is reported, not run); no click/form interaction — research is
deliberately read-only; no login handling.

**Chromium runtime: NOT VERIFIED.** No `~/.cache/ms-playwright` on this
VM; `ResearchAsync` and `DownloadAsync` have never executed against a
real browser. Manual Windows pass required (see §6).

## 5. One-click installer preparation (7.5)

Both scripts updated to **0.2.0** and kept in sync:
`src/AuroraOperator.Installer/AuroraOperator.nsi`,
`src/AuroraOperator.Installer/InnoSetup.iss`.

- Windows/arch: NSIS `.onInit` checks `${RunningX64}` + registry build
  ≥ 19045 (Win10 22H2+ / Win11), aborts otherwise; Inno has
  `MinVersion=10.0.19045` + `ArchitecturesAllowed=x64`.
- Disk space, app and model stated separately: model qwen3:4b ~2,600 MB
  / qwen3:1.7b ~1,400 MB (Ollama library, approximate); **app allowance
  512 MB, measured: the self-contained publish is 384 MB**
  (2026-11-21 Linux cross-publish; re-measure on Windows).
- .NET 8 Desktop Runtime: detection/install code present but dormant
  (self-contained publish needs none); flip the flag if publish ever
  goes framework-dependent.
- Ollama: HTTPS-only official URL, download size sanity check (>1 MB).
  No hash pinned — **Ollama publishes no checksum for
  `OllamaSetup.exe`**, so none was invented; re-check before release.
- Ollama service + model: poll `ollama list` until the server answers
  (~30 s cap); pull with **3 attempts** (`ollama pull` resumes partial
  blobs); verify with `ollama list` containing the model name; tiny
  `ollama run <model> "Say OK"` smoke test, 60 s cap, best-effort
  warning only.
- **Behaviour change (explicit):** model pull/verification failure after
  retries now **aborts and rolls back** (NSIS `Rollback`; Inno `Abort()`
  in `ssInstall`, before any app file is copied). Previously it installed
  with a warning. Neither script reports success when the model or a
  required dependency failed — explicit failure flags (`$PULL_OK` /
  `PullOk`) gate this.
- Health check + launch: both re-verify exe-present + model-listed at
  the end; NSIS finish page offers to launch; Inno launch entry kept.
- Chromium (optional): new optional NSIS section / Inno task running
  bundled `playwright.ps1 install chromium`, best-effort with a warning
  on failure; Inno desktop-icon task now checked by default.
- **Security fix (Phase 7 integration, finding F2):** both installers
  now pin `OLLAMA_HOST=127.0.0.1:11434` when starting Ollama (NSIS:
  `System::Call kernel32::SetEnvironmentVariable` before the `Exec`;
  Inno: `cmd /c set OLLAMA_HOST=… && "<ollama>" serve`), so a user-set
  `OLLAMA_HOST=0.0.0.0` cannot expose the unauthenticated API.
- No admin requirement is introduced by the installer beyond what the
  steps need (NSIS `RequestExecutionLevel admin` is pre-existing —
  Program Files install + uninstall keys; documented in the scripts).

**Installer validation: NOT compiled, NOT executed.** `makensis`/`iscc`
are not installed on this Linux VM and `apt-get install nsis` fails
("Unable to locate package nsis"). Validated by careful review only
(incl. NSIS `$$` PowerShell escaping, Inno Pascal quote-doubling,
`Abort()`-in-`ssInstall`). Compile and exercise the failure paths on
Windows before release. **Do not claim the installer works until it has
been executed on Windows.**

## 6. Windows manual testing package (7.6)

Created under `tests/windows/`:

- [WINDOWS_TESTING_GUIDE.md](tests/windows/WINDOWS_TESTING_GUIDE.md) —
  all 15 items with exact commands, expected results, FAIL criteria and
  evidence locations: (1) app launch, (2) Ollama detection, (3) model
  download, (4) real AI inference, (5) Notepad launch, (6) actual text
  entry, (7) text verification, (8) file saving, (9) approval and
  rejection, (10) task cancellation, (11) browser automation,
  (12) memory persistence, (13) offline operation, (14) app restart,
  (15) uninstallation. Also documents the known double-prompting wart
  and the Win11 tabbed-Notepad caveat.
- [WINDOWS_ACCEPTANCE_CHECKLIST.md](tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md) —
  tick-box list: the same 15 items + 8 installer/uninstaller checks +
  Windows build/test rows; every item marked NOT VERIFIED.
- [WINDOWS_DIAGNOSTICS.md](tests/windows/WINDOWS_DIAGNOSTICS.md) — how
  to run the script and interpret each check, incl. the loopback-only
  port-11434 check.
- [windows-diagnostics.ps1](tests/windows/windows-diagnostics.ps1) —
  read-only diagnostics: OS build, RAM, disk, .NET 8 Desktop Runtime,
  Ollama exe/process/API/models, Playwright Chromium, Aurora install +
  shortcuts, port 11434 loopback-only (`Get-NetTCPConnection` with
  netstat fallback), log/data locations, uninstall registry entry;
  PASS/FAIL/WARN/INFO per check, timestamped report written to the
  Desktop, exit 1 on any FAIL. **Not executed anywhere yet** (no
  PowerShell on this Linux VM) — its first run on Windows is itself part
  of the acceptance pass.

## 7. Security review (7.7)

Full review: [SECURITY_REVIEW.md](SECURITY_REVIEW.md) — 20 findings with
file:line evidence. Final verdict: **10 PASS, 4 PARTIAL, 6 FIXED,
0 FAIL remaining.**

| Result | Count | Findings |
|---|---|---|
| PASS | 10 | F1 Ollama localhost-only, F3 filesystem/NUL, F4 process execution, F6 no PowerShell, F7 browser research, F9 approval bypass (none found), F10 LLM output never a permission, F11 cancellation, F12 delete-all/fail-closed, F15 audit integrity |
| PARTIAL | 4 | F5 app-launch allowlist, F13 log privacy (residual free-text secrets), F14 plaintext SQLite, F20 close-app can kill any process |
| FIXED | 6 | F16 `run-tests` was auto-allowed (was FAIL), F17 browser `download` was ReadOnly (was FAIL), F18 OllamaClient accepted remote URLs (was PARTIAL), F19 stale panel click logged as "approve" (was PARTIAL), **F2 installer Ollama bind (fixed in Phase 7 integration)**, **F8 browser download containment (fixed in Phase 7 integration)** |

Fixes made in this review (Phase 7 worker C): `LocalLogger.RedactSecrets()`
scrubs `password=`/`api_key=`/`secret=`/`token=`/`Bearer` from every log
line; `OllamaClient` constructor enforces loopback-only base URLs
(`localhost`/`127.0.0.1`/`::1`, remote URLs throw); `run-tests` is now
Administrator, `download` is now ExternalCommunication.

**Integration fixes (coordinator, 2026-11-21):**
- **F2:** both installers pin `OLLAMA_HOST=127.0.0.1:11434` (see §5).
- **F8:** browser downloads are now contained to a fixed
  `BrowserTools.DefaultDownloadDirectory` (the user's Downloads folder);
  an explicit `downloadDirectory` arg must pass the new
  `BrowserTools.IsInsideUserProfile` check. The old code derived the
  "allowed" directory from the requested path, making containment
  vacuous. Covered by 9 new `Phase7IntegrationTests`.

Remaining PARTIALs are residual risks with concrete remediations in
SECURITY_REVIEW.md, not open bypasses: F5/F20 are product decisions
(app-launch allowlist, `close-app` approval); F13 residual free-text
secrets in logs; F14 plaintext SQLite (consider DPAPI encryption or
value-level secret filtering).

## 8. Tests — exact counts

- **TestRunner (this Linux VM): 184 passed, 0 failed, 184 total**
  (integration re-run, 2026-11-21T15:23Z). Baseline was 102 after
  Phases 1–6. New test classes added in Phase 7: `OllamaPhase7Tests`,
  `BrowserPhase7Tests`, `SecurityPhase7Tests`, `Phase7IntegrationTests`.
  The settled total is 184/0 — intermediate parallel runs showed
  170–175 with transient failures in other workers' in-progress files;
  the final integration run is the count that stands (see `build.log`).
- **`dotnet test` (vstest): 0 tests run** on this VM. The sandbox egress
  proxy redirects the testhost localhost channel to `[fd8b:…]:3128`
  (proved with `--Diag` logging in Phases 3–5; not re-proved in Phase 7
  because nothing about the environment changed). Environment defect,
  not a code failure. Run `dotnet test` on Windows.
- **Build: 0 errors, 40 warnings** (integration build; see §1).

## 9. Files changed in Phase 7

- New: `BUILD_AUDIT.md`, `Directory.Build.props`, `PHASE7_REPORT.md`
  (this file), `SECURITY_REVIEW.md`, `tests/windows/` (4 files),
  `publish/` (both win-x64 outputs + README),
  `tests/AuroraOperator.Tests/OllamaPhase7Tests.cs`,
  `BrowserPhase7Tests.cs`, `SecurityPhase7Tests.cs`,
  `Phase7IntegrationTests.cs`.
- Edited: `src/AuroraOperator.Core/Ollama/ModelCatalog.cs` (tags,
  profiles, 8 GB alignment, qwen3.5:2b size), `Tools/BrowserTools.cs`
  (readiness, validation, download action, F8 containment),
  `src/AuroraOperator.Installer/AuroraOperator.nsi` + `InnoSetup.iss`
  (0.2.0, disk-space accounting, rollback-on-model-failure, OLLAMA_HOST
  pin, Chromium option), `src/AuroraOperator.App/*.csproj` (version
  0.2.0), `README.md`, `BUILD.md`, `TESTS.md`, `LIMITATIONS.md`,
  `DEPENDENCIES.md`, `SECURITY_REVIEW.md` (F2/F8 fixed).
- `build.log` appended only, never overwritten.

## 10. Remaining limitations / NOT VERIFIED on Windows

1. **Windows runtime:** the win-x64 publish has never been launched;
   WPF UI, native DLL loading (flattened layout), and app startup are
   unverified.
2. **Notepad UI flow** (launch → type → read-back → Save dialog),
   including Windows 11 tabbed Notepad.
3. **Playwright/Chromium research and downloads** — no Chromium on this
   VM; no test covers the real browser path at all.
4. **Ollama model download and real inference** — no server or model on
   this VM; the smoke test in the installers is best-effort only.
5. **Both installers** — never compiled or executed; failure/rollback
   paths unexercised; NSIS `System::Call` and Inno `cmd /c set`
   OLLAMA_HOST lines need a Windows compile check.
6. **Model switch for an already-running engine** (pre-existing).
7. **`dotnet test`** — 0 tests run here (environment proxy defect);
   run it on Windows.
8. **PdfPig NU1603 warnings** — pin stable 0.1.9 on real nuget.org
   (Windows-integrator step); unproven whether it clears them.
9. **OllamaClient has no HTTP retry/backoff** and no explicit model
   warm-up call.
10. **`.playwright` dead weight** (~205 MB of linux-x64 Node in the
    framework-dependent publish) — trim before release.
11. **Security PARTIALs** F5, F13, F14, F20 (see §7).
12. **`windows-diagnostics.ps1` has never been executed** — first run on
    Windows is part of the acceptance pass.

## 11. Build / publish / install commands (Windows)

```powershell
# Build + test
dotnet build AuroraOperator.sln -c Release
dotnet test tests/AuroraOperator.Tests -c Release

# Publish (self-contained is what the installer ships)
dotnet publish src\AuroraOperator.App -c Release -r win-x64 --self-contained true
# Framework-dependent alternative:
dotnet publish src\AuroraOperator.App -c Release -r win-x64 --self-contained false

# Installers (from src\AuroraOperator.Installer, after publishing)
makensis AuroraOperator.nsi        # AuroraOperator-Setup-NSIS-0.2.0.exe
iscc InnoSetup.iss                 # AuroraOperator-Setup-Inno-0.2.0.exe

# Windows acceptance pass
powershell -NoProfile -ExecutionPolicy Bypass -File tests\windows\windows-diagnostics.ps1
# then follow tests\windows\WINDOWS_TESTING_GUIDE.md and tick off
# tests\windows\WINDOWS_ACCEPTANCE_CHECKLIST.md
```

## 12. Preflight reconciliation (2026-11-21) — added after the Phase 7 report

A preflight audit re-verified every Phase 7 claim against the actual
code and the full `build.log` (1,538 lines). Most of the report above
was confirmed. The contradictions below were in `BUILD_AUDIT.md`
(worker-level, written before the integration pass) or in stale
publish docs — not in the shipped code. Each is resolved here; the
superseded text is labelled SUPERSEDED in `BUILD_AUDIT.md` and
`publish/README.md` rather than silently deleted.

### 12.1 Contradictions resolved

1. **Test counts — 184 stands; 174 was intermediate (SUPERSEDED).**
   `BUILD_AUDIT.md` §7.4 records Worker A's parallel runs: 170 passed /
   4 failed, 174 total, then 175/0 — before the other workers' test
   files landed and before the 4 `BrowserPhase7Tests` failures were
   fixed. `build.log:1401` shows that 170/4 run; `build.log:1480` and
   `:1518` show the integration/closing runs at **184 passed / 0
   failed**. Preflight re-run (2026-11-21T16:03:28Z, after a full
   `--no-incremental` rebuild): **184 passed / 0 failed, 184 total**.
2. **Installer version — 0.2.0 in both scripts (confirmed).**
   `BUILD_AUDIT.md` §7.2 said both scripts were still 0.1.0 — true
   when Worker A wrote it, superseded when Worker B bumped them.
   Verified now: `AuroraOperator.nsi:74` (`APP_VERSION "0.2.0"`) and
   `InnoSetup.iss:74` (`AppVersion "0.2.0"`), plus
   `Directory.Build.props:11` and App csproj `<Version>0.2.0</Version>`.
3. **8 GB model selection — 1.7b confirmed in the app; installers
   were wrong and have been FIXED in this preflight.** `ModelCatalog.
   SelectBestModel` (`ModelCatalog.cs`): qwen3:4b only at **16 GB total
   AND ≥ 6 GB free**, otherwise qwen3:1.7b — pinned by
   `Phase7IntegrationTests.SelectBestModel_8GbTotal_SelectsFallback_
   NotPrimary` and `SelectProfile_And_SelectBestModel_Agree_On8And16Gb`.
   But both installer scripts still selected 4b at ≥ 8 GB
   (`AuroraOperator.nsi` RAM ≥ "8" → 4b; `InnoSetup.iss`
   `RamGb >= 8` → 4b), contradicting the documented profiles in
   `README.md:62-63`, `BUILD.md:129-130` and `TESTS.md:184-187`.
   Fixed: both scripts now select 4b at ≥ 16 GB, 1.7b at 8–15 GB,
   0.6b below 8 GB (and on RAM-detection failure); new size constants
   `MODEL_SIZE_06B_MB 523` (NSIS) / `ModelSize06bMB = 523` (Inno).
   This is the one behaviour change in the preflight, made because the
   implementation contradicted the intended documented profiles; it
   must be re-verified on Windows (8 GB VM pulls 1.7b, 16 GB VM pulls
   4b — see `RELEASE_PREFLIGHT.md`).
4. **qwen3.5:2b catalog — corrected values confirmed in code.**
   `ModelCatalog.cs`: `qwen3.5:2b`, 2,900,000,000 bytes (~2.9 GB),
   `MinRamGb 8`, role `vision`. `BUILD_AUDIT.md` §7.3's "left as-is
   (1.6 GB / MinRamGb 6)" was superseded by the integration fix.
5. **Publish recommendation — self-contained first (reconciled).**
   `BUILD_AUDIT.md` §7.2 / `publish/README.md` recommended
   framework-dependent first (SUPERSEDED). The reconciled
   recommendation is **self-contained 384 MB** (`publish/win-x64-
   self-contained/`): it needs no .NET runtime on the target, so the
   install cannot fail on a missing runtime; it is also the output
   both installer scripts copy from. Framework-dependent (224 MB)
   remains the alternative, only with the .NET 8 Desktop Runtime
   installed by the installer.
6. **Warnings 38 vs 40 vs 32 — explained, no suppression.** Full
   clean rebuild (`--no-incremental`, 2026-11-21T16:02:09Z): **0
   errors, 40 warnings = 32× NU1603 (PdfPig) + 4× CS8600 + 2× CS0067
   + 2× xUnit2013**. The 38 was Worker A's fresh solution build
   (before the 4 CS8600s in `BrowserPhase7Tests` landed). The 32s are
   incremental rebuilds where up-to-date projects are skipped, so
   restore/compile warnings are not re-emitted. The authoritative
   final count is **40 warnings, 0 errors**; the 72 raw NU1603 lines
   in the log are the same 32 warnings re-emitted per project/pass.
   File counts in §2 (31 / 281) are **top-level entries**; recursive
   file counts are 358 / 816, dominated by the Playwright Node trees.
7. **Security fixes — verified present in current source.**
   Installer loopback binding: `AuroraOperator.nsi:296-299`
   (`OLLAMA_HOST=127.0.0.1:11434` via `SetEnvironmentVariable` before
   serve) and `InnoSetup.iss:362-364` (`set OLLAMA_HOST=127.0.0.1:11434
   && ollama serve`). `OllamaClient` loopback enforcement:
   `OllamaClient.cs:43-60` (`ValidateLoopbackOnly`, rejects
   0.0.0.0 / LAN / remote hosts; default `http://localhost:11434`).
   Browser download containment: `BrowserTools.cs:196-205`
   (`DefaultDownloadDirectory` = user's Downloads folder, never
   derived from the requested path), `:266-330`
   (`ValidateDownloadPath`, no `..` escape, NUL rejected),
   `:348-379` (download action enforces both). All present; no fix
   needed.

### 12.2 New findings from the preflight (not in the Phase 7 report)

- **Installer output-filename collision (FIXED).** Both scripts wrote
  `AuroraOperator-Setup-0.2.0.exe` into the same folder
  (`src/AuroraOperator.Installer/`), so compiling the second silently
  overwrote the first. Fixed: NSIS now produces
  `AuroraOperator-Setup-NSIS-0.2.0.exe`, Inno produces
  `AuroraOperator-Setup-Inno-0.2.0.exe`. `BUILD.md`, `README.md` and
  §11 above updated to match.
- **Inno post-install health check is advisory-only (NOT fixed —
  First-Windows-test requirement).** §5 above says both installers
  "re-verify exe-present + model-listed at the end". True as a check,
  but the consequences differ: NSIS calls `Rollback` on failure
  (`AuroraOperator.nsi:396-397`), while Inno only shows a `MsgBox`
  (`InnoSetup.iss:413-418`) and setup still reports success. The
  model/download gates in `ssInstall` (`FailInstall`/`Abort()` before
  any app file is copied) are consistent and do abort; only the
  post-install check diverges. Do not change the Inno code without
  Windows verification of `Abort()` semantics in `ssPostInstall`.
- **"Rollback" removes app files only (both scripts).** NSIS
  `Rollback` (`AuroraOperator.nsi:183-190`) deletes `$INSTDIR` and
  temp files; it does **not** uninstall Ollama or delete the
  downloaded model. Inno `FailInstall` aborts before files are copied
  but likewise leaves an already-installed Ollama and pulled model in
  place. That is arguably correct (Ollama/model are shared, reusable
  state), but it is not a full rollback — recorded here, not hidden.
- **`.playwright` dead weight is in BOTH outputs, not only
  framework-dependent.** Each publish folder carries 205 MB of
  `.playwright` (`node/linux-x64` ~115 MB + `node/win32_x64` ~80 MB);
  §2/§10's "205 MB of the framework-dependent output" understated
  this. The Linux Node half is dead weight on Windows in both.
- **`publish/README.md` pointed at a file that does not exist**
  (`WINDOWS_VERIFICATION_CHECKLIST.md`) — fixed to
  `tests/windows/WINDOWS_TESTING_GUIDE.md`.
- **Admin is required by both installers** (NSIS
  `RequestExecutionLevel admin`, Inno `PrivilegesRequired=admin`) —
  justified in the scripts by Program Files install + uninstall keys,
  but the user-facing claim "no admin unless genuinely required"
  should be read as: admin IS required for this installer.

### 12.3 Preflight final counts (2026-11-21T16:02–16:03Z, Linux VM)

- Full clean rebuild: `dotnet build AuroraOperator.sln -c Release
  -p:EnableWindowsTargeting=true --no-incremental` → **0 errors,
  40 warnings** (32 NU1603 + 4 CS8600 [2 sites, emitted twice] +
  2 CS0067 [1 site, emitted twice] + 2 xUnit2013 [1 site, emitted
  twice]).
  Incremental rebuild immediately before it: 0 errors, 32 warnings.
- TestRunner: `dotnet run --project tests/AuroraOperator.TestRunner
  -c Release --no-build` → **184 passed, 0 failed, 184 total**.
- `dotnet test`: **0 tests run** (sandbox proxy defect stands).
- `curl -m 3 http://localhost:11434/api/tags` → exit 7 (no Ollama
  server on this VM). `makensis` / `iscc`: not installed (exit 1).
- Publish outputs re-verified unchanged by the preflight (no
  re-publish): framework-dependent 224 MB, self-contained 384 MB,
  both PE32+ GUI x86-64 exes; default-location publish
  (`src/AuroraOperator.App/bin/Release/net8.0-windows/win-x64/publish/`)
  is byte-identical in file listing to `publish/win-x64-
  self-contained/` and is the path both installer scripts copy from.
