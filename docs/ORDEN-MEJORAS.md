# Heartbeat — Orden de trabajo de las mejoras

Orden sugerido para cada persona. El criterio es hacer primero lo que desbloquea a otros (ruta crítica), luego P0, después P1 y P2 solo si sobra tiempo.

- Los números son issues de [AliaJimenez/HeartbeatBackend](https://github.com/AliaJimenez/HeartbeatBackend/issues). El plan completo está en [MEJORAS.md](MEJORAS.md).
- *(espera #n)* indica que ese ticket depende de otro: revisa en *Relationships* que esté cerrado. Mientras tanto, programa contra el contrato de #95 HB-00 con un mock.
- **Feature freeze 1 h antes del cierre.** Lo que no esté en `dev` a esa hora no entra.

**Ruta crítica:** #95 HB-00 contrato (Aslan) → #96 HB-DA-01 catálogo (Andujar) → #97 HB-BE-01 dominio (Aslan) → #99 HB-DA-03 radiación (Andujar) + #101 HB-BE-03 ambiente (Montserrat) → #100 HB-BE-02 → **#103 HB-BE-07 semáforo (Montserrat)** → #104 HB-FE-01 / #108 HB-FE-05 → #126 HB-QA-01.

---

## Aslan — BE1 (@AslanGuzman)

1. **#95 HB-00** Contrato v0.2 (DTOs de entorno, ambiente, radiación v2, semáforo, phantoms). *P0 · 1 h. Hazlo ya: bloquea a los 5.*
2. **#97 HB-BE-01** Dominio y migración (perfil de entorno, `EnvironmentReading`, tipo de radiación). *P0 · 2 h* *(espera #96 HB-DA-01)* Mientras esperas, deja preparadas las entidades.
3. **#102 HB-BE-04** Perfil de entorno de la misión y retardo de comunicación. *P0 · 1.5 h.* Desbloquea a Isaías.
4. **#115 HB-BE-09** Retardo simulado en el enlace del escritorio (repo HeartbeatFrontDesktop). *P1 · 1.5 h*
5. **#111 HB-BE-06** API de aislamiento y confinamiento. *P1 · 1 h* *(espera #110 HB-DA-05)*
6. **#126 HB-QA-01** E2E: SPE + CO₂ alto + privación de sueño. *P0 · 2 h · lideras, participa todo el equipo* *(espera #103 HB-BE-07, #104 HB-FE-01, #108 HB-FE-05, #98 HB-DA-02)*
7. **#120 HB-BE-11** Perfiles comerciales. *P2 · 2 h · solo si sobra tiempo*

## Montserrat — BE2 (@MontserratNunez)

1. **#101 HB-BE-03** API de ambiente con alertas por umbral. *P0 · 2 h* *(espera #97 HB-BE-01)* Mientras esperas, adelanta el esqueleto del motor de reglas de #103 HB-BE-07 en Shared contra el contrato.
2. **#100 HB-BE-02** API de radiación v2 y alerta SPE. *P0 · 1.5 h* *(espera #99 HB-DA-03)*
3. **#103 HB-BE-07** **Motor de reglas y semáforo unificado.** *P0 · 3 h.* Es el ticket más importante: lo esperan Aliandy, Isaías y el E2E.
4. **#113 HB-BE-05** API de presión ocular / visión. *P1 · 1.5 h*
5. **#47** Pitch: guion de 3 min, historia comercial, capturas. *P0 · 2 h · lideras* *(espera #126 HB-QA-01)* Puedes adelantar el guion y la historia comercial en cualquier hueco.
6. **#117 HB-BE-08** API de comparación de phantoms. *P1 · 1.5 h* *(espera #116 HB-DA-06)* Es lo primero que se recorta.
7. **#122 HB-BE-10** Exportar CSV. *P2 · 1 h*

## Andujar — DA1 (@andujarsky-lang)

1. **#96 HB-DA-01** Catálogo de indicadores con `unit`, `hazard`, `source`, `status`. *P0 · 2 h* *(espera #95 HB-00)* El segundo eslabón de la ruta crítica.
2. **#99 HB-DA-03** Modelo de radiación (tasa, tipo, % de 600 mSv y 250 mSv/SPE). *P0 · 2 h.* Desbloquea la radiación de Montserrat y Aliandy.
3. **#98 HB-DA-02** Generador simulado con semilla (SPE, pico de CO₂, privación de sueño). *P0 · 2 h.* Lo necesitan el E2E y la demo.
4. **#110 HB-DA-05** Índices de aislamiento y confinamiento. *P1 · 2 h.* Desbloquea #111 HB-BE-06.
5. **#119 HB-DA-08** Data card. *P1 · 1 h.* Muy valorada por los jueces.
6. **#112 HB-DA-04** Línea base personal + desviaciones (z-score/EWMA). *P1 · 2 h*
7. **#116 HB-DA-06** Phantoms. *P1 · 2 h.* Solo si la cadena de phantoms sigue viva: es lo primero que se recorta.
8. **#125 HB-DA-07** Validar rangos con OSDR/LSDA. *P2 · 3 h*

## Aliandy — FE1, escritorio WPF (@AliaJimenez) · repo [HeartbeatFrontDesktop](https://github.com/IsaiasMorillo/HeartbeatFrontDesktop)

1. **#104 HB-FE-01** Modo simple con semáforo por peligro, onboarding y glosario. *P0 · 3 h.* Empieza en cuanto esté #95 HB-00, con un mock; conéctalo a #103 HB-BE-07 al final.
2. **#105 HB-FE-02** Radiación v2 en `RadiationView`. *P0 · 2 h* *(espera #100 HB-BE-02)*
3. **#114 HB-FE-04** Presión ocular y visión con nota SANS. *P1 · 2 h* *(espera #113 HB-BE-05)*
4. Después: apoyar #126 HB-QA-01 y sacar las capturas del escritorio para #47.

## Isaías — FE2, portal + panel de ambiente del escritorio (@IsaiasMorillo) · repos [HeartbeatFrontWeb](https://github.com/IsaiasMorillo/HeartbeatFrontWeb) y [HeartbeatFrontDesktop](https://github.com/IsaiasMorillo/HeartbeatFrontDesktop)

1. **#109 HB-FE-11** «Datos simulados» en el portal + aviso en los README. *P0 · 0.5 h.* Sin dependencias: hazlo ya.
2. **#108 HB-FE-05** Vista de misión del portal. *P0 · 3 h* *(espera #102 HB-BE-04 y #103 HB-BE-07)* Empieza con un mock tras #95 HB-00.
3. **#106 HB-FE-03** Panel de ambiente y gravedad (en el **escritorio**). *P0 · 2.5 h* *(espera #101 HB-BE-03 y #102 HB-BE-04)*
4. **#118 HB-FE-06** Comparador de phantoms. *P1 · 2.5 h* *(espera #117 HB-BE-08)* Se recorta si cae la cadena de phantoms.
5. **#124 HB-FE-09** i18n ES/EN y accesibilidad. *P2 · 2 h*
6. **#121 HB-FE-07** Selector de perfil / vista comercial. *P2 · 2 h* *(espera #120 HB-BE-11)*
7. **#123 HB-FE-10** Botón de exportar CSV. *P2 · 0.5 h* *(espera #122 HB-BE-10)*

---

## Si se acaba el tiempo, se recorta en este orden

1. Todos los P2.
2. Cadena de phantoms: #116 HB-DA-06 → #117 HB-BE-08 → #118 HB-FE-06.
3. #112 HB-DA-04 línea base.
4. Cadena SANS: #113 HB-BE-05 → #114 HB-FE-04.
5. #115 HB-BE-09 retardo simulado.

**No se recortan:** #103 HB-BE-07 semáforo, #104 HB-FE-01 modo simple, #108 HB-FE-05 vista de misión, radiación v2 (#99 HB-DA-03, #100 HB-BE-02, #105 HB-FE-02) y «Datos simulados» (#109 HB-FE-11).
