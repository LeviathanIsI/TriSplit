# Repository Guidelines

## Project Structure & Module Organization

-   `src/TriSplit.Core`: shared domain models, data processors, and service interfaces consumed by other projects.
-   `src/TriSplit.Desktop`: WPF desktop shell, views, and view models.
-   `src/TriSplit.CLI`: lightweight command-line facade for scripted runs.
-   `profiles/`: sample profile JSON plus generated metadata. Do not hand-edit generated artifacts under `profiles/ProfileMetadata/`.
-   `scripts/`: utility PowerShell and diagnostic harnesses for local validation.
-   `dist/` and `out/`: build outputs; keep these out of commits.

## Build, Test, and Development Commands

```powershell
# Restore and build all projects
 dotnet build

# Run desktop app for ad-hoc testing
 dotnet run --project src/TriSplit.Desktop/TriSplit.Desktop.csproj

# Execute diagnostics harness (useful for mapping scenarios)
 dotnet run --project scripts/DiagHarness/DiagHarness.csproj
```

Running `dotnet build` prior to any pull request is mandatory; add `--configuration Release` when validating artifacts.

## Coding Style & Naming Conventions

-   C# files follow 4-space indentation; XAML uses 2 spaces (see `.editorconfig`).
-   Private fields use `_camelCase`; favor file-scoped namespaces and explicit accessibility.
-   Always wrap conditionals in braces and keep method bodies under 80 lines when practical.
-   XAML resources live beside their views; name bindings in `PascalCase` matching view-model properties.

## Testing Guidelines

-   Automated unit tests are not yet established; create them under a new `tests/` top-level folder using `xUnit` when adding coverage.
-   Use the diagnostics harness in `scripts/DiagHarness` plus in-app scenarios to reproduce bugs.
-   Include repro steps and observed outputs in pull request notes when adding or fixing behavior.

## Commit & Pull Request Guidelines

-   Make a commit after every change by Codex
-   Follow the existing sentence-case, action-oriented commit style (e.g., `Fix profile detection timing`). Avoid prefix tags unless coordinating a release.
-   Keep commits scoped; prefer one feature or fix per commit.
-   Pull requests must link related issues, describe validation steps (`dotnet build`, manual flows), and attach UI screenshots or logs when altering UX or processing output.
-   Request desktop QA when changes touch WPF UI, drag-fill behavior, or suggestion logic.

