# Flujo de tickets y dependencias

Cómo trabajamos los 5 con el GitHub Project **Heartbeat · NASA Space Apps 2026**.

## Equipo

| Persona | Rol | Dueño de |
| --- | --- | --- |
| @AliaJimenez | Frontend | App de escritorio WPF (`area:desktop`) |
| @IsaiasMorillo | Frontend | Portal Blazor (`area:portal`) |
| @AslanGuzman | Backend | Solución, auth, Crew API, sync, integración |
| @MontserratNunez | Backend | Mission Control API, permisos, hosting, despliegue, pitch |
| @andujarsky-lang | Data → Backend | Shared, base de datos, seed. **Al terminar todo lo de `area:data`** pasa a tickets de backend (`MSG-BACKEND`, `MC-SERIES`) |

## Etiquetas

| Etiqueta | Significado |
| --- | --- |
| `listo` | Todas sus dependencias están cerradas: se puede empezar |
| `bloqueado` | Tiene al menos una dependencia abierta: no empezar |
| `area:*` | backend, desktop, portal, data, devops, pitch, wearable |
| `P0`…`P3` | P0 obligatorio (MR) · P1 · P2 · P3 futuro |
| `epica` | Agrupa sub-issues (smartwatch) |

Las etiquetas `listo` / `bloqueado` **las pone el bot**, no a mano.

## Cómo se manejan las dependencias

1. **La fuente de verdad es la relación nativa "Blocked by" de GitHub** (panel derecho del issue → *Relationships* → *Mark as blocked by*). La sección "Dependencias" del cuerpo del ticket es solo una copia legible.
2. El workflow [`dependencias.yml`](../.github/workflows/dependencias.yml) corre cuando un issue se abre, se cierra o se reabre, y cada 30 minutos:
   - Si un ticket tiene dependencias abiertas → etiqueta `bloqueado` (y Status *Bloqueado* en el Project).
   - Cuando se cierra la última dependencia → quita `bloqueado`, pone `listo`, mueve Status a *Listo* y **comenta mencionando al responsable**.
   - Si se reabre una dependencia, los tickets que dependen de ella vuelven a `bloqueado`.
   - Nunca toca un ticket que ya está *En progreso*, *En revisión* o *Hecho*.
3. Para forzar una revisión inmediata: pestaña **Actions → Dependencias de tickets → Run workflow**.

## El día a día

1. En el Project, abre la vista **Mis tickets listos** (o filtra `assignee:@me label:listo`).
2. Toma el de mayor prioridad y muévelo a **En progreso**.
3. Rama: `<clave-o-número>-descripcion-corta`, por ejemplo `13-crew-sync`.
4. PR con `Closes #13` en la descripción. Al hacer merge el issue se cierra → el bot desbloquea lo siguiente y avisa a quien le toca.
5. Revisión: cualquier otra persona del equipo; idealmente quien depende de tu ticket.

## Si estás bloqueado

- **No esperes sentado.** Los contratos en `contracts/CONTRACTS.md` definen todo: programa contra la interfaz con un fake (`Heartbeat.Client` trae uno para el escritorio) y conecta lo real cuando se cierre la dependencia.
- Si una dependencia lleva mucho tiempo abierta, comenta en ese ticket mencionando a su responsable.
- Si descubres una dependencia nueva, agrégala con *Mark as blocked by* (el bot la recoge en ≤ 30 min, o córrelo a mano).
- Si una dependencia ya no aplica, quítala en *Relationships* y explica por qué en un comentario.

## Cambios a los contratos

Cualquier cambio a `contracts/` va en su propio PR, revisado por al menos un backend y un frontend, y sube la versión del archivo.
