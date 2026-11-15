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
- **Perfiles comerciales** (minería, Antártida, submarino, offshore): mismos peligros con otros umbrales. Viven en `profiles` de `contracts/indicators.catalog.json` y salen por `GET /api/mc/v1/profiles`. Todos sus umbrales y entornos típicos son `heuristic`: las fuentes (NIOSH, NASA analogs, HSE) describen el dominio, no publican estos umbrales.

Nada de esto es un diagnóstico médico. La app es una herramienta de autoevaluación.

## 3. Semillas y reproducibilidad

- `DemoDataSeeder` (`src/Heartbeat.Api/Data/Seeding/DemoDataSeeder.cs`) genera la demo con semilla fija: `Random(42)` para ambiente, `Random(7)` para IOP/PVT.
- Las mismas semillas producen siempre los mismos datos (prueba en `DemoDataSeederTests`).
- Eventos en fechas conocidas:
  - **Día 20 (de 30)**: pico de CO₂ del hábitat (está en el historial de los tres) y, en Leila Haddad, un SPE y la fiebre (38.4 → 39.2 °C). Cada evento tiene su alerta, ya reconocida por el cirujano (ver `docs/QA-E2E.md`).
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

## 7. Validación con datos abiertos de NASA (OSDR / LSDA)

Se contrastaron indicadores del catálogo con estudios del [NASA Open Science Data Repository](https://osdr.nasa.gov/bio/repo/) y del Life Sciences Data Archive. La evidencia está en el bloque `validation` de cada indicador en `contracts/indicators.catalog.json` (fecha, estudios, hallazgo, qué respalda y qué no). El cargador de `Heartbeat.Shared` valida que los estudios existan en `sources` y **no deja marcar `verified` un indicador cuya validación no sea `consistent`**.

| Indicador | Estudios | Qué muestran | Resultado |
| --- | --- | --- | --- |
| `iop` (mmHg) | OSD-816 (ojos humanos ex vivo), OSD-715 y RR-9 (ratones, 35 días en la ISS) | El análogo de SANS usa 16 mmHg como control y 21 mmHg como elevada; la IOP medida en vuelo quedó entre 14.4 y 20.3 mmHg | `partial`: respalda el límite normal/watch de 21; no respalda 25 ni 10 |
| `radiationRate` (mSv/día) | OSD-714 (dosímetros Bio PADLES en la ISS) | Dosis equivalente de 0.48 y 0.79 mSv/día (0.31 y 0.36 mGy/día absorbida) | `partial`: el rango típico de la ISS cae en `normal`; no respalda los bordes de 1.5 y 3 |
| `gravity` (g) | OSD-714 (centrífuga en la ISS, 1/6 g) | La gravedad lunar evitó la atrofia del sóleo pero no el cambio de tipo de fibra | `needsReview`: marcar `critical` por debajo de 0.3 g parece exagerado para la Luna; las bandas no se han cambiado |

Como contraste independiente, el experimento canadiense **Radi-N2** midió en tripulación de la ISS una dosis de neutrones típica de 150 µSv/día (0.15 mSv/día), aproximadamente el 30 % de la radiación de la estación. Confirma que esa componente cae en la banda `normal`, pero no define los umbrales `watch` o `critical`. La CSA usa además un límite efectivo de carrera de 1,000 mSv; Heartbeat conserva el estándar NASA de 600 mSv y no mezcla ambos límites.

Consecuencias:

- **Ninguno se promovió a `verified`**, porque ningún estudio respalda los umbrales extremos. Un resultado `partial` deja el indicador en `heuristic`.
- **`radiationRate` pasó de `verified` a `heuristic`.** Estaba marcado `verified` con la fuente del límite de carrera de NASA, que fija 600 mSv y 250 mSv por SPE, pero no bandas diarias. Los límites de `radiation` (acumulada) sí vienen de NASA y siguen `verified`.
- Los datos son de ratones y de tejido humano ex vivo, no de astronautas: sirven para contrastar órdenes de magnitud, no para validar clínicamente.
- Pendiente de revisión del equipo: las bandas de `gravity` para la Luna.

## 8. Limitaciones

- Los rangos son demo-grade, basados en referencias clínicas generales para adultos en reposo. NASA HRP no publica umbrales; solo explica por qué importa cada indicador.
- Todo dato nuevo (ambiente, radiación, IOP, PVT, aislamiento, confinamiento, phantoms) es simulado y se etiqueta así.
- La app es una herramienta de autoevaluación, no un dispositivo médico y no diagnostica.
- Base de datos: SQLite en el servidor demo; en producción se cambia a SQL Server por configuración.
- Dead links: revalidar antes de la entrega final.
