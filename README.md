# Heartbeat

NASA Space Apps 2026 — astronaut self-assessment and Mission Control platform.

## Projects

| Project | Type | Target |
| --- | --- | --- |
| Heartbeat.Shared | Class library | 
et10.0 |
| Heartbeat.Api | ASP.NET Core (minimal APIs) | 
et10.0 |
| Heartbeat.Client | Class library | 
et10.0 |
| Heartbeat.Desktop | WPF application | 
et10.0-windows |
| Heartbeat.Portal | Blazor Web App | 
et10.0 |
| Heartbeat.Tests | xUnit | 
et10.0 |

## Prerequisites

- .NET SDK 10.0.x
- Windows (required to build/run Heartbeat.Desktop)

## Build everything

`ash
dotnet build Heartbeat.sln
`

## Run

### API

`ash
dotnet run --project src/Heartbeat.Api/Heartbeat.Api.csproj
`

### Portal

`ash
dotnet run --project src/Heartbeat.Portal/Heartbeat.Portal.csproj
`

### Desktop (Windows only)

`ash
dotnet run --project src/Heartbeat.Desktop/Heartbeat.Desktop.csproj
`

## Test

`ash
dotnet test Heartbeat.sln
`

## Contracts

Shared data model and API contracts live in [contracts/](contracts). The eference-ranges.json file is embedded into Heartbeat.Shared.
