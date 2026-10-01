# Backend — Vision Support

API ASP.NET Core (net8.0) que gestiona usuarios, dispositivos, sesiones de soporte y la
señalización WebRTC entre la app Android y el panel web. **No transporta vídeo ni audio.**

## Estructura

- `Models/` — entidades EF Core: `User`, `RefreshToken`, `Device`, `RemoteSession`,
  `SessionEvent`, `PairingCode`, `PairingCodeEvent`, `DeviceHealthSnapshot`,
  `DeviceDiagnosticEvent`.
- `Data/AppDbContext.cs` — contexto EF Core + mapeo.
- `Dtos/` — contratos de request/response de la API REST.
- `Controllers/` — `AuthController`, `UsersController`, `HealthController`, `DevicesController`,
  `PairingCodesController`, `SessionsController`.
- `Hubs/RemoteHub.cs` — hub SignalR de señalización (presencia, solicitud/aceptación de
  sesión, offer/answer/ICE). Mensajes tipados en `Hubs/SignalingMessages.cs`.
- `Authentication/ClaimsPrincipalExtensions.cs` — helpers para leer el usuario autenticado
  desde los claims del JWT.
- `Services/TokenService.cs` + `Options/JwtOptions.cs` — emisión de access/refresh tokens.
- `Services/AdminBootstrapper.cs` — crea el primer Admin al arrancar (ver **Autenticación**).
- `Services/PairingCodeService.cs` — genera/valida códigos de emparejamiento de un solo uso
  (ver **Emparejamiento de dispositivos** más abajo).
- `RateLimiting/RateLimitPolicyNames.cs` — nombre de la política de rate limiting por IP de
  `POST /api/devices/enroll` (configurada en `Program.cs`).
- `Services/TurnCredentialService.cs` + `Options/TurnOptions.cs` + `Dtos/TurnDtos.cs` —
  credenciales TURN efímeras para Coturn (ver sección **TURN/Coturn** más abajo).
- `Options/DiagnosticsOptions.cs` — retención configurable de `DeviceDiagnosticEvent` (ver
  **Diagnóstico de dispositivos** más abajo).
- `Migrations/` — `InitialCreate` + `AddAuthentication` + `AddPairingCodes` +
  `AddDeviceDiagnostics` + `AddApplicationDiagnostics`, generadas y verificadas; se aplican
  solas al arrancar (en todo entorno, no solo Development — ver **Autenticación**). Ver
  `Migrations/README.md` si necesitas generar una nueva.
- `VisionSupport.Server.Tests/` — pruebas xUnit (Hub + API HTTP real). Ver sección **Pruebas**.

## Requisitos

- .NET SDK 8.0+ (no imprescindible: `infra/docker-compose.yml` compila y ejecuta todo dentro
  de contenedores con el SDK/runtime correctos)
- PostgreSQL 16 (vía `infra/docker-compose.yml` o instalación local)

## Ejecutar en local (sin Docker)

```bash
cd server
cp .env.example .env   # solo como referencia; dotnet no lee .env directamente
export INITIAL_ADMIN_EMAIL=admin@example.com   # solo la primera vez (ver más abajo)
export INITIAL_ADMIN_PASSWORD=cambia-esto      # nunca lo subas a git
dotnet restore
dotnet run
```

La API queda en `http://localhost:5080`, con Swagger en `http://localhost:5080/swagger`
(con soporte para probar endpoints protegidos: botón "Authorize", pega el `accessToken` sin
el prefijo `Bearer `). El hub SignalR está en `ws://localhost:5080/hubs/remote`.

La cadena de conexión de desarrollo (`appsettings.Development.json`) apunta a
`localhost:5432` con credenciales de solo-desarrollo (`visionsupport` / `changeme_dev_only`),
coincidentes con las que usa `infra/docker-compose.yml`.

Compilación verificada con el SDK de .NET 8 (`docker compose build server`, 0 errores/0
warnings) porque este entorno no tiene `dotnet` instalado localmente; si lo tienes, `dotnet
build`/`dotnet run` funcionan igual.

## Autenticación (email + contraseña, JWT)

**No hay registro público.** Un Admin crea cuentas vía `POST /api/users`; la primera cuenta
(Admin) la crea el propio backend al arrancar, **solo si la tabla `Users` está vacía**, leyendo
`INITIAL_ADMIN_EMAIL`/`INITIAL_ADMIN_PASSWORD` de variables de entorno
(`Services/AdminBootstrapper.cs`). Si faltan y ya hay al menos un usuario, no hace nada. Si
faltan y la tabla está vacía: en `Production` el arranque **falla explícitamente**; en
`Development` solo deja un `warning` en el log y sigue (para no bloquear `docker compose up`
sin querer crear un Admin todavía). Las migraciones y este bootstrap corren en **todo**
entorno (no solo Development) — si no, el chequeo de "fallar en producción" nunca se activaría.

Roles: `Admin` (gestiona usuarios, ve dispositivos, solicita sesiones) y `Support` (ve
dispositivos, solicita sesiones; no puede crear/editar usuarios).

Endpoints:

| Método | Ruta | Auth | Descripción |
| --- | --- | --- | --- |
| POST | `/api/auth/login` | pública | `{email, password}` → tokens + datos del usuario |
| POST | `/api/auth/refresh` | pública | `{refreshToken}` → nuevo par de tokens; **rotativo** (el usado queda revocado) |
| POST | `/api/auth/logout` | pública | `{refreshToken}` → lo revoca; idempotente, 204 siempre |
| GET | `/api/auth/me` | JWT | Usuario actual |
| GET | `/api/users` | JWT + Admin | Lista de usuarios |
| POST | `/api/users` | JWT + Admin | Crea usuario (`email, displayName, role, password`) |
| PATCH | `/api/users/{id}` | JWT + Admin | Actualiza `displayName`/`role`/`isActive`/`newPassword` (todos opcionales) |

Detalles de seguridad:

- Contraseñas con `Microsoft.AspNetCore.Identity.PasswordHasher<User>` (PBKDF2 + salt); nunca
  se guardan ni se devuelven en texto plano.
- Access token JWT corto (`Jwt:AccessTokenMinutes`, default 15 min). Refresh token opaco de 512
  bits, **solo se guarda su hash SHA-256** en `RefreshTokens`; cada uso lo rota (revoca el viejo,
  emite uno nuevo). Reutilizar un refresh token ya revocado se trata como indicio de robo: se
  revoca **toda** la familia de tokens de ese usuario.
- Desactivar un usuario o restablecerle la contraseña revoca de inmediato sus refresh tokens
  activos.
- El login siempre responde el mismo mensaje genérico exista o no el email, esté activo o no.
- `AuthController`/`UsersController` usan `[Authorize]`/`[Authorize(Roles = "Admin")]`.
  `RemoteHub.JoinSession` es el único método del Hub con `[Authorize]` (los demás los llama
  también Android, que nunca tiene JWT — ver siguiente sección).
- CORS explícito (nunca `AllowAnyOrigin`) + `AllowCredentials`; el JWT viaja como
  `Authorization: Bearer` en REST, y como `?access_token=` en la conexión SignalR (el
  navegador no puede mandar cabeceras propias en el handshake de WebSocket — ver
  `Program.cs`, `JwtBearerEvents.OnMessageReceived`).
- **HTTPS es obligatorio antes de exponer esto fuera de `localhost`** (ver
  `docs/local-development.md`): sin TLS, tanto el access token como el refresh token viajan en
  claro.

**Separado por completo de Android**: los dispositivos se autentican por `deviceCode` +
`pairingKey` (ver más abajo), nunca con JWT ni contraseñas de usuario.

## Pruebas

```bash
cd server
dotnet test VisionSupport.Server.Tests/VisionSupport.Server.Tests.csproj
```

Dos estilos, según qué hace falta verificar:

- **Pruebas del Hub** (`RemoteHubTests.cs`, `RemoteHubTurnCredentialsTests.cs`,
  `RemoteHubJoinSessionTests.cs`): instancian `RemoteHub` directamente con
  `Context`/`Clients`/`Groups` sustituidos y SQLite en memoria — el patrón recomendado por
  Microsoft para probar hubs sin levantar un servidor real. Cubren pairing key,
  reconexión/"zombie", `GetTurnCredentials`, y `JoinSession` con un `ClaimsPrincipal` con la
  misma forma que emite `TokenService`.
- **Pruebas de API por HTTP real** (`AuthApiTests.cs`, vía `TestApiFactory.cs`): un
  `TestServer` real con el mismo pipeline de autenticación/autorización que `Program.cs`
  (JwtBearer, `[Authorize(Roles=...)]`, controladores reales) — necesario porque instanciar un
  controlador directamente NO evalúa esos atributos. Cubren login válido/inválido (con mensaje
  idéntico para email inexistente vs. contraseña incorrecta), usuario inactivo, refresh
  rotativo, reutilización de un refresh token revocado (revoca toda la familia), logout,
  **rol `Support` recibiendo 403 al intentar crear un usuario**, creación como Admin (nunca
  devuelve `passwordHash`), email duplicado, rol inválido, y desactivación revocando refresh
  tokens.
- **`TurnCredentialServiceTests.cs`**: formato del `username`, HMAC, TTL/recorte a 10 min, URLs.
- **`PairingCodesApiTests.cs`**: solo Admin crea/lista/revoca (Support recibe 403), el código en
  texto plano solo se devuelve al crear (nunca en el listado), y un código ya usado no se puede
  revocar (`Conflict`).
- **`DevicesEnrollApiTests.cs`**: código válido (con normalización de mayúsculas/guiones),
  inválido, vencido, revocado, reutilizado, reemparejamiento del mismo `deviceCode` (rota
  `pairingKey` en vez de duplicar), **intento concurrente con el mismo código (solo una de N
  peticiones simultáneas tiene éxito)**, y límite de tasa por IP (429 tras varios intentos). Usa
  su propia instancia de `TestApiFactory` por test (no `IClassFixture` compartido): el rate
  limiter es estado compartido dentro de un mismo host de prueba, y varias pruebas de enroll en
  la misma ventana de 1 minuto se pisarían entre sí si compartieran factory.
- **`RemoteHubDiagnosticsTests.cs`** y **`DeviceDiagnosticsApiTests.cs`**: ver "Pruebas de
  autorización" en la sección **Diagnóstico de dispositivos** más abajo.

Si tienes el SDK de .NET 8 instalado localmente, el comando de arriba funciona igual; si no,
ejecútalo dentro de un contenedor:

```bash
docker run --rm -v "$(pwd):/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test VisionSupport.Server.Tests/VisionSupport.Server.Tests.csproj
```

## Autenticación de dispositivos (Android)

Completamente separada de la de arriba — Android nunca usa JWT ni tiene contraseña:

- `POST /api/devices/enroll` (ver **Emparejamiento de dispositivos** más abajo) devuelve un
  `pairingKey` (una sola vez) que Android debe presentar en
  `RemoteHub.AnnounceDevicePresence`. Sin la clave correcta, el hub rechaza la conexión como ese
  dispositivo — así ningún tercero puede suplantarlo solo conociendo su `deviceCode` (que no es
  secreto). A diferencia del registro abierto anterior, `enroll` exige además un código de
  emparejamiento de un solo uso generado por un Admin: no hay alta de dispositivos sin que un
  humano autorizado lo apruebe primero.
- El hub verifica en cada paso que quien invoca un método es quien dice ser (dispositivo dueño
  de la sesión, técnico dueño de la sesión) y que la sesión está en un estado válido para esa
  acción — ver la tabla de autorización en `docs/signaling-protocol.md`.

## TURN/Coturn

`TurnCredentialService` genera credenciales efímeras (máximo 10 minutos) compatibles con el
mecanismo "TURN REST API" que Coturn entiende con `--use-auth-secret`:
`username = "{expiración-unix}:{etiqueta}"`, `credential = Base64(HMAC-SHA1(secreto, username))`.
Se configuran vía la sección `Turn` (env vars `Turn__SharedSecret`, `Turn__Realm`,
`Turn__PublicHost`, `Turn__Port`, `Turn__CredentialTtlSeconds` — ver `infra/.env.example`).

Se exponen **solo** por SignalR: `RemoteHub.GetTurnCredentials(sessionId)`, reutilizando
exactamente `AuthorizeSessionParticipantAsync` (la misma autorización que
`SendOffer`/`SendAnswer`/`SendIceCandidate`) — no hay endpoint REST equivalente ni forma de
obtenerlas sin ser el dispositivo o el técnico ya autorizados de una sesión `Accepted`/`Active`.
Ni el secreto ni la credencial calculada se registran en logs.

Ver `infra/coturn/README.md` para cómo activar/configurar Coturn (LAN local o VPS) y
`docs/architecture.md` para el detalle del flujo completo.

## Endpoints

| Método | Ruta | Auth | Descripción |
| --- | --- | --- | --- |
| GET | `/api/health` | pública | Estado del servicio |
| GET | `/api/devices` | JWT | Lista de dispositivos y su estado |
| POST | `/api/devices/enroll` | pública (Android), limitada por IP | Alta/reemparejamiento de un dispositivo mediante un código de emparejamiento de un solo uso; devuelve `pairingKey` (solo aquí). Ver "Emparejamiento de dispositivos" abajo — ya no existe registro abierto/automático |
| GET | `/api/devices/{id}/diagnostics` | JWT | Último snapshot de diagnóstico del dispositivo; `204` si todavía no reportó ninguno, `404` si el dispositivo no existe |
| GET | `/api/devices/{id}/diagnostics/events` | JWT | Histórico de eventos de diagnóstico, más recientes primero (hasta 200 por dispositivo, ver retención) |
| GET | `/api/pairing-codes` | JWT (Admin) | Lista códigos de emparejamiento (activos/usados/revocados/vencidos), sin exponer el valor del código |
| POST | `/api/pairing-codes` | JWT (Admin) | Genera un código de emparejamiento de un solo uso, vigente 10 minutos; el valor en texto plano solo se devuelve aquí |
| POST | `/api/pairing-codes/{id}/revoke` | JWT (Admin) | Revoca un código todavía no usado |
| POST | `/api/sessions/request` | JWT | El técnico/admin solicita sesión con un dispositivo |
| POST | `/api/sessions/{id}/end` | JWT | Finaliza una sesión |

Ver también la tabla de `/api/auth` y `/api/users` más arriba, y
`docs/signaling-protocol.md` para los eventos del hub `RemoteHub`.

## Emparejamiento de dispositivos

No existe alta automática de dispositivos: `POST /api/devices/register` (registro abierto) se
eliminó. El único punto de entrada es `POST /api/devices/enroll`, que exige un código de
emparejamiento de un solo uso creado por un Admin vía `POST /api/pairing-codes`.

- **`PairingCode`** (`Models/PairingCode.cs`): guarda solo el **hash** SHA-256 del código (nunca
  el valor en texto plano), quién lo creó, cuándo vence (`ExpiresAt = CreatedAt + 10 min`,
  `PairingCodeService.Lifetime`), y cuándo se usó/revocó. `DeviceId` queda vacío hasta que el
  código se usa con éxito.
- **Generación** (`PairingCodeService.GenerateCode`): 10 caracteres de un alfabeto de 32 símbolos
  sin ambigüedades (sin `0`/`O` ni `1`/`I`), aleatorio criptográfico (`RandomNumberGenerator`),
  ~50 bits de entropía. El panel lo muestra formateado como `ABCDE-FGHJK`; el guion es solo
  visual — `PairingCodeService.Normalize` lo ignora al validar, igual que mayúsculas/espacios.
- **`POST /api/devices/enroll`** valida, en este orden: el hash coincide con un código existente
  → no está revocado → no está usado → no está vencido. Si todo eso pasa, reclama el código
  atómicamente con una actualización condicional (`UPDATE ... WHERE UsedAt IS NULL AND
  RevokedAt IS NULL`) — así, si dos peticiones llegan a la vez con el mismo código (o el mismo
  código se reutiliza), solo una tiene éxito; la otra ve la fila ya actualizada y falla con
  `"used"`. Si `DeviceCode` ya existe (reemparejar un dispositivo de desarrollo), rota su
  `PairingKey` en vez de duplicar el dispositivo.
- **Errores** (`EnrollErrorResponse.ErrorCode`): `"invalid"` | `"expired"` | `"used"` |
  `"revoked"`, para que Android muestre un mensaje específico sin exponer detalles internos.
- **Rate limiting**: `POST /api/devices/enroll` está limitado a 5 peticiones por minuto por IP
  (`Program.cs`, `AddRateLimiter`/`RateLimitPolicyNames.PairingEnroll`) — dificulta adivinar
  códigos por fuerza bruta incluso si alguien automatiza intentos.
- **Auditoría** (`PairingCodeEvent`): cada creación, uso, revocación e intento fallido
  (`AttemptInvalidCode`/`AttemptExpiredCode`/`AttemptUsedCode`/`AttemptRevokedCode`) queda
  registrado con IP y marca de tiempo — nunca con el código en texto plano.

### Reemparejar un dispositivo de desarrollo existente

Los dispositivos ya emparejados antes de este cambio siguen funcionando sin tocar nada: su
`pairingKey` ya emitido sigue siendo válido para `RemoteHub.AnnounceDevicePresence`. Si
necesitas volver a emparejar uno (por ejemplo, tras reinstalar la app o "Desvincular
dispositivo" en Android), genera un código nuevo (panel o `POST /api/pairing-codes`) y vuelve a
llamar a `POST /api/devices/enroll` con el mismo `deviceCode` de siempre: el backend detecta que
ya existe y le asigna un `pairingKey` nuevo en vez de crear un dispositivo duplicado.

## Diagnóstico de dispositivos

Estado actual e historial básico de cada Android emparejado (batería, red, almacenamiento,
identificación de SO/app, errores propios), sin Logcat ni datos de otras apps — ver también
`docs/architecture.md`.

- **`DeviceHealthSnapshot`** (`Models/DeviceHealthSnapshot.cs`): el último estado reportado, una
  fila por `Device` (índice único en `DeviceId`) — cada snapshot nuevo lo sobrescribe, nunca
  crece. Android lo envía al conectar y cada 60 segundos mientras sigue conectado.
- **`DeviceDiagnosticEvent`** (`Models/DeviceDiagnosticEvent.cs`): histórico append-only. Siete
  tipos: tres propios del agente (`NetworkChanged`, `BatteryCritical`, `AgentError`) y cuatro de
  una app propia integrada vía el SDK `VisionDiagnostics`
  (`ApplicationInfo`/`ApplicationWarning`/`ApplicationError`/`ApplicationException` — ver
  `android/vision-diagnostics-sdk/README.md`). Para estos últimos cuatro, `SourcePackage` es
  obligatorio (`RemoteHub.ReportDiagnosticEvent` rechaza el evento si falta) y viene ya verificado
  por Android del lado del agente (`DiagnosticsProvider`, UID real → PackageManager) — el backend
  no vuelve a verificarlo porque no tiene forma de hacerlo desde el Hub, pero confía en que llegó
  ya correcto por ese camino. También se guardan `SourceAppVersion`, `Code` (código corto
  definido por la app integrada) y `OccurredAt` (instante real según el reloj del dispositivo
  fuente, que puede preceder a `CreatedAt` si el evento vino de una cola offline). **Retención
  configurable** (`Options/DiagnosticsOptions.cs`, variable de entorno
  `Diagnostics__MaxEventsPerDevice`, 200 por defecto): `RemoteHub.PruneOldDiagnosticEventsAsync`
  poda los más antiguos en cada escritura nueva, sin necesitar un job de limpieza aparte.
- **Escritura, solo por SignalR** (`RemoteHub.ReportDiagnosticsSnapshot`/`ReportDiagnosticEvent`):
  nunca hay un endpoint REST para reportar. Ambos métodos resuelven el `Device` a partir de
  `Context.ConnectionId` (la misma prueba de identidad que `RespondToSession`/`SendOffer`/etc,
  fijada por `AnnounceDevicePresence`) — **nunca** de un `deviceId` que el propio mensaje llevara,
  así que estructuralmente ninguna conexión puede reportar "en nombre de" otro dispositivo. Una
  conexión que nunca anunció presencia recibe `HubException`.
- **Lectura, por REST con JWT** (`GET /api/devices/{id}/diagnostics[/events]`, en
  `DevicesController`): igual que `GET /api/devices` — cualquier técnico o Admin autenticado ve
  el diagnóstico de cualquier dispositivo; no existe un concepto de "dispositivo asignado a un
  técnico" en este producto. `204` (no `404`) si el dispositivo existe pero todavía no reportó
  nada — no es un error, es un estado vacío legítimo.
- **Nunca se envía ni se persiste**: contraseñas, `pairingKey`, JWT, contenido de pantalla,
  contactos, ubicación, ni Logcat/logs de otras apps — ni de Vision Support ni de la app
  integrada (el SDK `VisionDiagnostics` nunca tiene acceso al `pairingKey`).
- **SDK `VisionDiagnostics`** (`android/vision-diagnostics-sdk/`): ya implementado — ver ese
  README para la guía de integración completa, y `docs/architecture.md` para el diagrama de la
  arquitectura completa (ContentProvider `signature` → cola persistente → Hub).

### Pruebas de autorización

- **`RemoteHubDiagnosticsTests.cs`**: una conexión que nunca llamó `AnnounceDevicePresence` no
  puede reportar (`HubException`); con dos dispositivos conectados a la vez, el evento reportado
  por uno nunca aparece bajo el otro; un snapshot repetido actualiza la misma fila (no crea
  duplicados); un evento `Application*` sin `SourcePackage` se rechaza; un tipo propio del agente
  ignora cualquier `SourcePackage` que llegara (defensa en profundidad); la retención poda
  correctamente los eventos más antiguos más allá del límite, y ese límite es configurable
  (probado con un valor distinto al de por defecto).
- **`DeviceDiagnosticsApiTests.cs`**: sin token → 401; dispositivo inexistente → 404; dispositivo
  sin snapshot todavía → 204; cualquier técnico o Admin autenticado puede leer el diagnóstico de
  cualquier dispositivo; los eventos se listan del más reciente al más antiguo; los campos
  `SourcePackage`/`SourceAppVersion`/`Code`/`OccurredAt` se sirven correctamente.
