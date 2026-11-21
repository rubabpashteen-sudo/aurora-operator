# DEPENDENCIES.md

## AuroraOperator.App — no third-party packages

The WPF app references **no NuGet packages at all**. It uses the .NET 8
WPF framework and `AuroraOperator.Core` only. The UI is plain XAML —
deliberately **no WebView2**: no embedded browser runtime to download,
update, or patch, and no web content running inside a desktop agent
that holds file-system and automation permissions.

| Component | Version | Licence |
| --- | --- | --- |
| .NET 8 + WPF (`net8.0-windows`, `UseWPF`) | 8.0.x | MIT |

## AuroraOperator.Core (per CONTRACTS.md)

| Package | Version | Licence | Used for |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite | 8.0.8 | Apache-2.0 | Local memory database |
| ClosedXML | 0.105.0 | MIT | CSV → Excel |
| CsvHelper | 33.0.1 | MS-PL / Apache-2.0 (dual) | CSV parsing |
| UglyToad.PdfPig | 0.1.9-alpha001-patch1 (pinned; stable 0.1.9 exists on real nuget.org but this sandbox's feed only offers the prerelease and 1.7.0-custom-5 — see BUILD_AUDIT.md §7.1) | Apache-2.0 | PDF text extraction |
| DocumentFormat.OpenXml | 3.1.1 | MIT | Word / PowerPoint creation |
| Microsoft.Playwright | 1.50.0 | MIT | Headless Chromium research |
| Microsoft.Extensions.Logging.Abstractions | 8.0.2 | MIT | Logging abstractions |

## Tests

| Package | Version | Licence |
| --- | --- | --- |
| xunit | 2.5.3 | Apache-2.0 |
| xunit.runner.visualstudio | 2.5.3 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.8.0 | MIT |
| coverlet.collector | 6.0.0 | MIT |

## Runtime / installer dependencies (not NuGet)

| Component | Version | Licence | Notes |
| --- | --- | --- | --- |
| Aurora Operator app | 0.2.0 | — | win-x64 publish, measured 2026-11-21: framework-dependent 224 MB, self-contained 384 MB (see `publish/`); self-contained is what the installer ships |
| Ollama | current from ollama.com | MIT | Local inference server; installer downloads `OllamaSetup.exe` |
| qwen3:4b (primary) / qwen3:1.7b (fallback) | Qwen3 release | Apache-2.0 | Downloaded by the installer / Model Manager from Ollama. Tags verified 2026-11-21 against ollama.com/library: qwen3:4b 2.5 GB, qwen3:1.7b 1.4 GB, qwen3:0.6b 523 MB, qwen3:8b 5.2 GB, qwen3.5:2b 2.7–3.1 GB (multimodal/vision), qwen2.5-coder:3b 1.9 GB — all exist; see BUILD_AUDIT.md §7.3. No model was downloaded on this VM (no Ollama server). |
| Playwright Chromium | as bundled with Playwright 1.50.0 | BSD-3-Clause (Chromium) | Installed once via `playwright install chromium`; only needed for web research |
| NSIS | 3.x | zlib/libpng licence | Builds `AuroraOperator.nsi` |
| Inno Setup | 6.x | Inno Setup Licence (BSD-style) | Builds `InnoSetup.iss` |
| Pillow (dev only) | 10.x | HPND (MIT-style) | Generates `aurora.ico` / `aurora.png` via `build-icon.py`; optional — a stdlib fallback is included |

Licence names are as published by each project's own repository.
No package above requires a commercial licence for this use.
