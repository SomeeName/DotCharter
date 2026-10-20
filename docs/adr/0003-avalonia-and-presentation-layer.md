# 0003. Avalonia UI, with screen contracts in a Presentation layer that never references Avalonia

**Status:** Accepted

## Context

The first UI layer grew large: view models that used Avalonia types, did file
and process I/O and took dozens of dependencies. Screens could not be built or
tested without a window and the whole service graph.

## Decision

- The app is Avalonia on .NET 10, Windows first.
- Screens are rebuilt in three parts: a contract (interfaces over
  `INotifyPropertyChanged` and `ICommand`), a view model against Services
  abstractions, and a view. Contracts and view models live in
  `DotCharter.Presentation`, which references no Avalonia, Dock, Skia or
  JuceSharp assembly.
- `DotCharter.Presentation.Fakes` holds a fake per contract with named
  variants. The gallery app and the tests use them; the real UI never does.
- The dependency direction between assemblies is enforced by a test, not by
  convention. Size limits on files, types and constructors are enforced the
  same way.

## Consequences

- A new screen can be designed and reviewed in the gallery before it is wired
  to anything.
- More types and more indirection than a code-behind app. That is the price of
  testing screens on fakes.
- Old and new screens coexist while the migration finishes, so some view
  models still sit in `DotCharter.UI`.

## Links

[ARCHITECTURE.md](../ARCHITECTURE.md),
[docs/agents/ui/target-architecture.md](../agents/ui/target-architecture.md),
`DotCharter.Tests/Infrastructure/AssemblyLayeringTests.cs`.
