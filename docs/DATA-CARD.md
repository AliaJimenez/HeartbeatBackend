# Heartbeat — Data card

Transparencia para los jueces: qué es real, qué es simulado, de dónde salen las cifras y cómo se reproducen.

## 1. Qué es real

- **Los valores de entrada son reales en su origen**: la tripulación del demo introduce valores reales (presión, frecuencia, sueño, ánimo, dosis) o los importa de un reloj.
- **Las fórmulas y rangos son públicos**: cada banda de `contracts/reference-ranges.json` y `contracts/indicators.catalog.json` cita su fuente (`source`) y su estado (`status`).
- **El evaluador es compartido**: `Heartbeat.Shared.HealthEvaluator` corre igual en el escritorio y en el servidor, así que el resultado no cambia entre offline y online.
- **El código es abierto** y se construye con .NET 10 + EF Core + ASP.NET Core. Sin servicios externos en el demo.

## 2. Qué es simulado

Todo lo que no mide la tripulación está marcado como **"Datos simulados"** en la UI y en el código (`Simulated = true` en `EnvironmentReading`):

- **Ambiente de cabina** (CO₂, O₂, presión, temperatura, humedad, luz, ruido): series sintéticas con semilla fija.
- **Radiación** (GCR, SPE): dosis acumuladas simuladas, tasas por entorno y porcentajes de límite.
- **IOP / visión y PVT-B (vigilancia)**: valores simulados, incluida la racha de privación de sueño de Kenji.
- **Aislamiento y confinamiento**: índices calculados con las fórmulas del catálogo sobre datos simulados.
- **Phantoms**: perfiles digitales con cambios simulados de hueso, músculo, fluidos e IOP.
- **Perfiles comerciales** (minería, Antártida, submarino, offshore): mismos peligros con otros umbrales.

Nada de esto es un diagnóstico médico. La app es una herramienta de autoevaluación.

## 3. Semillas y reproducibilidad

- `DemoDataSeeder` (`src/Heartbeat.Api/Data/Seeding/DemoDataSeeder.cs`) genera la demo con semilla fija: `Random(42)` para ambiente, `Random(7)` para IOP/PVT.
- Las mismas semillas producen siempre los mismos datos (prueba en `DemoDataSeederTests`).
- Eventos en fechas conocidas:
  - **Día 20 (de 30)**: pico de CO₂ y SPE en Leila Haddad (38.4 → 39.2 °C).
  - **Últimos 14 días**: privación de sueño en Kenji Sato (sueño de 7 h a 4.5 h, ánimo a 2).
  - **Día 30**: pico crítico de presión arterial en Ana Rivera, con alerta abierta y reporte.
- Para repetir la demo desde cero: borra `heartbeat.db` (y `-shm`/`-wal`) con la API detenida y arranca con `Seed__Enabled=true`.

## 4. Fórmulas

| Índice | Fórmula | Estado |
| --- | --- | --- |
| Aislamiento (0–1) | 0.3·norm(retardo) + 0.2·norm(distancia) + 0.3·(1/√tripulación) + 0.2·norm(horas sin contacto) | `heuristic` |
| Confinamiento (0–1) | (min(m³/persona_ref / m³/persona, 1) + min(horas/24, 1)) / 2 | `heuristic` |
| % límite de carrera (radiación) | dosis acumulada / 600 mSv · 100 | `verified` (límite NASA) |
| % límite SPE | dosis del evento / 250 mSv · 100 | `verified` (límite NASA) |
| Phantoms (cambio por semana) | hueso -0.25 %/sem y musculo -0.6 %/sem a 0 g, x (1 - g), x (1 - ejercicio hasta 60 %); fluidos +2 % e IOP +3 mmHg como maximo | `heuristic` |
| Tasa GCR por entorno | ISS 0.5, Luna 1.3, Marte 0.7, espacio profundo 1.8 mSv/día | `heuristic` |
| Semáforo | peor de los peligros; normal/watch/critical por umbral | `verified` (regla) |

Todos los pesos y mínimos de referencia son configurables y viven en `Heartbeat.Shared.IsolationConfinement` y en el catálogo, no en el código de UI.

## 5. Fuentes

Las fuentes están en `contracts/reference-ranges.json` (`sources`) y `contracts/indicators.catalog.json` (`sources`). Incluyen NASA HRP (5 Hazards of Human Spaceflight, Human Research Roadmap), la Agencia Espacial Canadiense (medicina espacial y radiación), NHLBI, MedlinePlus, ADA y NIMH. Los enlaces se verificaron el 14 nov 2026.

## 6. Qué es `verified` y qué es `heuristic`

- `verified`: el umbral o la fórmula sale de una referencia pública citada (límites de radiación de NASA, rangos clínicos generales).
- `heuristic`: valor de demo, marcado como tal. Incluye CO₂ ≈ 3 mmHg, gravedades, retardos, volumen mínimo, tasas GCR por entorno, índices de aislamiento/confinamiento.

Ningún valor `heuristic` se presenta como oficial en el pitch.

## 7. Limitaciones

- Los rangos son demo-grade, basados en referencias clínicas generales para adultos en reposo. NASA HRP no publica umbrales; solo explica por qué importa cada indicador.
- Todo dato nuevo (ambiente, radiación, IOP, PVT, aislamiento, confinamiento, phantoms) es simulado y se etiqueta así.
- La app es una herramienta de autoevaluación, no un dispositivo médico y no diagnostica.
- Base de datos: SQLite en el servidor demo; en producción se cambia a SQL Server por configuración.
- Dead links: revalidar antes de la entrega final.
