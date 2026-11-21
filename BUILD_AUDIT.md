# BUILD_AUDIT.md — Phase 7 (2026-11-21)

Build audit, win-x64 publish, and Ollama model-tag verification for Aurora
Operator 0.2.0. Linux VM, dotnet SDK 8.0.428 (`~/.dotnet/dotnet`).
No ZIP was packaged. Nothing here claims Windows runtime or Ollama
inference as verified — both remain **NOT VERIFIED** (see §7.4).

> **RECONCILIATION — Phase 7 preflight (2026-11-21).** This file was
> written by Worker A before the Phase 7 integration pass, so several
> passages below describe the state at that moment, not the final
> shipped state. The final, reconciled values are:
> **build 0 errors / 40 warnings (full clean rebuild)**; **TestRunner
> 184 passed / 0 failed** (the 174-total runs in §7.4 were intermediate
> parallel runs — see §7.4 note and `build.log:1480,1518`); **both
> installer scripts at 0.2.0** (NSIS:74, Inno:74); **qwen3.5:2b catalog
> corrected to 2.9 GB / MinRamGb 8**; **SelectBestModel aligned —
> qwen3:4b only at 16 GB total + ≥ 6 GB free, 8 GB → qwen3:1.7b**; and
> **publish recommendation: self-contained 384 MB first** (the
> framework-dependent-first line in §7.2 is superseded). Individual
> superseded passages are tagged **[SUPERSEDED — preflight]** in place
> below; the history is preserved, not deleted. Full reconciliation:
> `PHASE7_REPORT.md` §12 and `RELEASE_PREFLIGHT.md`.

## 7.1 Build audit

### Warning inventory in the pre-existing `build.log` (1,040 lines, appended across Phases 1–6)

| Code | Lines in `build.log` | What it is |
| --- | --- | --- |
| NU1603 | 190 | `UglyToad.PdfPig 0.1.9-alpha001-patch1` depends on `PdfPig.Core` / `.Fonts` / `.Tokenization` / `.Tokens` at `>= 0.1.9-alpha001-patch1`, not found; approximate best match `1.7.0-custom-5` resolved. The 190 lines are the same 4 warnings re-emitted per project (Core, App, Tests, TestRunner) across several appended builds — not 190 distinct warnings. |
| CS0414 | 2 | `LocalLogger._initialised` assigned but never used — from an older build in the log. The field no longer exists in `src/AuroraOperator.Core/LocalLogger.cs` (fixed in Phase 5); it does not occur in the Phase 7 build. |
| CA1416 | 6 | `Registry.LocalMachine` / `RegistryKey` calls in `HardwareInfo.cs` — from an older build. `ReadWindowsGpuName()` is now wrapped in `#pragma warning disable CA1416` (`HardwareInfo.cs:106-115`); it does not occur in the Phase 7 build. |
| NU1103 / NU1605 | 0 | The `grep` hits in the earlier count were the strings `NU1103`/`NU1605` appearing inside NU1603-adjacent text, not warnings. No NU1103/NU1605 warnings exist in `build.log`. |

### Fresh Phase 7 build

```
dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true
→ Build succeeded. 0 errors, 38 warnings (MSBuild summary).
```

Composition of the 38: the NU1603 PdfPig set is the bulk (Phase 6 counted
32), plus 2 × CS0067 and 2 × xUnit2013. The raw log contains 72
`warning NU1603` lines because restore warnings are re-emitted per project
and per pass (including 8 lines from temporary WPF project files), so raw
line counts exceed MSBuild's summary count; the summary (38) is the
authoritative number, and its exact per-code split cannot be recovered from
the log alone.
never used, `tests/AuroraOperator.Tests/ApprovalTests.cs:25`) and xUnit2013
(`Assert.Equal` collection-size check, `Phase2Tests.cs:260`) are pre-existing
test-code warnings already documented in `BUILD.md` ("Warning counts vary
slightly between invocations (32–40 observed)"). They were **left alone**:
they live in other workers' test files, are not errors, and fixing them is
not a breaking-change-free win. No warning was suppressed to get a clean build.

`dotnet list package` (Phase 7) confirms the resolved versions:
`UglyToad.PdfPig 0.1.9-alpha001-patch1` with transitive
`UglyToad.PdfPig.Core/.Fonts/.Tokenization/.Tokens 1.7.0-custom-5`;
`Microsoft.Playwright 1.50.0`.

### The PdfPig NU1603 investigation — pin NOT changed

* Real nuget.org **does** have stable `UglyToad.PdfPig 0.1.9` (and 0.1.10–
  0.1.13; latest seen 0.1.13). Verified by web search, 2026-11-21.
* This sandbox's NuGet feed does **not**. In a scratch copy of the tree
  (source only, `~/workspace/scratch-pdfpig`, since removed), changing the
  Core pin to `0.1.9` and restoring gave:

  ```
  error NU1103: Unable to find a stable package UglyToad.PdfPig with version (>= 0.1.9)
  error NU1103:   - Found 2 version(s) in nuget.org [ Nearest version: 1.7.0-custom-5 ]
  ```

  The same NU1103 was reproduced for `0.1.10`. The feed offers exactly two
  PdfPig versions: `0.1.9-alpha001-patch1` and `1.7.0-custom-5`.
* **Decision: the pin stays at `0.1.9-alpha001-patch1`.** Changing it would
  break every restore/build in this environment. On a machine with real
  nuget.org access the correct fix is to pin stable `0.1.9` (or newer,
  after checking the PdfPig API surface — versions below 1.0 do not follow
  SemVer), which should clear all 32 NU1603 warnings. That step is for the
  Windows integrator, not this VM.
* `DEPENDENCIES.md` was wrong about the pin (it said PdfPig `0.1.10`,
  OpenXml `3.0.2`, Playwright `1.49.1`); corrected to the versions actually
  pinned/resolved (§7.5).

## 7.2 win-x64 publish (version 0.2.0)

### Version

* `src/AuroraOperator.App/AuroraOperator.App.csproj`: `<Version>0.2.0</Version>`.
* New `Directory.Build.props` at the repo root sets `<Version>0.2.0</Version>`
  as the single source of truth; the App csproj repeats the same value.
  Verified stamped in the output: `AuroraOperator.dll` contains `0.2.0.0`.
* **[SUPERSEDED — preflight]** Installer scripts were NOT edited
  (another worker owns them).
  `src/AuroraOperator.Installer/AuroraOperator.nsi:26` still says
  `APP_VERSION "0.1.0"` and `InnoSetup.iss:27` still says
  `AppVersion "0.1.0"`. The installer integrator must bump both to `0.2.0`
  before packaging, or the installed product will report 0.1.0.
  — This was true when Worker A wrote it. Worker B subsequently bumped
  both scripts to 0.2.0 (verified in the preflight: NSIS:74,
  InnoSetup.iss:74). The 0.1.0 statement no longer applies.

### Settings checked

* Icon: `src/AuroraOperator.App/Assets/aurora.ico` exists (10,748 bytes) and
  `file` reports a valid `MS Windows icon resource - 4 icons, 16x16 …
  32x32`. Referenced by `ApplicationIcon` in the App csproj and included as
  a `Resource`.
* `runtimeconfig.json` (framework-dependent): `tfm net8.0`, frameworks
  `Microsoft.NETCore.App 8.0.0` and `Microsoft.WindowsDesktop.App 8.0.0`.
  **No explicit `rollForward` is set** — the default (Minor) applies, so a
  newer installed .NET 8.0.x Desktop Runtime satisfies it. No
  `RollForward` change was made; making it explicit (`LatestMinor`) is a
  one-line follow-up for the integrator if they want it pinned in the file.
* Self-contained `runtimeconfig.json`: `includedFrameworks`
  `Microsoft.NETCore.App 8.0.34` and `Microsoft.WindowsDesktop.App 8.0.34`.
* WPF: `UseWPF`, `net8.0-windows`, `OutputType WinExe`, `AssemblyName
  AuroraOperator` — unchanged.

### Publish commands (both needed `-p:EnableWindowsTargeting=true` on Linux)

```
dotnet publish src/AuroraOperator.App/AuroraOperator.App.csproj -c Release -r win-x64 --self-contained false -p:EnableWindowsTargeting=true -o publish/win-x64-framework-dependent
dotnet publish src/AuroraOperator.App/AuroraOperator.App.csproj -c Release -r win-x64 --self-contained true  -p:EnableWindowsTargeting=true -o publish/win-x64-self-contained
```

First attempts failed and are recorded honestly: the framework-dependent
attempt without the property failed with `NETSDK1100: To build a project
targeting Windows on this operating system, set the EnableWindowsTargeting
property to true`; the self-contained attempt with a directory path
(`src/AuroraOperator.App`) failed with `MSB1050: Specify which project or
solution file to use` — resolved by passing the explicit `.csproj` path.
Both re-runs exited 0.

### Results

| Output | Size | Files | `AuroraOperator.exe` |
| --- | --- | --- | --- |
| `publish/win-x64-framework-dependent/` | 224 MB | 31 entries | PE32+ executable (GUI) x86-64, for MS Windows |
| `publish/win-x64-self-contained/` | 384 MB | 281 entries | PE32+ executable (GUI) x86-64, for MS Windows |

Both contain `AuroraOperator.exe`, `AuroraOperator.dll`,
`AuroraOperator.deps.json`, `AuroraOperator.runtimeconfig.json`,
`AuroraOperator.Core.dll`, and the NuGet dependency DLLs
(ClosedXML, CsvHelper, DocumentFormat.OpenXml, Microsoft.Data.Sqlite,
Microsoft.Playwright, UglyToad.PdfPig + sub-packages, e_sqlite3.dll).

**Two findings, recorded not hidden:**

1. **No `runtimes/` folder exists in either output.** The publish flattens
   the native assets instead: `e_sqlite3.dll` sits at the output root, and
   Playwright's native driver lives under `.playwright/node/win32_x64`.
   That is the SDK's normal behaviour for these packages here, but it means
   the "runtimes/ present" check does not pass as written — Windows runtime
   verification must confirm the native DLLs actually load from the
   flattened layout.
2. **205 MB of the 224 MB framework-dependent output is `.playwright`**
   (195 MB `node` + 11 MB `package`). The bundled Node runtime ships both
   `linux-x64` and `win32_x64` binaries, so roughly half of that is dead
   weight on Windows. The app itself (Core + App + document/SQLite
   libraries) is ~19 MB. Trimming the unused Playwright Node platform is a
   packaging follow-up, not a code change made here.

`publish/README.md` states the recommendation (framework-dependent first,
self-contained fallback), the .NET 8 Desktop Runtime requirement, and that
Windows runtime verification is NOT VERIFIED on Linux.
**[SUPERSEDED — preflight]** The recommendation in `publish/README.md`
has been corrected to self-contained first (see the preamble above);
the framework-dependent-first line no longer stands.

## 7.3 Ollama model tags — verified 2026-11-21 against ollama.com/library

| Tag | Exists? | Size (library page) | Context | Notes |
| --- | --- | --- | --- | --- |
| `qwen3` (latest) | Yes | 5.2 GB | 40K | Alias of `qwen3:8b` (same digest `500a1f067a9f`). |
| `qwen3:4b` | Yes | 2.5 GB | 256K | Primary. Catalog says 2.6 GB — close enough. |
| `qwen3:1.7b` | Yes | 1.4 GB | 40K | Fallback. Matches catalog. |
| `qwen3:0.6b` | Yes | 523 MB | 40K | Ultra-light. Added to catalog. |
| `qwen3:8b` | Yes | 5.2 GB | 40K | Advanced tier. Added to catalog. |
| `qwen2.5-coder:3b` | Yes | 1.9 GB | 32K | Coding. Matches catalog. |
| `qwen2.5-coder` (latest) | Yes | 4.7 GB | 32K | Alias of `qwen2.5-coder:7b`. |
| `qwen3.5:2b` | **Yes** | **2.7–3.1 GB** | 256K | The suspect tag **exists** — multimodal (Text, Image input). No correction needed for existence. **But** the catalog's size (1.6 GB) is wrong: the real download is 2.7–3.1 GB depending on quantisation, and `MinRamGb 6` is optimistic for a vision model with a 256K context. **[SUPERSEDED — preflight]** Left as-is in code (tag corrections only, per scope); flagged here for a follow-up data fix. — The integration pass did apply the data fix: `ModelCatalog.cs` now has qwen3.5:2b at 2.9 GB / MinRamGb 8. |

**No tag in `ModelCatalog.cs` was proven wrong, so no tag was replaced.**
Changes made to `ModelCatalog.cs` (additions only, existing API untouched):

* Added `qwen3:0.6b` (523 MB, role `ultra-light`) and `qwen3:8b`
  (5.2 GB, role `advanced`) to `All` — both verified above. `Primary`
  (`All[0]`, `qwen3:4b`) and `Fallback` (`All[1]`, `qwen3:1.7b`) are
  unchanged, and `SelectBestModel` is unchanged (existing tests pin
  16 GB → `qwen3:4b`, 4 GB → `qwen3:1.7b`).
  **[SUPERSEDED — preflight]** `SelectBestModel` was changed by the
  integration pass (see `PHASE7_REPORT.md` §12.1 item 3): it now
  returns qwen3:4b only at 16 GB total + ≥ 6 GB free, and qwen3:1.7b
  on 8 GB, matching the profile table.
* Added `HardwareProfile` record + `Profiles` + `SelectProfile(totalRamBytes)`:
  * **Lightweight — 8 GB:** `qwen3:1.7b`, 4K–8K context, CPU-friendly
    (below 8 GB use `qwen3:0.6b`).
  * **Recommended — 16 GB:** `qwen3:4b`, 8K context (the primary model).
  * **Advanced — 32 GB:** `qwen3:8b`, 8K context — the largest model Aurora
    Operator will recommend; nothing bigger is auto-selected.
* **Known tension, deliberately not merged:** the profile table recommends
  `qwen3:1.7b` on 8 GB machines, but shipped `SelectBestModel` returns
  `qwen3:4b` for any machine with ≥ 8 GB total and ≥ 4 GB free
  (`ModelCatalog.cs:70-74`). The profiles are the documented
  recommendation; auto-selection stays conservative about downgrading.
  Changing `SelectBestModel` to match the table is a product decision, not
  a Phase 7 fix.
  **[SUPERSEDED — preflight]** The integration pass made that product
  decision and merged them: `SelectBestModel` now agrees with the
  profiles (4b only at 16 GB total + ≥ 6 GB free), pinned by
  `Phase7IntegrationTests`. The tension no longer exists in the app —
  but it **did** still exist in both installer scripts until this
  preflight fixed their thresholds too (see `PHASE7_REPORT.md` §12.1
  item 3).
* **Never auto-load multiple large models:** `ModelManager` keeps exactly
  one `CurrentModel` (`ModelManager.cs:12`); `SwitchModelAsync` verifies a
  single model before using it (`:58-66`); unloading is explicit
  (`UnloadModelAsync`, `:82-95`). Nothing in the codebase loads two models
  at once.

### Ollama connectivity (this VM)

```
$ curl -s -m 3 http://localhost:11434/api/tags
curl exit=7 (connection refused)
```

**No Ollama server runs on this VM.** No model was downloaded (a 4B model
was never fetched), and no inference result is claimed anywhere in this
audit. All tag verification above is against the public Ollama library
pages, not a live server.

### Ollama code-path checklist (all paths present in code unless noted)

| Capability | Present? | Evidence |
| --- | --- | --- |
| Ollama install detection (exe on PATH / known Windows paths) | Yes | `OllamaInstaller.cs:18-47` (`IsOllamaInstalled`, `FindOllamaExecutable`); `EnsureInstalledAsync` `:50-74` reports `NotInstalled` / `ServiceNotRunning` honestly, never claims success without an answering API (`:67`). |
| Detection via `GET /api/tags` | Yes | `OllamaClient.cs:67-78` (`IsAvailableAsync`); `:80-92` (`ListModelsAsync` parses `models[].name`). |
| `POST /api/pull` with progress | Yes | `OllamaClient.cs:163-195` (`PullModelAsync`); NDJSON `total`/`completed` → `IProgress<double>` 0..1 (`:181-190`); `success` status sets the return value (`:178-180`). |
| Load / unload via `keep_alive` | Partial | Unload: yes — `POST /api/generate` with `keep_alive=0` (`OllamaClient.cs:201-210`), delegated by `ModelManager.UnloadModelAsync` (`ModelManager.cs:82-95`). Explicit "load" (a `keep_alive` > 0 warm-up call) does **not** exist; loading happens implicitly on first `/api/chat` or `/api/generate` call. |
| `POST /api/chat` + `POST /api/generate` | Yes | `ChatAsync` `OllamaClient.cs:94-114`; `GenerateAsync` `:117-127`. Non-streaming both send `stream=false`. |
| Streaming NDJSON | Yes | `ChatStreamAsync` `OllamaClient.cs:130-160`: `HttpCompletionOption.ResponseHeadersRead` (`:142`), `ReadLineAsync` loop (`:146`), per-line JSON parse, `message.content` deltas yielded (`:151-158`), malformed lines skipped (`:156`). `PullModelAsync` streams the same way (`:170-175`). |
| `CancellationToken` plumbed through | Yes | Every public async method takes a `ct` and passes it to `GetAsync` / `PostAsJsonAsync` / `SendAsync` / `ReadLineAsync` / `ReadAsStringAsync`. Streaming uses `[EnumeratorCancellation]` (`:130`). New Phase 7 tests prove cancellation aborts both streaming chat and pull (see §7.4). |
| Retry / backoff on HTTP calls | **No** | No retry, backoff, or Polly-style policy exists in `OllamaClient.cs` (grep: no `retry`/`Retry`/`Delay`). A transient failure fails the single attempt (e.g. `ChatAsync` throws `HttpRequestException`, `:106`). Step-level retry exists in `AgentEngine` (`AgentEngine.cs:467`) but that re-runs a whole agent step, not an Ollama HTTP call. Adding a small bounded retry (e.g. 2 retries with backoff on 5xx / connection errors, never on 4xx) is a recommended follow-up. |
| Timeouts | Yes | Default 120 s `HttpClient.Timeout` (`OllamaClient.cs:32,64`); overridable via constructor `timeout`. Note: a 120 s timeout also bounds a multi-GB model pull that reports no progress — a pull-specific longer/no timeout is a follow-up worth considering. |
| Localhost-only | Yes | `DefaultBaseUrl = "http://localhost:11434"` (`:21`); the class doc (`:14-16`) states it never contacts a cloud API. (Loopback enforcement itself is covered by another Phase 7 worker's tests.) |

## 7.4 Tests

New file `tests/AuroraOperator.Tests/OllamaPhase7Tests.cs` (9 tests, mocked
HTTP only — `StubHandler` + a token-honouring `HangingHandler`; no server,
no model). Existing mocked tests already covered `/api/tags`, chat,
generate, streaming deltas and unload-failure, so these fill the gaps:
pull-with-progress (incl. the no-`success` false case), streaming-cancel,
pull-cancel, chat-4xx error, unload-success, catalog-tags-exist, and
hardware-profile selection.

```
TestRunner (dotnet run --project tests/AuroraOperator.TestRunner -c Release):
  Run 1 (before fixing my own test): 169 passed, 5 failed, 174 total
  Run 2 (after the fix):             170 passed, 4 failed, 174 total
  OllamaPhase7Tests: 9 passed, 0 failed.
```

The one Phase 7 failure in run 1 was my own test's fault, not the product:
`PullModelAsync_ReportsProgress_AndReturnsTrueOnSuccess` used
`Progress<double>`, which posts callbacks to the captured
SynchronizationContext — in a context-free test the reports landed after
the assertion. Fixed by recording progress synchronously with an inline
`IProgress<double>` implementation; the product code was not changed.

The 4 remaining failures are all `BrowserPhase7Tests` (another parallel
Phase 7 worker's file — **not touched here**):
`ExecuteAsync_Download_TraversalPath_FailsWithoutBrowser`,
`TryValidateDownloadUrl_BadUrls_Rejected()` (parameter-count mismatch),
`ValidateDownloadPath_DirectoryOnly_Rejected`,
`ValidateDownloadPath_EmptyPath_Rejected()` (parameter-count mismatch).
Baseline before Phase 7 was 102 passed / 0 failed; the total is now 174
because parallel workers added Browser/Security Phase 7 tests.
**[SUPERSEDED — preflight]** 174 was Worker A's intermediate total
(§7.4 runs above). The final settled total after integration is
**184 passed / 0 failed** (`build.log:1480,1518`; re-confirmed in the
preflight at 2026-11-21T16:03:28Z).

`dotnet test` was not re-run (Phase 3–5 finding stands: vstest testhost
traffic is redirected to the sandbox egress proxy; see `TESTS.md`).

## 7.5 Files changed (Worker A only)

| File | Change |
| --- | --- |
| `BUILD_AUDIT.md` | New — this file. |
| `Directory.Build.props` | New — `<Version>0.2.0</Version>` (single source of truth). |
| `src/AuroraOperator.App/AuroraOperator.App.csproj` | `<Version>0.1.0</Version>` → `0.2.0`. |
| `src/AuroraOperator.Core/Ollama/ModelCatalog.cs` | Added verified `qwen3:0.6b` + `qwen3:8b` entries, `HardwareProfile` / `Profiles` / `SelectProfile`. No tag replaced; `SelectBestModel` untouched. **[SUPERSEDED — preflight]** `SelectBestModel` was later aligned by the integration pass (see §7.3 note above). |
| `tests/AuroraOperator.Tests/OllamaPhase7Tests.cs` | New — 9 mocked-HTTP tests (see §7.4). |
| `publish/win-x64-framework-dependent/` | New publish output (224 MB). |
| `publish/win-x64-self-contained/` | New publish output (384 MB). |
| `publish/README.md` | New — publish guidance + NOT VERIFIED notice. |
| `DEPENDENCIES.md` | Corrected only the rows verified in Phase 7: PdfPig `0.1.10` → `0.1.9-alpha001-patch1` (+ resolved `1.7.0-custom-5` sub-packages), DocumentFormat.OpenXml `3.0.2` → `3.1.1`, Playwright `1.49.1` → `1.50.0` (incl. Chromium row), Ollama model rows replaced with the verified tag/size table. |
| `build.log` | Appended Phase 7 build + publish + test output (never overwritten). |

Not touched (other workers' scope): installer scripts (`AuroraOperator.nsi`,
`InnoSetup.iss` — still at version 0.1.0, see §7.2), browser code,
**[SUPERSEDED — preflight]** both installer scripts are at 0.2.0;
`SECURITY_REVIEW.md`, testing docs (`TESTS.md`, `BUILD.md`, `LIMITATIONS.md`,
`README.md`), other workers' test files. Scratch copy
`~/workspace/scratch-pdfpig` and `phase7-*.txt` captures were removed after
their contents were appended to `build.log`.

## 7.6 What remains NOT VERIFIED (Windows)

* WPF runtime / UI rendering, Notepad UI flow, Playwright/Chromium research.
* Ollama model download, `/api/pull` against a real server, real inference
  (no server on this VM; `curl` exit 7).
* Both installers (NSIS/Inno not installed here; scripts still at 0.1.0).
  **[SUPERSEDED — preflight]** Scripts are at 0.2.0 (NSIS:74,
  InnoSetup.iss:74); compilers still not installed here.
* That the flattened native layout (`e_sqlite3.dll` at output root, no
  `runtimes/` folder) loads correctly on Windows.
* Whether pinning stable PdfPig `0.1.9` on real nuget.org actually clears
  the NU1603s (expected, but unproven in this sandbox).
* `qwen3.5:2b` catalog size (1.6 GB vs real 2.7–3.1 GB) and its `MinRamGb 6`.
  **[SUPERSEDED — preflight]** Corrected in code to 2.9 GB / MinRamGb 8.
* Retry/backoff for Ollama HTTP calls (missing — see §7.3).

## Phase 8 addendum (2026-11-21)

- **Tests:** TestRunner 248 passed / 0 failed (was 184); 64 new
  Phase 8 cases. `dotnet test` re-tried 2026-11-21T17:47Z with
  `--diag`: still 0 tests run — testhost diag again shows the
  socket polling the sandbox egress proxy `[fd8b:…]:3128`.
  Test-project configuration is correct (Test.Sdk 17.8.0, xunit
  2.5.3, adapters present); the failure is environmental.
- **Warnings:** clean rebuild still 0 errors, 40 warnings (32×
  NU1603 + 4× CS8600 + 2× CS0067 + 2× xUnit2013). One new CS0168
  from the retry work was fixed by removing the unused variable.
- **PdfPig:** stable 0.1.9 re-probed in a scratch project
  (`/tmp/pdfpig-probe`): NU1103, feed offers only 1.7.0-custom-5.
  Pin unchanged.
- **Model selection:** the preflight's "32 GB → 8b" was a profile
  claim the code did not implement, and below-8 GB the app (1.7b)
  contradicted the installers (0.6b). Both fixed in Phase 8; all
  three mechanisms now agree (see `PHASE8_REPORT.md` §3).
- **Publish:** self-contained 384 → 270 MB, framework-dependent
  224 → 109 MB (Linux Playwright Node excluded at publish time).
- **Installers:** 8b branch added to both; Inno post-install health
  check now cancels setup on failure (was advisory-only). Neither
  installer has been compiled or executed.
