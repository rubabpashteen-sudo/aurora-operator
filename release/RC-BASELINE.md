# Windows baseline and audit

Baseline revision `01f131c0090c167388bbb7f796b2b77b0f0e3a2a` from `main`.

The repository includes a WPF application, a .NET 8 Core library, xUnit tests, an in-process test runner, NSIS and Inno scripts, and Windows test guides. The head had no GitHub Actions runs and no GitHub Releases. The solution compiled on Windows using .NET SDK 8.0.428 after generating the ignored icon resources. There were 24 warnings, mainly the approximate PdfPig dependency resolution.

The official Windows baseline executed 285 tests: 276 passed and 9 failed. Failures covered space-containing Windows paths, a test hard-coded to Unix `/tmp`, Windows SQLite file sharing, a missing Python executable and browser test assumptions about the user profile and temp directory.

Source inspection found keyword-driven planning and scope inference, re-generation of the plan at execution after review, placeholder project and screen-context UI, history stored at individual task granularity, external fragments passed through legacy tool routes, and legacy installers capable of deleting model trees. The RC keeps existing Core callers and old installer source available, introduces a constrained desktop planner, and packages from the new NSIS entry point.

The Windows acceptance results for the shipped candidate are recorded separately from earlier phase reports.
