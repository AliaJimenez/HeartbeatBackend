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

## Repos

| Repo | Contenido |
| --- | --- |
| [`AliaJimenez/HeartbeatBackend`](https://github.com/AliaJimenez/HeartbeatBackend) | API, `Heartbeat.Shared`, `Heartbeat.Client`, contratos y **todos los tickets** |
| [`IsaiasMorillo/HeartbeatFrontDesktop`](https://github.com/IsaiasMorillo/HeartbeatFrontDesktop) | App de escritorio WPF de los astronautas (@AliaJimenez) |
| [`IsaiasMorillo/HeartbeatFrontWeb`](https://github.com/IsaiasMorillo/HeartbeatFrontWeb) | Portal Blazor de control de misión (@IsaiasMorillo) |

**Todos los tickets viven en `HeartbeatBackend`**, también los de frontend. Así el Project y el bot de dependencias funcionan desde un solo lugar.

- Desde un PR en otro repo, el ticket se cierra con la referencia completa: `Closes AliaJimenez/HeartbeatBackend#27`. El ticket se cierra cuando el PR se fusiona en `dev`, que es la rama por defecto de los 3 repos.
- Para enlazar commits sin cerrar: `Refs AliaJimenez/HeartbeatBackend#27`.

### Código compartido (submódulo)

Los frontends usan `Heartbeat.Shared` (y el escritorio también `Heartbeat.Client`) como submódulo:

```bash
git submodule add https://github.com/AliaJimenez/HeartbeatBackend.git external/HeartbeatBackend
```

```xml
<ProjectReference Include="..\..\external\HeartbeatBackend\src\Heartbeat.Shared\Heartbeat.Shared.csproj" />
```

- Clonar con submódulos: `git clone --recurse-submodules <url>`. Si ya clonaste, ejecuta `git submodule update --init`.
- Para traer cambios nuevos de Shared: `git submodule update --remote external/HeartbeatBackend` y haz commit del puntero actualizado.
- En GitHub Actions: `actions/checkout` con `submodules: recursive`.
- **No edites Shared desde el submódulo.** Los cambios a Shared van como PR en `HeartbeatBackend`.

## Ramas: todo va a `dev`, nunca a `main`

| Rama | Regla |
| --- | --- |
| `dev` | **Rama por defecto** de los 3 repos. Solo se cambia por PR: no se puede hacer push directo, ni borrarla, ni hacer force-push. Aquí corre el CI. |
| `main` | **No se toca.** Está bloqueada para todos; solo la dueña del repo puede actualizarla, y únicamente para una entrega o release. |
| `<n>-descripcion` | Tu rama de trabajo. Sale de `dev` y vuelve a `dev` por PR. |

- `dev` es la rama por defecto porque GitHub solo cierra issues con `Closes …` al fusionar en la rama por defecto. Así cada merge a `dev` cierra el ticket y el bot desbloquea lo siguiente.
- Los workflows de CI usan:
  ```yaml
  on:
    pull_request:
      branches: [dev]
    push:
      branches: [dev]
  ```
- Al crear el PR, comprueba que la base sea **`dev`** (es la opción por defecto).

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
3. Actualiza `dev` (`git switch dev && git pull`) y crea tu rama desde ahí: `<número>-descripcion-corta`, por ejemplo `13-crew-sync`.
4. Abre el PR **hacia `dev`** con `Closes #13` en la descripción. Desde los repos de frontend usa `Closes AliaJimenez/HeartbeatBackend#13`. Al fusionarlo, el issue se cierra y el bot desbloquea lo siguiente y avisa a quien le toca.
5. Revisión: cualquier otra persona del equipo; idealmente quien depende de tu ticket.

## Si estás bloqueado

- **No esperes sentado.** Los contratos en `contracts/CONTRACTS.md` definen todo: programa contra la interfaz con un fake (`Heartbeat.Client` trae uno para el escritorio) y conecta lo real cuando se cierre la dependencia.
- Si una dependencia lleva mucho tiempo abierta, comenta en ese ticket mencionando a su responsable.
- Si descubres una dependencia nueva, agrégala con *Mark as blocked by* (el bot la recoge en ≤ 30 min, o córrelo a mano).
- Si una dependencia ya no aplica, quítala en *Relationships* y explica por qué en un comentario.

## Cambios a los contratos

Cualquier cambio a `contracts/` va en su propio PR, revisado por al menos un backend y un frontend, y sube la versión del archivo.
