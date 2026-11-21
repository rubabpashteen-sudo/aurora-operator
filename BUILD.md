# BUILD.md — Building Aurora Operator

Last verified: **2026-11-21** (Phases 3–5). Full-solution build on
Linux: **0 errors, 32 warnings** (all NU1603 — see below). The
TestRunner then passes **102/102**. See [TESTS.md](TESTS.md) for the
test status and [LIMITATIONS.md](LIMITATIONS.md) for what a Linux
build does and does not prove.

## Prerequisites (Windows)

- **Windows 10/11 64-bit.**
- **.NET 8 SDK** (8.0.x) — <https://dotnet.microsoft.com/download/dotnet/8.0>.
  Verify with `dotnet --version`. (This VM: SDK 8.0.428.)
- **Visual Studio 2022** with the **.NET desktop development** workload
  (for the WPF designer and the Windows SDK), **or** the .NET SDK alone
  for command-line builds. The workload is required for a comfortable
  XAML editing experience; `dotnet build` works without it.
- **NSIS 3.x** (`makensis`) — only for the NSIS installer.
  **Inno Setup 6.x** (`iscc`) — only for the Inno Setup installer.
- **Ollama** for running the app (the installer installs it; for
  development, install it from <https://ollama.com> and run
  `ollama pull qwen3:4b`).
- **Playwright browsers** for web research (see below).

## Build

From the repository root:

```powershell
dotnet build AuroraOperator.sln -c Release
```

On Windows this builds Core, App (WPF), and Tests.

**On Linux the full solution also builds** — the earlier claim that
the App project cannot build on Linux was wrong. Use Windows
targeting:

```bash
~/.dotnet/dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true
```

Verified 2026-11-21: **0 errors, 32 warnings**. All 32 warnings are
NU1603: `UglyToad.PdfPig 0.1.9-alpha001-patch1` depends on
`PdfPig.Core` / `.Fonts` / `.Tokenization` / `.Tokens` at
`>= 0.1.9-alpha001-patch1`, which were not found, so approximate
`1.7.0-custom-5` matches were resolved. The build succeeds with
these substitutions; PDF text extraction is covered by the test
suite. Warning counts vary slightly between invocations (32–40
observed) depending on which projects recompile: the stable set is
the NU1603s above, plus a few pre-existing test-code warnings that
appear when the test projects recompile (CS0067: an unused event in
the `TestApprovalService` test double; xUnit2013: an
`Assert.Equal` collection-size check in `Phase2Tests`). None of
them is an error, and none was introduced in Phases 3–5.

**What a Linux build does NOT prove:** compiling
`net8.0-windows` with Windows targeting is a compile check only.
It does not prove the WPF app runs, renders, or behaves correctly
on Windows. Do not claim Windows compatibility from a Linux build.
See [LIMITATIONS.md](LIMITATIONS.md).

**Do not run builds concurrently.** This VM has 7.7 GB RAM and
builds are heavy; run one build at a time and let it finish.
Append build/test output to `build.log` with a timestamped header
(`=== PHASE … — <UTC timestamp> ===`); never overwrite `build.log`
history — append only.

## Tests

`dotnet test` **does not run on this Linux VM**: vstest's testhost
channel is transparently redirected to the sandbox egress proxy
(proved with `--Diag` logging — see [TESTS.md](TESTS.md) for the
per-attempt record). Run the in-process TestRunner instead:

```bash
~/.dotnet/dotnet tests/AuroraOperator.TestRunner/bin/Release/net8.0/AuroraOperator.TestRunner.dll
```

Verified 2026-11-21 (Phase 8): **248 passed, 0 failed, 248 total**. On a
normal Windows machine use `dotnet test
tests/AuroraOperator.Tests -c Release` instead and expect the same
248 tests.

## Publish (self-contained, what the installer ships)

```powershell
dotnet publish src\AuroraOperator.App -c Release -r win-x64 --self-contained true -o src\AuroraOperator.App\bin\Release\net8.0-windows\win-x64\publish
```

Self-contained means the .NET 8 runtime ships inside the app folder, so
users do not need a runtime installed. The installer copies this
`publish` folder verbatim.

Measured 2026-11-21 (Phase 7, re-published trimmed in Phase 8, Linux
cross-publish to `win-x64`):

- Framework-dependent: `publish/win-x64-framework-dependent/` — **109 MB**
  (needs the .NET 8 Desktop Runtime on the target machine; recommended
  only if the installer also installs the runtime).
- Self-contained: `publish/win-x64-self-contained/` — **270 MB** (what the
  installer ships); the same output is also produced at the default
  location `src/AuroraOperator.App/bin/Release/net8.0-windows/win-x64/publish/`,
  which is the path the installer scripts copy from. Phase 8 removed
  the Linux Playwright Node runtime (~115 MB) at publish time; the
  trimmed build has never launched on Windows.
- Both contain a PE32+ `AuroraOperator.exe`, `AuroraOperator.dll`,
  `.deps.json` and `.runtimeconfig.json` (verified with `file`).
  Native DLLs are flattened to the publish root (`e_sqlite3.dll` at
  root, Playwright driver under `.playwright/node/win32_x64`) — native
  loading must still be confirmed by launching on Windows.
  Note: ~205 MB of the framework-dependent output is `.playwright`
  shipping both linux-x64 and win32_x64 Node — dead weight on Windows.

**Windows runtime verification: NOT VERIFIED.** These outputs were
produced on Linux and have never been launched; a PE header is not a
Windows test. See [LIMITATIONS.md](LIMITATIONS.md) and the Windows
testing package in `tests/windows/`.

## Installer

After publishing, build either installer from
`src\AuroraOperator.Installer\`:

```powershell
cd src\AuroraOperator.Installer
makensis AuroraOperator.nsi        # produces AuroraOperator-Setup-0.2.0-x64.exe
iscc InnoSetup.iss                 # produces AuroraOperator-Setup-Inno-0.2.0.exe
```

Both installers detect RAM, install Ollama if missing, pull
`qwen3:4b` (16 GB+ RAM), `qwen3:1.7b` (8 GB) or `qwen3:0.6b` (below
8 GB), install the app, and create desktop + Start Menu shortcuts. See the script headers for the exact
flow and rollback behaviour. **Neither installer has been built on
this VM** — `makensis` and `iscc` are not installed here. See
[LIMITATIONS.md](LIMITATIONS.md).

## Playwright browsers (web research)

The Core `BrowserTools` uses Playwright with headless Chromium. After
restoring Core, install the browser once on the dev/test machine:

```powershell
dotnet build src\AuroraOperator.Core
# Playwright's CLI is delivered with the Microsoft.Playwright package:
pwsh src\AuroraOperator.Core\bin\Release\net8.0\playwright.ps1 install chromium
```

On Windows PowerShell the script is `playwright.ps1`; if it is not
present after build, run the equivalent from the NuGet package cache
(`.playwright` tooling) or install Chromium via
`npx playwright install chromium` if Node.js is available. Without a
Chromium install, web research returns an honest failure instead of
fake results. **No Chromium is installed on this VM**
(`~/.cache/ms-playwright` does not exist) and no test covers
`BrowserTools.ResearchAsync`, so web research is entirely unverified
here — see [LIMITATIONS.md](LIMITATIONS.md).

## Icons

Regenerate the app icon any time from
`src\AuroraOperator.App\Assets\`:

```powershell
python build-icon.py
```

Requires Python 3 with Pillow; without Pillow it falls back to a
pure-stdlib PNG/ICO writer (same mark). Produces `aurora.ico`
(256/64/32/16) and `aurora.png`. The SVG source is `aurora.svg`.

## Project layout

| Path | Contents |
| --- | --- |
| `src/AuroraOperator.Core/` | Cross-platform engine: Ollama, agent, tools, memory, security |
| `src/AuroraOperator.App/` | WPF UI (`net8.0-windows`), ViewModels, approval dialog |
| `src/AuroraOperator.Installer/` | NSIS + Inno Setup scripts |
| `tests/AuroraOperator.Tests/` | xUnit tests (Core, Linux-runnable via TestRunner) |
| `tests/AuroraOperator.TestRunner/` | In-process runner for the xUnit tests (sandbox workaround, not shipped) |
| `CONTRACTS.md` | The shared API contracts — now updated to the shipped Phase 1/2/5 APIs |

## Resolved package versions (actual, 2026-11-21)

`CONTRACTS.md` lists the requested versions; restore resolved these
nearest-stable versions instead (see `build.log` NU1603/NU1103
history and the `.csproj` files):

| Package | Requested | Resolved |
| --- | --- | --- |
| Microsoft.Playwright | 1.49.1 | **1.50.0** |
| DocumentFormat.OpenXml | 3.0.2 | **3.1.1** |
| UglyToad.PdfPig | 0.1.10 | **0.1.9-alpha001-patch1** (sub-dependencies resolved to 1.7.0-custom-5) |
| Microsoft.Data.Sqlite | 8.0.8 | 8.0.8 |
| ClosedXML | 0.105.0 | 0.105.0 |
| CsvHelper | 33.0.1 | 33.0.1 |
| Microsoft.Extensions.Logging.Abstractions | 8.0.2 | 8.0.2 |
