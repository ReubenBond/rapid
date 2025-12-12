# AGENTS.md - Rapid.Net

## Build & Test Commands
```bash
dotnet build Rapid.Net/Rapid.slnx                    # Build
dotnet test Rapid.Net/Rapid.slnx                     # Run all tests
dotnet test Rapid.Net/Rapid.slnx --filter "FullyQualifiedName~TestMethodName"  # Single test
dotnet test Rapid.Net/Rapid.slnx --filter "FullyQualifiedName~TestClassName"   # Single class
dotnet test Rapid.Net/Rapid.slnx --filter "FullyQualifiedName!~Integration"    # Unit tests only
```

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
