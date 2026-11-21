# Aurora Operator

A local-first Windows desktop AI agent. You type what you want done — in
English, Chinese, or Urdu — and Aurora plans the work, shows you the plan,
asks before anything destructive, does the job with tools on your machine,
and reports back with what it did and what it produced.

Everything runs on your PC through [Ollama](https://ollama.com): no cloud
account, no API key, no data sent anywhere.

> **Verification status (2026-11-21):** the full solution builds on
> Linux with 0 errors, and 102 automated tests pass in the TestRunner.
> The WPF app itself, Notepad typing, web research, the installers,
> and the Ollama model download have **not** been run on Windows yet.
> See [LIMITATIONS.md](LIMITATIONS.md) for the honest per-area status
> and [TESTS.md](TESTS.md) for what is tested and how.

> **Source code on GitHub:** this tree is prepared for GitHub so a
> Codex agent can read it and collaborate on it — see
> [PUSH_INSTRUCTIONS.md](PUSH_INSTRUCTIONS.md). The intended
> repository is `rubabpashteen-sudo/aurora-operator` (public, so any
> Codex can read it); the repository itself has to be created on
> github.com first, because the GitHub connection here cannot create
> repositories. Binaries (installer, publish output, generated icons)
> are not in git — regenerate the icons with
> `python3 src/AuroraOperator.App/Assets/build-icon.py` after cloning.

## Features

- **Three modes** — *Ask* (answer only), *Plan* (show the plan, wait for
  approval), *Autonomous* (execute the plan). You pick per task.
- **Local models** — `qwen3:8b` on 32 GB RAM, `qwen3:4b` on 16 GB+ RAM, `qwen3:1.7b` on 8 GB,
  `qwen3:0.6b` below that. Switch or download models from Model Manager.
- **Real tools** — file search/organise, duplicate finding, PDF/CSV/Excel/
  Word/PowerPoint work, Python project scaffolding and test runs, web
  research (headless Chromium), system diagnostics, and desktop apps:
  Aurora can launch Notepad, type real text into it, verify the text by
  reading it back, and save it (Windows only — see below).
- **Approvals before anything risky** — every Sensitive or higher step
  needs a per-request approval, bound to the exact task, action and
  parameters. The approval dialog shows the exact command or change;
  denying is always safe, approvals expire after 5 minutes, and a
  denied/expired approval means the step does not run. A category
  grant never auto-approves a risky step, and an "approved" flag
  smuggled into plan data is ignored. Destructive requests such as
  "delete everything" are refused outright and the agent asks you to
  narrow the scope. Writing over an *existing* file is a Sensitive
  step and needs approval; creating a new file does not.
- **Pause / Resume / Stop** — pause a running task mid-flight and
  resume it; stopping cancels the task and any approval still waiting
  for it. Retries re-execute a failed step at most twice.
- **Memory** — conversations and tasks are stored in a local SQLite
  database (`%LOCALAPPDATA%\AuroraOperator\aurora.db`). Nothing is
  uploaded. Clear it any time from the Memory panel. A task left
  running when the app was killed loads back as interrupted — it is
  never silently resumed or re-executed.
- **Trilingual** — language is detected automatically (Chinese, Urdu,
  English), and written text keeps its exact characters (Unicode,
  newlines) from your request to the file or to Notepad.
- **Dark glass UI** — native WPF, no embedded browser (no WebView2):
  nothing extra to download or patch, and no web content running inside
  an agent that holds file-system permissions.

## Install

**Requirements:** Windows 10/11 64-bit, 8 GB RAM recommended (4 GB
minimum), ~5 GB free disk (app + Ollama + one model).

1. Download `AuroraOperator-Setup-0.2.0-x64.exe` from `release/` and run it.
   (The NSIS installer; the Inno Setup build is `AuroraOperator-Setup-Inno-0.2.0.exe`,
   built with `iscc InnoSetup.iss` on Windows — see `release/RELEASE_NOTES.md`.)
2. The installer checks your RAM, installs Ollama if it is missing,
   and downloads the right model (`qwen3:8b` on 32 GB, `qwen3:4b` on 16 GB+, `qwen3:1.7b`
   on 8 GB, `qwen3:0.6b` below that). The model download takes a few minutes on first install.
3. Launch **Aurora Operator** from the desktop or Start Menu.

To build from source instead, see [BUILD.md](BUILD.md).

## Usage

1. Type a request in the box at the bottom, e.g.
   - "Organise the files in my Downloads folder by type"
   - "Find duplicate photos in Pictures and list them"
   - "Open Notepad and write Hello World"
   - "Research electric bikes under $1000 and write a summary"
   - "Diagnose why this PC is slow"
2. Pick a mode: **Ask**, **Plan**, or **Autonomous**.
3. Press **Send** (or Enter). **Stop** cancels, **Pause**/**Resume**
   suspend and continue a running task.
4. Watch the plan in the centre panel and the right panel for the
   current step, files used, verification status, and any approval
   requests. Approve or deny each one — the exact command is shown.
5. Attach files with **Attach**; enable **Screen context** to include a
   UI Automation snapshot of the screen with the next request.

The sidebar switches the right panel: **Recent Tasks** (double-click to
reload one), **Projects**, **Files**, **Memory**, **Model Manager**,
**Activity** (diagnostics), **Settings** (theme, Ollama, context length).

## Privacy

- All inference runs on localhost via Ollama. No network calls to any
  AI provider.
- Memory, logs, and audit records live under
  `%LOCALAPPDATA%\AuroraOperator\` and nowhere else.
- Web research fetches only the pages needed for your query, in a
  headless browser; it never bypasses CAPTCHAs or logins — a blocked
  page is reported as blocked.
- The agent never deletes files on its own: deletions need your explicit
  approval, and blanket deletions ("delete all") are refused.

## Docs

- [BUILD.md](BUILD.md) — building and publishing on Windows (and Linux)
- [TESTS.md](TESTS.md) — what is tested, per-item status, and the manual Windows pass
- [LIMITATIONS.md](LIMITATIONS.md) — what is verified, what is not yet verified on Windows, and what is not implemented
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — common problems
- [DEPENDENCIES.md](DEPENDENCIES.md) — packages and licences
- [CONTRACTS.md](CONTRACTS.md) — the shared API contracts (updated to the shipped Phase 1/2/5 APIs)
