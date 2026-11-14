# Heartbeat — Orden recomendado de tickets

Orden sugerido para cada persona. El criterio es hacer primero lo que desbloquea a más gente (la ruta crítica), luego lo obligatorio (P0) y al final P1, P2 y lo futuro.

- Los números son issues de [AliaJimenez/HeartbeatBackend](https://github.com/AliaJimenez/HeartbeatBackend/issues).
- *(espera #n)* indica que ese ticket depende de otra persona: revisa en *Relationships* que esté cerrado antes de empezarlo.
- Si te toca esperar, programa contra los contratos con un fake y conecta lo real cuando se cierre la dependencia.

**Ruta crítica:** #1 SCAF (Aslan) → #3 modelos (Andujar) → #7 base de datos (Andujar) → #11 auth (Aslan) → Crew API y Mission Control API. Los primeros 4 tickets de Aslan y Andujar marcan el ritmo de todo el equipo.

---

## Aslan — Backend (@AslanGuzman)

1. **#1** Crear la solución .NET 10 y la estructura de proyectos. *El primero de todo el equipo: hazlo ya.*
2. **#12** Heartbeat.Client: cliente tipado de la Crew API, con fake. *(espera #3)* Hazlo mientras Andujar termina la base de datos; Aliandy lo necesita para el login.
3. **#11** Auth base: Identity + JWT con dos audiencias. *(espera #7)* Desbloquea a Montserrat.
4. **#13** Crew API: login, refresh y perfil (/me)
5. **#15** Crew API: POST /sync idempotente (push + pull). *(espera #5)* El ticket más importante del backend.
6. **#16** Crear una alerta por cada check-up crítico al sincronizar. Desbloquea las alertas de Montserrat.
7. **#17** Crew API: GET /checkups (historial)
8. **#14** Crew API: GET /reference-ranges con ETag
9. **#45** Integración: offline → sync → alerta en el portal. *(espera frontends, #10 y #24)*
10. **#54** Smartwatch: vincular la cuenta de Google (OAuth). *Futuro (M5).* *(espera #53)*

## Montserrat — Backend (@MontserratNunez)

1. **#2** Verificar que el plan de GoDaddy soporta .NET 10. *Hora 0, no depende de nadie.*
2. **#18** Mission Control API: login, /me y políticas por permiso. *(espera #11)* Mientras esperas, adelanta el esqueleto de las slides de #47.
3. **#21** MC API: lista de tripulación y cuentas de astronautas. Desbloquea la primera pantalla del portal.
4. **#22** MC API: estado actual e historial de check-ups. Desbloquea el detalle del portal.
5. **#19** MC API: roles y catálogo de permisos
6. **#20** MC API: gestión de usuarios
7. **#23** MC API: listar y reconocer alertas. *(espera #16)*
8. **#46** Desplegar backend y portal (demo). *(espera #15 y #37)*
9. **#47** Pitch: slides, video demo y entrega en Space Apps. *(espera #45 y #46)*

## Andujar — Data → Backend (@andujarsky-lang)

Primero todo lo de data (1–8); después pasas a backend (9–10).

1. **#3** Shared: enums, records y constantes de permisos. *(espera #1)* Bloquea a los 5: es lo más urgente.
2. **#7** EF Core: esquema del servidor, Identity y migraciones. Bloquea toda la auth y las APIs.
3. **#4** Shared: cargar reference-ranges.json como recurso embebido
4. **#5** Shared: implementar IHealthEvaluator. Desbloquea el check-up del escritorio y el sync.
5. **#8** SQLite del escritorio: tablas locales y estado de sync. Desbloquea el check-up y el login del escritorio.
6. **#10** Datos de demo: misión, 3 astronautas, 30 días y usuarios
7. **#6** Pruebas unitarias del evaluador
8. **#9** Preparar el modelo para lecturas de wearables. *Deja listo el smartwatch.*
9. **#24** Mensajes: endpoints del portal y entrega por sync. *(espera #15 y #18)* Primer ticket de backend.
10. **#25** MC API: series por indicador para gráficas. *(espera #22)*
11. **#50** P2 · Gamificación: rachas de check-ups
12. **#51** P2 · Registro de exposición a radiación
13. **#53** Smartwatch: investigar Google Health API / Health Connect. *Futuro.*
14. **#55** Smartwatch: importar lecturas del reloj. *Futuro.* *(espera #54)*
15. **#52** Épica smartwatch: se cierra sola cuando terminen sus sub-issues.

## Aliandy — Frontend escritorio WPF (@AliaJimenez) · repo [HeartbeatFrontDesktop](https://github.com/IsaiasMorillo/HeartbeatFrontDesktop)

Primero todo lo que funciona offline; después lo que necesita la API.

1. **#26** Escritorio: estructura WPF (MVVM, DI, navegación, tema, submódulo, CI). *(espera #1)*
2. **#31** Panel de información y fuentes por indicador. *(espera #4)*
3. **#28** Formulario de check-up con los 6 indicadores. *(espera #5 y #8)*
4. **#29** Pantalla de resultado con guía y escalamiento
5. **#30** Dashboard con el estado actual de los 6 indicadores
6. **#27** Login online y offline. *(espera #12 y #13)*
7. **#34** Ver y editar mi perfil
8. **#32** Motor de sincronización en segundo plano. *(espera #15)*
9. **#33** Mensajes y reportes en cola
10. **#35** Historial y gráficas (LiveCharts2). *P1.*
11. **#48** P2 · Figura humana clicable
12. **#56** Smartwatch: vincular reloj en el escritorio. *Futuro.* *(espera #54)*

## Isaías — Frontend portal Blazor (@IsaiasMorillo) · repo [HeartbeatFrontWeb](https://github.com/IsaiasMorillo/HeartbeatFrontWeb)

Primero el flujo de revisión médica (lo que se ve en la demo); después la administración.

1. **#36** Portal: estructura Blazor, cliente MC API, navegación por permisos, submódulo, CI. *(espera #1 y #3)*
2. **#37** Inicio y cierre de sesión. *(espera #18)*
3. **#38** Vista general de la tripulación. *(espera #21)*
4. **#39** Detalle del astronauta (estado actual + historial). *(espera #22)*
5. **#40** Alertas críticas y reconocimiento. *(espera #23)*
6. **#43** Roles y checklist de permisos. *(espera #19)*
7. **#42** Administración de usuarios. *(espera #20)*
8. **#41** Mensajes por astronauta. *(espera #24)* Va después de roles y usuarios porque su endpoint llega tarde.
9. **#44** Gráficas por indicador (Chart.js). *P1.* *(espera #25)*
10. **#49** P2 · Recordatorios de check-up (en el repo del escritorio)
11. **#57** Smartwatch: origen de lecturas en el portal. *Futuro.* *(espera #55)*

---

## Horas tranquilas al inicio

| Persona | Espera a | Qué hacer mientras tanto |
| --- | --- | --- |
| Montserrat | #11 (≈ hora 11) | #2 hosting, esqueleto de slides, escribir los endpoints de MC contra los contratos con auth simulada |
| Isaías | #1, #3 (≈ hora 4) | Wireframes del portal y paleta de colores |
| Aliandy | #1 (≈ hora 2) | Wireframes del escritorio; repasar CONTRACTS.md y reference-ranges.json |
