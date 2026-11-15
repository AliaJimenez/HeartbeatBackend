# QA de punta a punta: SPE + CO₂ alto + privación de sueño (HB-QA-01, #126)

Qué se probó, cómo repetirlo, qué bugs salieron y qué queda como limitación. Todo dato del escenario es **simulado** y no es un diagnóstico.

## 1. Qué cubre y qué no

| Parte | Estado |
| --- | --- |
| API, sync, motor de reglas y datos del seed | **Automatizado**: `tests/Heartbeat.Tests/ScenarioE2ETests.cs` (HTTP real, logins reales, cliente real del escritorio `CrewApiClient`, semilla de demo) |
| Escritorio WPF (modo simple, radiación, ambiente) y portal Blazor (vista de misión, alertas) | **No verificado desde este repositorio**: viven en otros repos. Lista de comprobación en la sección 5 |
| Congelar funciones 1 h antes del cierre | Es un acuerdo del equipo, no un cambio de código |

## 2. El escenario automatizado

Con el seed (3 astronautas, 30 días):

1. **El seed cuenta las tres historias en el portal.** Ana: presión crítica con una alerta abierta y un reporte sin leer. Kenji: el sueño baja de 7 a 4,5 h y el ánimo a 2 (watch, nunca crítico). Leila: un evento de partículas solares (SPE) hace 10 días, ahora en *watch* de radiación. El CO₂ tuvo un pico crítico hace 10 días y hoy es normal. Vista de misión con perfil, retardo e índices simulados.
2. **Los eventos del seed tienen sus alertas**, como las habría creado el sync (ver bugs, sección 3).
3. **Escenario en vivo con Kenji.** Cae el enlace con la Tierra y el escritorio sigue trabajando con el código compartido:
   - registra un SPE de 140 mSv (56 % del límite por evento), un CO₂ de 5,8 mmHg (crítico) y un check-up con 3,2 h de sueño (crítico);
   - intenta sincronizar sin enlace: falla y **la cola queda intacta**;
   - el portal no ve nada todavía;
   - vuelve el enlace: un solo sync empuja los tres elementos; **repetirlo no duplica nada** (idempotente).
4. **Portal tras la reconexión:** una alerta abierta de cada tipo (check-up, radiación, ambiente); tarjeta de la tripulación en crítico con 3 alertas; semáforo por peligro (radiación *watch*; aislamiento y confinamiento *critical* por el sueño; ambiente hostil *critical* por el CO₂) y global *critical*; resumen de radiación, ambiente y serie de sueño.
5. **Lo que el escritorio calculó sin red coincide con el servidor** para esos tres peligros (el motor y el evaluador son los mismos).
6. **Respuesta:** el cirujano reconoce las tres alertas y escribe; el siguiente sync del escritorio trae las 3 confirmaciones y el mensaje.
7. **Robustez offline:** un token de acceso que caducó sin enlace se renueva solo al reconectar; un elemento inválido de la cola se rechaza **con motivo** sin bloquear al resto.

## 3. Bugs encontrados

**Corregidos en este PR**

| Bug | Corrección |
| --- | --- |
| El seed guardaba el SPE de Leila y los picos de CO₂ críticos **sin alerta**, aunque POST `/sync` las crea. El portal mostraba las alertas vacías para el evento que cuenta la historia | `DemoDataSeeder` crea las alertas de radiación y de ambiente con las mismas reglas del sync, ya reconocidas por el cirujano (los eventos tienen 10 días): no añade alertas abiertas |
| Las lecturas de ambiente del seed salían con 14 decimales (`humedad = 40,81839100495837`) | Se redondean a 2 decimales al guardarlas |

**Limitaciones (no se corrigen aquí; proponer issue)**

| # | Limitación | Propuesta |
| --- | --- | --- |
| L1 | **P1.** La confirmación de una alerta de **ambiente** llega al escritorio sin forma de saber de cuál es: `AcknowledgedAlert` solo trae `CheckUpId` o `RadiationDoseId`, y en las de ambiente ambos son `null` | Añadir `AlertId`/`Kind` (y el indicador) a `AcknowledgedAlert`: cambio de contrato, con su PR y revisión de backend y frontend |
| L2 | `RadiationSummaries.Build` (resumen de radiación) vive en la API, no en `Heartbeat.Shared`: sin red, el escritorio tiene que reimplementar ese cálculo para el semáforo | Mover el cálculo a `Shared` |
| L3 | La explicación del CO₂ crítico no da pauta de acción (los demás peligros sí) y mezcla el nombre en español del catálogo con texto en inglés: *"Dióxido de carbono (pCO₂) is critical…"* | Añadir guía por indicador al catálogo y unificar el idioma |
| L4 | Con varios peligros en el mismo estado, la explicación global abre con el primero en el orden de la lista (aislamiento antes que ambiente), no con el más urgente | Priorizar por gravedad y luego por tipo de evento |
| L5 | `ICrewApiClient.IsOnline` mira la interfaz de red del equipo, no si el enlace con la Tierra responde: con tarjeta de red pero sin ruta dice "en línea" | Considerar el interruptor "Enlace con la Tierra" del escritorio como fuente de verdad |
| L6 | El seeder no modifica astronautas que ya existen: una base creada antes de este PR **no recibe** las alertas nuevas ni el redondeo | Borrar `heartbeat.db` (y `-shm`/`-wal`) con la API detenida y volver a arrancar con `Seed__Enabled=true` |
| L7 | El pico de CO₂ del seed es de hace 10 días: hoy el ambiente sale normal. Para ver CO₂ alto en vivo hay que empujar una lectura | Es por diseño; para la demo, usar el escenario de la sección 2 |

## 4. Cómo repetirlo

Automático, junto con el resto de las pruebas:

```bash
dotnet test Heartbeat.sln --filter ScenarioE2ETests
```

Manual con la API local y datos de demo (`dotnet run --project src/Heartbeat.Api --launch-profile demo`, las cuentas están en `docs/DESPLIEGUE.md`):

1. Escritorio: iniciar sesión como `k.sato` y **apagar el enlace con la Tierra**.
2. Registrar un check-up con sueño de 3,2 h, una dosis SPE de 140 mSv y un CO₂ de 5,8 mmHg.
3. Portal (`j.okafor`): comprobar que **aún no** hay alertas de Kenji.
4. Encender el enlace y sincronizar. Ver las tres alertas, el semáforo en rojo y el resumen de radiación y ambiente.
5. Reconocer las alertas y responder desde el portal; sincronizar otra vez en el escritorio y ver la respuesta.

## 5. Lista para el equipo de frontend (pendiente de verificar)

Escritorio:

- [ ] Modo simple: semáforo por peligro y global, con la explicación en lenguaje simple y la etiqueta "Datos simulados".
- [ ] Panel de radiación: SPE de Leila en *watch* (72 % del límite por evento) y el total acumulado.
- [ ] Panel de ambiente: valores redondeados, historial con el pico de CO₂ y estado actual normal.
- [ ] Con el enlace apagado se puede registrar todo y la cola se conserva; al encenderlo sincroniza y vacía la cola.
- [ ] Un elemento rechazado por el servidor se muestra con su motivo y no bloquea el resto.

Portal:

- [ ] Vista de misión: retardo, gravedad, perfil de entorno, índices de aislamiento y confinamiento (simulados) y semáforo de la tripulación por peligro.
- [ ] Alertas: las abiertas (Ana) y el historial con las de radiación y ambiente ya atendidas (Leila y los picos de CO₂).
- [ ] Tras la reconexión del escenario: tres alertas nuevas de Kenji, tarjeta en rojo, y las respuestas llegan al escritorio.

Cualquier fallo en estas pantallas se abre como issue P0 si rompe la demo, o se anota aquí como limitación.
