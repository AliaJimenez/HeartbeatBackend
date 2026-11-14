# Demo y despliegue

**Decisión actual:** la presentación se muestra **en local, directamente en pantalla**; no se sube a GoDaddy.
La API queda lista para desplegarse más adelante en cualquier host (IIS, contenedor o similar) sin cambios de código.
La decisión de base de datos sigue siendo **SQLite** (un solo archivo, sin servidor aparte).

## 1. Demo local (presentación)

```bash
dotnet dev-certs https --trust                                 # una sola vez: certificado HTTPS local
dotnet run --project src/Heartbeat.Api --launch-profile demo   # https://localhost:7017, con datos de demo
```

- El perfil `demo` corre en `Development` con `Seed__Enabled=true`: crea la base (`src/Heartbeat.Api/heartbeat.db`),
  aplica las migraciones y carga los datos de demo una sola vez (es idempotente; reiniciar no duplica nada).
  El perfil `https` arranca la API sin datos.
- La clave JWT de desarrollo ya está en `appsettings.Development.json` (es pública a propósito y la API **no** la
  acepta en `Production`).
- Las 30 jornadas terminan "hoy" (la hora del seed), así que las gráficas siempre se ven actuales. Para renovarlas,
  borra `heartbeat.db` (y `-shm`/`-wal`) con la API detenida y vuelve a arrancar.

### Cuentas de demo

Las contraseñas están **solo** en `appsettings.Development.json` (sección `Seed:Passwords`); el seed nunca usa una
contraseña por defecto y se detiene si falta alguna.

| Cuenta | API | Rol / historia |
| --- | --- | --- |
| `admin` | portal | Administrator |
| `j.okafor` | portal | Flight Surgeon (reconoce alertas) |
| `m.chen` | portal | Mission Control Operator (solo lectura y mensajes) |
| `a.rivera` "Comet" | escritorio | Sana 29 días; el día 30 pico de presión crítico → alerta abierta + reporte sin leer |
| `k.sato` "Atlas" | escritorio | El sueño baja de 7 h a 4.5 h en dos semanas y el ánimo a 2 → tendencia de vigilancia |
| `l.haddad` "Nova" | escritorio | Fiebre los días 18–20 (38.4 → 39.2 °C), luego se recupera → alerta reconocida |

Todas las lecturas se evalúan con el evaluador compartido (`HealthEvaluator`); ningún estado está escrito a mano.

- Portal y escritorio apuntan a `https://localhost:7017/` por su propia configuración (no por código):
  - Portal (`HeartbeatFrontWeb`): la `BaseAddress` del `HttpClient` de la MC API.
  - Escritorio (`HeartbeatFrontDesktop`): la `BaseAddress` del `HttpClient` que recibe `CrewApiClient`.
- Comprobación rápida: `curl -k https://localhost:7017/health` → `{"status":"ok","rangesVersion":"..."}`.
- Para repetir la demo desde cero, borra `heartbeat.db` (y `-shm`/`-wal`) con la API detenida.

## 2. Despliegue futuro (cualquier host)

Lo que ya está resuelto en el código:

| Requisito | Cómo está resuelto |
| --- | --- |
| Secretos fuera del repo | `Jwt:Key` no está en `appsettings.json`. Fuera de `Development` la API **no arranca** sin `Jwt__Key` (32+ caracteres) ni con la clave pública de desarrollo |
| Cadena de conexión | `appsettings.Production.json` solo trae la ruta `App_Data/heartbeat.db`; se sobrescribe con `ConnectionStrings__DefaultConnection`. Una ruta relativa se resuelve desde la carpeta de la app y la carpeta se crea sola |
| HTTPS | La API redirige a HTTPS y envía HSTS fuera de `Development`. El certificado lo pone el host |
| Seed en el entorno | `Seed__Enabled=true` ejecuta los `IDataSeeder` registrados (#10) una vez al arrancar, tras las migraciones. Deben ser idempotentes |
| Comprobar que vive | `GET /health` anónimo; verifica la base de datos |
| Paquete listo | Cada push a `dev` genera el artefacto `heartbeat-api` en GitHub Actions (job `publish-api`); el `Dockerfile` construye la imagen |

### Variables de entorno

Se definen en el host, nunca en el repo. El doble guion bajo equivale a `:`.

| Variable | Obligatoria | Valor |
| --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | sí | `Production` (la imagen Docker ya lo trae) |
| `Jwt__Key` | sí | clave aleatoria de 32+ caracteres |
| `Jwt__Issuer` | no | por defecto `Heartbeat` |
| `ConnectionStrings__DefaultConnection` | no | por defecto `Data Source=App_Data/heartbeat.db` |
| `Seed__Enabled` | solo la primera vez | `true`, y luego `false` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | si un proxy termina HTTPS | `true` (la imagen Docker ya lo trae) |

Generar una clave (PowerShell): `[Convert]::ToBase64String((1..48 | % { Get-Random -Max 256 }) -as [byte[]])`

### Opción A: contenedor

```bash
docker build -t heartbeat-api .
docker run -p 8080:8080 -v heartbeat-data:/app/App_Data -e Jwt__Key=<clave> heartbeat-api
```

El volumen conserva `heartbeat.db` entre redespliegues. La imagen no contiene secretos ni base de datos.

### Opción B: IIS / hosting Windows

1. `dotnet publish src/Heartbeat.Api/Heartbeat.Api.csproj -c Release -o publish/api` (o descarga el artefacto de CI).
   El SDK Web incluye `web.config` para hosting in-process.
2. Sube el contenido al sitio. **No sobrescribas `App_Data/`**: ahí vive la base.
3. Define las variables de entorno en el panel, activa HTTPS y la redirección HTTP→HTTPS.

### Después de desplegar

```bash
curl https://<dominio>/health
curl -X POST https://<dominio>/api/mc/v1/auth/login -H "Content-Type: application/json" \
     -d '{"userName":"admin","password":"<clave de demo>","deviceId":"smoke"}'
```

Y apuntar el portal y el escritorio a la nueva URL en su configuración.

## Estado de #46

- [x] Código listo: secretos y cadena de conexión por entorno, HTTPS/HSTS, seed por configuración, `/health`, CI y Docker
- [x] Seed de demo (#10)
- [ ] Despliegue real: **aplazado**; la demo se presenta en local
