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

## Check-up streaks (NR-06)

`Heartbeat.Shared.CheckUpStreaks.Calculate(recordedAt, now, timeZone)` returns a `StreakSummary` (current streak, best
streak, whether today is done, total days, `StreakBadge` and days to the next badge). It is pure, so the desktop
computes it offline from its local check-ups and gets the same result as the server. Days are calendar days in the
astronaut's time zone; the streak stays alive until the end of today if yesterday was done. Badges: Bronze 3, Silver 7,
Gold 30 days.

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
