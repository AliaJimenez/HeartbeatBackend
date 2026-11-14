# Heartbeat

NASA Space Apps 2026 — astronaut self-assessment and Mission Control platform.

## Projects in this repository

| Project | Type | Target |
| --- | --- | --- |
| Heartbeat.Shared | Class library | `net10.0` |
| Heartbeat.Api | ASP.NET Core (minimal APIs) | `net10.0` |
| Heartbeat.Client | Class library | `net10.0` |
| Heartbeat.Tests | xUnit | `net10.0` |

`Heartbeat.Desktop` (WPF) and `Heartbeat.Portal` (Blazor Web App) are developed in their own repositories and consume `Heartbeat.Shared`/`Heartbeat.Client` via submodule.

## Prerequisites

- .NET SDK 10.0.x

## Build everything

```bash
dotnet build Heartbeat.sln
```

## Run

### API

```bash
dotnet run --project src/Heartbeat.Api/Heartbeat.Api.csproj
```

## Test

```bash
dotnet test Heartbeat.sln
```

## Deploy

See [docs/DESPLIEGUE.md](docs/DESPLIEGUE.md): secrets via environment variables, SQLite on IIS, seed switch and smoke test.

## Contracts

Shared data model and API contracts live in [contracts/](contracts). The `reference-ranges.json` file is embedded into `Heartbeat.Shared`.
