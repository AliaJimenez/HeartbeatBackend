# Heartbeat — Plan de mejoras

15 nov 2026 · Aprobado. Los issues están en el Project con milestones **Bloque 1 / 2 / 3 / Cierre** y etiquetas `frontend|backend|data` + `P0|P1|P2`.

## 0. Lo que ya existe (no se rehace)

El núcleo está completo: los issues #1–#51 están cerrados y solo siguen abiertos #47 (pitch) y la épica del smartwatch (#52–#57).

| Pedido del prompt | Qué ya existe | Cómo lo trato |
| --- | --- | --- |
| Radiación | Registro manual de dosis en mSv con total acumulado (#51): `RadiationDoses`, `RadiationSummaries`, `RadiationView` en el escritorio | **Se extiende** (tipo, tasa, % del límite, SPE); no es una pantalla nueva |
| Sync store-and-forward (HB-BE-09) | Cola offline con reintentos y orden (#15, #32) e interruptor "Enlace con la Tierra" (`SimulatedLinkService`, solo on/off) | Solo falta el **retardo** simulado: el ticket se reduce a eso |
| Alertas y estado de sync (HB-FE-08) | Barra de estado en el escritorio, página de alertas y contador en el portal | **Se elimina.** Las alertas nuevas entran por la tabla `Alert` actual y aparecen solas en el portal |
| Catálogo con fuente | `contracts/reference-ranges.json` con `source` por indicador y `hazard`, embebido en Shared | **Se extiende** con `status` y un catálogo nuevo para las familias nuevas |
| "Muñecos" / cambios físicos | Figura corporal con medidas por región y cambio respecto a la medida anterior (`BodyPanelView`, #48) | Los phantoms **reutilizan** esa figura |
| Contratos (HB-00) | `contracts/CONTRACTS.md` + DTOs en `Heartbeat.Shared` | HB-00 = **CONTRACTS v0.2**, no un JSON Schema aparte |
| Pitch (HB-TM-01) | Issue #47 abierto | **Se reutiliza #47**, añadiendo la historia comercial |

## 1. Resumen de la división

| Track | Responsable | Qué hace |
| --- | --- | --- |
| BE1 | @AslanGuzman | Contrato v0.2, dominio y migración, perfiles de misión y retardo, índices en la API, integración E2E |
| BE2 | @MontserratNunez | APIs de radiación v2, ambiente y visión, **motor de semáforo**, phantoms, pitch (#47) |
| DA1 | @andujarsky-lang | Catálogo, generador simulado con semilla, modelo de radiación, línea base, índices, data card |
| FE1 | @AliaJimenez | Escritorio WPF: modo simple con semáforo y radiación v2 |
| FE2 | @IsaiasMorillo | Portal: vista de misión, phantoms, "Datos simulados". También el panel de ambiente del **escritorio** (ya trabajó en ese repo y FE1 está más cargada) |

**Reglas que se mantienen:** Data publica el catálogo y la lógica en `Heartbeat.Shared`. Backend la consume. Frontend solo lee DTOs.

El **motor de semáforo va en `Heartbeat.Shared`**, igual que el evaluador actual, para que el escritorio sin conexión muestre el mismo resultado que el servidor. Todo dato nuevo es simulado, se etiqueta así y no hace diagnósticos.

**Ruta crítica:** HB-00 → HB-DA-01 → HB-BE-01 → (HB-DA-03 → HB-BE-02) + HB-BE-03 → **HB-BE-07 semáforo** → HB-FE-01 / HB-FE-05 → HB-QA-01.

## 2. Lista de issues propuesta

Milestones nuevos: **Bloque 1** (contrato y base), **Bloque 2** (P0), **Bloque 3** (P1/P2), **Cierre**. Las horas son estimadas.

### Bloque 1 — desbloquea a todos (≈ primeras 2 h)

| ID | Título | Resp. | Prio | h | Depende de |
| --- | --- | --- | --- | --- | --- |
| HB-00 | Contrato v0.2: DTOs `EnvironmentProfile`, `EnvironmentReading`, `RadiationSummary` v2, `HazardStatus`/`RiskStatus`, `PhantomProfile` en Shared + CONTRACTS.md §10 | BE1 | P0 | 1 | — |
| HB-DA-01 | Catálogo `contracts/indicators.catalog.json` (radiación, CO₂, O₂, presión, temp., humedad, luz, ruido, IOP/visión, PVT-B, estrés, cohesión, contacto, gravedad, retardo) con `unit`, `hazard`, `source`, `status`; añade `status` a `reference-ranges.json`; embebido y validado al cargar | DA1 | P0 | 2 | HB-00 |
| HB-BE-01 | Dominio + migración: perfil de entorno en `Mission` (g, distancia, tripulación, volumen habitable), entidad `EnvironmentReading`, `RadiationDose.Type`; carga del catálogo | BE1 | P0 | 2 | HB-00, HB-DA-01 |

### Bloque 2 — P0 en paralelo

| ID | Título | Resp. | Prio | h | Depende de |
| --- | --- | --- | --- | --- | --- |
| HB-DA-02 | Generador simulado con semilla fija (amplía `DemoDataSeeder`): ambiente, radiación, IOP y PVT por astronauta, con eventos SPE, pico de CO₂ y privación de sueño | DA1 | P0 | 2 | HB-DA-01 |
| HB-DA-03 | Modelo de radiación en Shared (amplía `RadiationDoses`): tasa por entorno, tipo GCR/SPE/atrapada, % de 600 mSv de carrera y de 250 mSv por SPE | DA1 | P0 | 2 | HB-DA-01 |
| HB-BE-02 | API de radiación v2 (amplía `RadiationSummaries`): tipo, tasa, % del límite; alerta SPE en la tabla `Alert` | BE2 | P0 | 1.5 | HB-BE-01, HB-DA-03 |
| HB-BE-03 | API de ambiente: lecturas por sync (crew) + `GET` en MC; alertas por umbral del catálogo | BE2 | P0 | 2 | HB-BE-01 |
| HB-BE-04 | Perfil de entorno de la misión (crew y MC) + retardo de comunicación calculado (distancia / c) | BE1 | P0 | 1.5 | HB-BE-01 |
| HB-BE-07 | **Motor de reglas + semáforo unificado** por los 5 peligros y global, con "por qué" en lenguaje simple; en Shared + `GET /crew/{id}/risk` | BE2 | P0 | 3 | HB-BE-02, HB-BE-03 |
| HB-FE-01 | Escritorio: **modo simple** con semáforo por peligro y global, onboarding y glosario corto; etiqueta "Datos simulados" | FE1 | P0 | 3 | HB-BE-07 (con mock mientras tanto) |
| HB-FE-02 | Escritorio: radiación v2 en `RadiationView` (barra vs 600 mSv, tipo, línea de tiempo, alerta SPE) | FE1 | P0 | 2 | HB-BE-02 |
| HB-FE-03 | Escritorio: panel de ambiente + gravedad (CO₂, O₂, presión, temp., luz, ruido) con estado por color | FE2 | P0 | 2.5 | HB-BE-03, HB-BE-04 |
| HB-FE-05 | Portal: **vista de misión** (distancia, retardo, gravedad, semáforo de la tripulación por peligro; los índices se añaden cuando llegue HB-BE-06) | FE2 | P0 | 3 | HB-BE-04, HB-BE-07 |
| HB-FE-11 | "Datos simulados" visible en el portal + aviso de no diagnóstico en el README de los 3 repos | FE2 | P0 | 0.5 | — |

### Bloque 3 — P1 (suman puntos)

| ID | Título | Resp. | Prio | h | Depende de |
| --- | --- | --- | --- | --- | --- |
| HB-DA-05 | Índices de aislamiento y confinamiento (fórmulas del prompt, pesos configurables, `status: heuristic`) + pruebas | DA1 | P1 | 2 | HB-DA-01 |
| HB-BE-06 | API de aislamiento/confinamiento, conectada al semáforo de "Conducta" | BE1 | P1 | 1 | HB-DA-05, HB-BE-04 |
| HB-DA-04 | Línea base personal + desviaciones (z-score/EWMA) por indicador | DA1 | P1 | 2 | HB-DA-02 |
| HB-BE-05 | API de presión ocular/visión con tendencia y correlación con CO₂ | BE2 | P1 | 1.5 | HB-BE-03 |
| HB-FE-04 | Escritorio: visión/IOP con tendencia y nota SANS | FE1 | P1 | 2 | HB-BE-05 |
| HB-BE-09 | Retardo de comunicación simulado en el enlace del escritorio (amplía `SimulatedLinkService` con el retardo del perfil) | BE1 | P1 | 1.5 | HB-BE-04 |
| HB-DA-06 | Phantoms: perfiles (sexo, edad, masa) y cambios simulados de hueso, músculo, fluidos e IOP, `heuristic` | DA1 | P1 | 2 | HB-DA-02 |
| HB-BE-08 | API de comparación de phantoms | BE2 | P1 | 1.5 | HB-DA-06 |
| HB-FE-06 | Portal: comparador de phantoms lado a lado | FE2 | P1 | 2.5 | HB-BE-08 |
| HB-DA-08 | Data card: origen, limitaciones, fórmulas, fuentes, "datos simulados" | DA1 | P1 | 1 | HB-DA-01..03 |

### P2 — solo si sobra tiempo

| ID | Título | Resp. | Prio | h | Depende de |
| --- | --- | --- | --- | --- | --- |
| HB-BE-11 | Perfiles comerciales (minería, Antártida, submarino, offshore): mismos peligros con otros umbrales | BE1 | P2 | 2 | HB-BE-04 |
| HB-FE-07 | Portal: selector de perfil de entorno / vista comercial | FE2 | P2 | 2 | HB-BE-11 |
| HB-BE-10 | Exportar CSV por astronauta y misión (sin PDF) | BE2 | P2 | 1 | HB-BE-07 |
| HB-FE-10 | Botón de exportar en el portal | FE2 | P2 | 0.5 | HB-BE-10 |
| HB-FE-09 | i18n ES/EN y accesibilidad (contraste, no depender solo del color) | FE2 | P2 | 2 | — |
| HB-DA-07 | Rangos desde datos abiertos de NASA (OSDR/LSDA) | DA1 | P2 | 3 | HB-DA-01 |

### Cierre — todo el equipo

| ID | Título | Resp. | Prio | h | Depende de |
| --- | --- | --- | --- | --- | --- |
| HB-QA-01 | E2E: escenario SPE + CO₂ alto + privación de sueño en las dos apps; corrección de bugs | BE1 lidera | P0 | 2 | HB-BE-07, HB-FE-01, HB-FE-05, HB-DA-02 |
| #47 (existente) | Pitch: añadir guion de 3 min, historia comercial (usuarios sin conocimiento técnico + trabajos difíciles) y capturas | BE2 lidera, todos | P0 | 2 | HB-QA-01 |

**Carga P0 por persona:** Aslan 6.5 h · Montserrat 6.5 h + pitch · Andujar 6 h · Aliandy 5 h · Isaías 6 h.

## 3. Cambios respecto al prompt original

1. **HB-FE-08 eliminado:** las alertas y el estado de sync ya existen.
2. **HB-BE-09 reducido:** de 3 h a 1.5 h, porque la cola store-and-forward ya existe y solo falta el retardo.
3. **HB-FE-05 ya no espera a HB-BE-06:** era P0 dependiendo de un P1. Ahora los índices se suman después.
4. **HB-FE-07 pasa a P2:** era P1 pero dependía de HB-BE-11, que es P2.
5. **HB-FE-09 dividido:** la etiqueta "Datos simulados" es una restricción obligatoria, así que pasa a P0 como **HB-FE-11**. La i18n se queda en P2.
6. **Exportación solo en CSV:** el PDF cuesta más y aporta poco a la demo.
7. **Reasignaciones para equilibrar carga:**
   - **HB-BE-02** (radiación) pasa a BE2, porque BE1 tenía 8 h de P0.
   - **HB-FE-03** (panel de ambiente WPF) pasa a FE2, porque FE1 tenía 7.5 h.
8. **HB-TM-01 = #47:** se reutiliza el issue existente.

## 4. Riesgos y plan B (qué se recorta primero)

1. Todos los P2.
2. **Cadena de phantoms** (HB-DA-06, HB-BE-08, HB-FE-06), unas 6 h. La figura corporal que ya existe cubre la idea en el pitch.
3. HB-DA-04 línea base: se menciona en el pitch como trabajo futuro.
4. Cadena SANS (HB-BE-05, HB-FE-04): el IOP se puede mostrar solo en el panel de ambiente.
5. HB-BE-09 retardo: se explica con el interruptor de enlace que ya existe.
6. **No se recortan:** semáforo (HB-BE-07), modo simple (HB-FE-01), vista de misión (HB-FE-05), radiación v2 y "Datos simulados".

**Otros riesgos:**
- **Valores marcados "verificar" en el prompt** (CO₂ ≈ 3 mmHg, gravedades, retardos, volumen mínimo): van con `status: heuristic` hasta confirmarlos. Ninguno se cita como oficial en el pitch.
- **Migración nueva de EF sobre la base desplegada:** se prueba primero en local con el seed.
- **Congelar features 1 h antes del cierre.**

## 5. Reglas de trabajo

- **Un PR por ticket**, siempre hacia `dev`. Usa `Closes #n`, o `Closes AliaJimenez/HeartbeatBackend#n` desde los repos de frontend.
- **Revisión cruzada entre tracks:** frontend ↔ backend ↔ data. La plantilla `.github/pull_request_template.md` trae el checklist.
- Dependencias: relación nativa *Blocked by*. Al fusionar, pasa a *Listo* en el Project los tickets que dependían del tuyo.
- **Feature freeze 1 h antes del cierre.**
