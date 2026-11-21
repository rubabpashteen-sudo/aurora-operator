# Aurora Operator 0.2.0 — Release Notes (Release Candidate)

**Version:** 0.2.0 · **Built:** 2026-11-21, installer rebuilt 2026-11-22 · **Status: Release Candidate for user testing — NOT production-ready.**
The installer below is a genuine compiled NSIS installer, but it has never been
executed successfully on Windows yet. You are the first Windows test. Please follow
`INSTALLATION_GUIDE.md` and report anything that fails.

## 2026-11-22 installer fix — please re-download

The first installer you downloaded (SHA-256 `a185abb1…`) had a real bug
that made it fail on your PC even though your Ollama was running fine:
every `nsExec::ExecToStack` check in the installer popped the stack in
the wrong order, so the Ollama check compared an empty output to "0"
and could never succeed — that is the "Ollama did not start (its local
API did not answer within ~30 seconds)" error you saw, after the poll
loop ran all 10 times. The same inversion also broke RAM detection
(every machine fell back to the smallest model), the disk-space check
(silently skipped), and model verification / health check (could never
pass). All call sites are fixed, guarded by a new regression test
(`tests/AuroraOperator.Tests/NsisInstallerTests.cs`, 5 cases), and the
installer was recompiled. **Re-download it from the same Google Drive
link** — the file there is replaced with the fixed build below. The
fixed installer itself has still never executed on Windows; your
re-run is its first execution test.

## What is in this release

| File | Size | SHA-256 |
| --- | --- | --- |
| `AuroraOperator-Setup-0.2.0-x64.exe` (NSIS installer — use this) | 110,168,298 bytes (~105 MB) | `4b9b361f8ddac147385dc593eb42c5ad39f9240f6400d02cf66b7176e6ffe50a` |
| `AuroraOperator-Portable-0.2.0-x64.zip` (portable, no installer) | 110,283,558 bytes (~105 MB) | `7b25faddd76f89ca97f6b21b250a8ce2d867a299c67ef18412a891bb9320ca54` |

`AuroraOperator-Setup-NSIS-0.2.0.exe` is the same installer under its
builder name (identical file, identical SHA-256). Verify any download with
`SHA256SUMS.txt` before running it.

The installer contains the self-contained Windows app (270 MB uncompressed:
WPF app + .NET 8 runtime + SQLite + Windows Playwright runtime). It does
**not** contain Ollama or the AI model — those are downloaded during setup
(see below).

## What the installer does

1. Checks 64-bit Windows 10 22H2 (build 19045) or newer / Windows 11, and free disk space (app + model, stated separately).
2. Downloads and installs Ollama from ollama.com if it is missing (HTTPS, official URL; Ollama publishes no checksum, so the download is verified by a size sanity check).
3. Starts Ollama pinned to loopback only (`OLLAMA_HOST=127.0.0.1:11434`) — a pre-existing `OLLAMA_HOST=0.0.0.0` cannot expose the API from the installer-started process.
4. Downloads the AI model for your RAM (see "Model policy" below), up to 3 attempts, then verifies it with `ollama list`. **If the model download or verification fails, the install aborts and rolls back — it never reports success without a model.**
5. Installs the app to `C:\Program Files\Aurora Operator`, creates Start Menu and desktop shortcuts, and runs a health check (app exe present + model listed). A failed health check rolls back the install.
6. Offers to launch Aurora Operator. Chromium for web research is an optional section (`playwright install chromium`); if it fails, research reports "Chromium missing" honestly.
7. Uninstall asks whether to keep or remove the downloaded models. Ollama itself is left installed. Your data in `%LOCALAPPDATA%\AuroraOperator` (tasks, memory, logs) is preserved by uninstall.

The installer requires administrator rights (Program Files, uninstall registry keys, Ollama install). It is **not digitally signed** — Windows SmartScreen will warn you. That warning is expected for an unsigned build; only continue if you verified the SHA-256 above.

## Model policy (final — app, NSIS and Inno all agree)

| Your RAM | Model downloaded automatically | Size |
| --- | --- | --- |
| Below 8 GB | `qwen3:0.6b` | ~523 MB |
| 8 GB | `qwen3:1.7b` | ~1.4 GB |
| 16 GB | `qwen3:4b` (only if ≥ 6 GB free) | ~2.5 GB |
| 32 GB | `qwen3:8b` (only if ≥ 10 GB free) | ~5.2 GB |

**Why a 4 GB machine gets 0.6b, not 1.7b:** an older checklist line said a 4 GB
machine should receive `qwen3:1.7b`. That was wrong and is corrected here. A
1.4 GB model plus Windows itself plus the app leaves a 4 GB machine with
almost no headroom and risks making Windows unstable; 0.6b (523 MB) is the
safe automatic choice. You can still pick a larger model manually in Model
Manager afterwards. The app, both installer scripts and every document now
use the table above. The installer measures total RAM only; the app also
checks free RAM at runtime and falls back if free RAM is low.

## What Aurora Operator does

Type a task in plain English, Chinese or Urdu and the local AI plans it,
shows the plan, asks before anything sensitive, does the work on your
computer, verifies the result and reports back. All AI runs locally through
Ollama — no cloud account, no API key, no documents uploaded.

Modes: **Ask** (answers only), **Plan** (shows the plan, waits for approval),
**Autonomous** (runs normal approved actions). Stop / Pause / Resume are
always available.

## Security status (honest)

Security review: **10 PASS, 2 PARTIAL, 8 FIXED, 0 FAIL** — these are
code-review findings, not Windows acceptance tests.

- **F5 app launch — FIXED (code):** friendly apps (Notepad, Calculator, Paint,
  WordPad) work. URLs, shell protocols, scripts, installers and shortcuts are
  refused outright, even if approved. Unknown executables need per-request
  approval showing the exact target. The raw launch primitive now refuses
  dangerous targets too. Still needs a live Windows bypass-attempt test.
- **F20 close app — FIXED (code):** close is by **PID only**. Several
  same-named processes close nothing — you must pick one PID. Graceful close
  first; force termination only when the approved action says `force=true`,
  and it kills only that one process, never a whole tree. Protected Windows
  processes are never closed. The approval dialog now shows the exact
  parameters (PID, force flag). Still needs a live Windows test with real
  Notepad instances.
- **F13 log privacy — PARTIAL:** secrets with a key marker (password, token,
  API key, PEM blocks, Basic auth) are redacted from logs, and full user
  requests are no longer logged. A secret written as ordinary free text with
  no marker cannot be caught by any redaction. Do not paste passwords or
  private keys into the app.
- **F14 database — PARTIAL:** preferences that look like credentials are
  protected with Windows DPAPI (CurrentUser); the SQLite file's ACL is
  restricted to the current user. **Conversations and tasks are stored in
  plaintext by design** — they are not encrypted. The DPAPI path and the ACL
  have never run on Windows yet.

Ollama is loopback-only (`127.0.0.1:11434`) in the app, the client (remote
URLs are rejected) and both installers. Network binding still needs a real
Windows `netstat` check.

## Known limits (read before testing)

- **Never run on Windows yet.** App launch, WPF rendering, the Notepad
  workflow, Playwright/Chromium, Ollama download and inference, the
  installer itself, DPAPI and the database ACL are all unverified on Windows.
  That is exactly what this release candidate is for.
- Local models are small: good at planning, files and research summaries;
  weaker than frontier cloud models at long reasoning.
- Web research cannot bypass CAPTCHAs or logins; blocked pages are reported
  as blocked.
- The agent never deletes files without your explicit approval, and blanket
  deletions ("delete everything") are refused.
- Build: 0 errors, 40 warnings on a clean rebuild (32× NU1603 PdfPig
  prerelease pin + 4× CS8600 + 2× CS0067 + 2× xUnit2013). The PdfPig pin is
  unchanged: stable 0.1.9 exists on nuget.org but this build sandbox's feed
  only offers the prerelease; a Windows build should re-check it.
- Tests: in-process TestRunner **268 passed / 0 failed** (248 Phase 8 + 15
  final-release cases + 5 new NSIS pop-order regression cases, 2026-11-22).
  Official `dotnet test` executed **0 tests** on
  the Linux build machine (sandbox proxy corrupts the testhost localhost
  channel) — it must be run on Windows.
- The Inno Setup installer (`AuroraOperator-Setup-Inno-0.2.0.exe`) was **not
  compiled** — its compiler runs on Windows only. Its script is included in
  the source and is consistent with the NSIS flow, including the health-check
  fix (a failed health check cancels setup instead of reporting success),
  but that behaviour has never executed anywhere.

## Files

- `release/` — installer, portable ZIP, checksums, these notes, the
  installation guide and the Windows test results.
- `publish/win-x64-self-contained/` — the 270 MB app the installer ships.
- `FINAL_RELEASE_REPORT.md` — the full engineering record for this release.
