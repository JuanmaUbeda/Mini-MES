# Mini-MES — Industrial Batch Management System

A small, simulated MES (Manufacturing Execution System) for an industrial mixing process.
Container A and Container B feed ingredients into a mixer; batches are created, stored,
exported as XML, and sent to a separate simulated external service that accepts or rejects them.

> This is a software simulation. It does not communicate with PLCs or control real equipment.

## Tech stack

- C# / .NET 8, ASP.NET Core Minimal APIs
- SQLite via Entity Framework Core
- XML + XSD for batch data exchange
- Plain HTML/CSS/JavaScript for the process overview
- xUnit for tests

## Solution structure

| Project | Responsibility |
|---|---|
| `src/MiniMes.Core` | Domain models and business rules (no web or DB dependencies) |
| `src/MiniMes.Data` | Database access (EF Core, SQLite) |
| `src/MiniMes.Xml` | XML generation and XSD validation |
| `src/MiniMes.Api` | Main REST API and web UI |
| `src/MiniMes.ErpSimulator` | Separate service that accepts or rejects batches |
| `tests/MiniMes.Tests` | Automated tests |

## Build

```
dotnet build
dotnet test
```

## Status

Work in progress — project skeleton only.
