# Heartbeat

NASA Space Apps 2026 — astronaut self-assessment and Mission Control platform.

> **Datos simulados.** Las demostraciones, semillas y datos de ejemplo son ficticios y sirven para probar los flujos del sistema.
>
> **Herramienta de apoyo a la decisión; no diagnóstico médico.** Heartbeat no es un dispositivo médico y no diagnostica, previene ni trata enfermedades.
> No uses sus estados, alertas ni umbrales como consejo médico.

Los umbrales y fórmulas son configurables y se mantienen en el catálogo de indicadores, no en la UI. Cada entrada documenta su `source` y el estado de la evidencia (`verified` o `heuristic`). Consulta también la [data card](docs/DATA-CARD.md) para conocer el origen y las limitaciones de los datos.

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

### Claude assistant (optional)

The authenticated crew endpoint `POST /api/crew/v1/assistant/chat` explains the caller's current `RiskStatus` and open
alerts in plain language through Anthropic Claude. Mission Control can use
`POST /api/mc/v1/crew/{id}/assistant/chat` when the signed-in user has `vitals.view`. It is disabled by default, never participates in evaluation or sync,
and receives no profile notes, messages, date of birth, credentials or tokens. If Claude is unavailable, the rest of
Heartbeat continues to work normally.

Configure the secret only in the API project; never commit it or place it in a frontend:

```powershell
cd src/Heartbeat.Api
dotnet user-secrets set "AI:Enabled" "true"
dotnet user-secrets set "AI:ApiKey" "sk-ant-..."
dotnet user-secrets set "AI:Model" "<model-enabled-for-your-Anthropic-account>"
```

Production uses the equivalent environment variables `AI__Enabled`, `AI__ApiKey` and `AI__Model`. The desktop must use
`CrewApi__Mode=Live` to reach this endpoint. An Anthropic user subscription and Anthropic API billing are separate.

## Test

```bash
dotnet test Heartbeat.sln
```

## Smartwatch

Galaxy Watch 5 (FC en vivo) y Samsung Health (vía WatchSync): ver [docs/SMARTWATCH.md](docs/SMARTWATCH.md) para la arquitectura, la puesta en marcha y las limitaciones.

## Deploy

See [docs/DESPLIEGUE.md](docs/DESPLIEGUE.md): secrets via environment variables, SQLite on IIS, seed switch and smoke test.

## Data card

Transparencia (qué es real, qué es simulado, semillas, fórmulas, fuentes): [docs/DATA-CARD.md](docs/DATA-CARD.md).

## Contracts

Shared data model and API contracts live in [contracts/](contracts). The `reference-ranges.json` file is embedded into `Heartbeat.Shared`.
