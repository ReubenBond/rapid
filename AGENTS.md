# AGENTS.md - Rapid.Net

> **Note**: Keep this file updated as you learn more about the environment, CLI tools, and project conventions.

## Build & Test Commands
```bash
dotnet build Rapid.Net/Rapid.slnx                    # Build

# Run tests using dotnet run (xUnit v3 with Microsoft.Testing.Platform)
cd Rapid.Net/tests/Rapid.Tests
dotnet run -- --timeout 60s                                        # Run all tests with 60s timeout
dotnet run -- --timeout 60s --filter-class "*ClusterBasicTests"    # Single test class
dotnet run -- --timeout 60s --filter-method "*TestMethodName"      # Single test method
dotnet run -- --timeout 60s --filter-not-class "*Integration*"     # Exclude integration tests
dotnet run -- --list-tests                                         # List all tests
dotnet run -- --help                                               # Show all options
```

Note: This project uses xUnit v3 with Microsoft.Testing.Platform (MTP). Use `dotnet run --` to run tests, not `dotnet test`. The `--timeout` parameter sets a global test execution timeout (format: `<value>[h|m|s]`).

## CLI Tools
- **Shell**: Use `pwsh` (modern PowerShell), not `cmd` or legacy `powershell`
- **Platform**: Windows
- **ripgrep (rg)**: Use `rg` for searching, not `grep`. Example: `rg "pattern" --type cs`
- **sed**: Available for batch text replacements. Example: `sed -i 's/old/new/g' file.cs`

## Code Style (enforced via .editorconfig)
- **Framework**: .NET 10, nullable enabled, warnings as errors
- **Namespaces**: File-scoped (`namespace Foo;`)
- **Types**: Use `var` always, prefer pattern matching, use collection expressions
- **Braces**: Always use braces, newline before open brace (Allman style)
- **Naming**: `_camelCase` for private fields, `PascalCase` for public/constants, `IPrefix` for interfaces
- **Imports**: Sort System directives first, remove unused usings
- **Async**: Forward CancellationToken, avoid ConfigureAwait
- **Error handling**: Use throw helpers (ArgumentNullException.ThrowIfNull), rethrow to preserve stack
- **Regions**: Do not use `#region`/`#endregion` directives

## Project Structure
- `Rapid.Net/src/Rapid.Core/` - Main library
- `Rapid.Net/tests/Rapid.Tests/` - xUnit v3 tests (underscores allowed in test names)

## Debugging Tips
- **Log files**: Simulation tests produce log files that can be very large (multi-MB). When reading log files, always use tools to limit the amount of text read at once (e.g., `Get-Content -Tail 100` or `Get-Content -Head 100` in PowerShell, or use offset/limit parameters with Read tool).
