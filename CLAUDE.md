# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TriSplit is a WPF desktop application (.NET 8) for processing CSV/Excel data through HubSpot-oriented transformations. The solution consists of three projects:
- **TriSplit.Core**: Business logic for ingestion, mapping, and data splitting
- **TriSplit.Desktop**: WPF UI using MVVM pattern with CommunityToolkit.Mvvm
- **TriSplit.CLI**: Command-line interface for headless automation

## Build and Run Commands

```bash
# Build entire solution
dotnet build

# Run WPF Desktop application
dotnet run --project src/TriSplit.Desktop/TriSplit.Desktop.csproj

# Run CLI with help
dotnet run --project src/TriSplit.CLI/TriSplit.CLI.csproj -- --help

# Use PowerShell dev script for clean/build/run cycle
pwsh scripts/dev-run.ps1

# Watch mode for development
pwsh scripts/dev-run.ps1 -Watch
```

## Architecture Patterns

### MVVM Structure (Desktop)
- **ViewModels**: Located in `src/TriSplit.Desktop/ViewModels/`, with tabs in `ViewModels/Tabs/`
- **Views**: XAML files in `src/TriSplit.Desktop/Views/` and `Views/Tabs/`
- **Services**: DI-registered services in `src/TriSplit.Desktop/Services/`
- **Models**: View-specific models in `src/TriSplit.Desktop/Models/`

### Dependency Injection
Services are configured in `App.xaml.cs` using Microsoft.Extensions.DependencyInjection:
- Core services registered via `AddTriSplitCore()` extension method
- ViewModels registered as singletons for state preservation across tabs
- `IAppSession` manages cross-tab state (selected profile, loaded file)

### Data Flow
1. **Profiles** stored as JSON in `profiles/` directory
2. **Sample data** loaded from CSV/Excel files into memory
3. **Mappings** defined between source columns and HubSpot properties
4. **Transforms** applied in sequence (regex, format, normalization)
5. **Output** written to `out/{timestamp}/{inputFile}/` directory

### Key Interfaces
- `IProfileStore`: Manages profile CRUD operations
- `ISampleLoader`: Handles CSV/Excel file reading and caching
- `IDialogService`: Abstracts WPF dialogs for testability
- `IInputReader`: Factory for CSV/Excel readers based on file extension

## Code Style Requirements

Per `.editorconfig`:
- UTF-8 encoding with CRLF line endings
- 4-space indentation (2 for XAML)
- File-scoped namespaces preferred
- Private fields prefixed with underscore
- Explicit accessibility modifiers required
- Braces on all control blocks

## Current Known Issues

- PreviewTabViewModel.cs and ProfileStore.cs have syntax errors from incomplete refactoring
- No automated tests implemented yet
- Profile validation is minimal

## Profile Structure

Profiles define how source data maps to HubSpot entities with:
- Contact/Property/Phone field mappings
- Transform pipelines (regex, format, normalize)
- Deduplication keys
- Association labels and batch tags