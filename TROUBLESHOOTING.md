# TROUBLESHOOTING.md

## The app says "Ollama offline"

- Start Ollama: open a terminal and run `ollama serve`, or launch
  Ollama from the Start Menu (it sits in the system tray).
- Check it answers: `curl http://localhost:11434/api/tags` should
  return JSON. If it does not, Ollama is not listening.
- The app only talks to `http://localhost:11434`. If you run Ollama on
  another port or host, this build cannot reach it — that is deliberate
  (localhost-only by design).
- Look at `%LOCALAPPDATA%\AuroraOperator\Logs\aurora-operator.log`
  for the exact error.

## The model download fails or is very slow

- `qwen3:4b` is ~2.6 GB; on a slow connection it takes a while. The
  installer shows the pull output; let it finish.
- Retry from the app: **Model Manager → select the model → Download**.
- Or from a terminal: `ollama pull qwen3:4b` (or `qwen3:1.7b` on
  machines with under 8 GB RAM).
- If the disk is full, free space first: check the drive Ollama uses
  (`%USERPROFILE%\.ollama\models`). Each model needs its full size free.

## The app will not start / crashes immediately

- If you built from source without `--self-contained`, install the
  **.NET 8 Desktop Runtime (x64)** from Microsoft. The installer ships
  a self-contained publish, so this should not happen with the release
  installer.
- Check `%LOCALAPPDATA%\AuroraOperator\Logs\aurora-operator.log` — the
  global exception handler writes there before showing a message.
- A second copy shows "Aurora Operator is already running." — that is
  the single-instance guard. Close the first copy (check the tray/task
  manager) and try again.

## The UI is blank / the window is the wrong theme

- Use the **Theme** button (top right) or **Settings → Toggle light /
  dark theme**. The default is dark.
- If XAML fails to load after a bad build, the log will contain the
  XAML parse error with the line number.

## A task never finishes / the progress bar spins forever

- Press **Stop**. The engine caps steps (max 50) and each step has a
  5-minute timeout, so a genuinely stuck run ends on its own, but a
  model that is still generating can look stuck — watch the status
  line in the right panel.
- If Ollama stopped responding, restart it (`ollama serve`) and
  re-send the task.

## "Approval required" but nothing happens after I approve

- Approvals for planned steps are collected in **Plan** mode and the
  plan re-runs once all are granted. If a step still cannot run, the
  engine records the denial in the audit log
  (`%LOCALAPPDATA%\AuroraOperator\Logs\audit.log`).
- A blanket request ("delete all files") is refused before planning —
  that is the safety guard, not a bug. Narrow the request to specific
  files or folders.

## Web research returns "blocked" or fails

- Some sites show CAPTCHAs or require login. The agent records those
  pages as blocked and continues — it never tries to bypass them.
- If every research task fails, Chromium is probably not installed:
  see "Playwright browsers" in [BUILD.md](BUILD.md).

## High memory usage

- The top bar shows the app's own RAM. The model itself lives in
  Ollama's process, not the app's. `qwen3:4b` needs ~4 GB RAM for the
  model alone; on a 4 GB machine use `qwen3:1.7b` (Model Manager).
- Close other heavy apps before a big research or document task.

## Installer problems

- **"Ollama download failed"**: no internet, or ollama.com unreachable.
  Install Ollama manually from <https://ollama.com> and re-run the
  installer — it will detect Ollama and skip the download.
- **"App files were not copied"** (NSIS): run `dotnet publish` first —
  see [BUILD.md](BUILD.md). The installer ships the publish folder.
- **Uninstall asks about models**: answering **No** deletes
  `%USERPROFILE%\.ollama\models`. Answer **Yes** to keep them for
  another install. `%LOCALAPPDATA%\AuroraOperator` (your memory
  database and logs) is always left in place on uninstall.
