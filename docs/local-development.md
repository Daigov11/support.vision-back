# Desarrollo local — Vision Support

## Opción A: todo con Docker Compose (recomendado)

```bash
cd infra
cp .env.example .env
```

Antes de arrancar, define el primer Admin en `infra/.env` (ver
[Autenticación y primer Admin](#autenticación-y-primer-admin) más abajo):

```bash
# infra/.env
INITIAL_ADMIN_EMAIL=admin@example.com
INITIAL_ADMIN_PASSWORD=<una contraseña que definas tú, mínimo 8 caracteres>
```

```bash
docker compose up --build
```

- Backend: http://localhost:5080 (Swagger en `/swagger`)
- Panel web: http://localhost:5173
- PostgreSQL: localhost:5432

La migración inicial y `AddAuthentication` (`server/Migrations/`) ya están generadas y se
aplican automáticamente al arrancar el backend, en **todos** los entornos
(`db.Database.Migrate()` en `Program.cs`) — no hace falta ningún paso manual para tener las
tablas en una base nueva. Verificado end-to-end: `docker compose up --build` desde cero crea
`Users`, `RefreshTokens`, `Devices`, `RemoteSessions` y `SessionEvents` en PostgreSQL. Si
cambias las entidades más adelante, ver `server/Migrations/README.md` para generar una nueva
migración.

## Opción B: cada módulo por separado

### 1. PostgreSQL

```bash
cd infra
cp .env.example .env
docker compose up postgres
```

### 2. Backend

Requiere .NET SDK 8.0+.

```bash
cd server
dotnet restore
dotnet run
```

Por defecto usa `appsettings.Development.json`, que apunta a
`localhost:5432` con las mismas credenciales de desarrollo que `infra/.env.example`.

### 3. Panel web

Requiere Node.js 20+.

```bash
cd web
cp .env.example .env
npm install
npm run dev
```

Se sirve en http://localhost:5173. Si el backend no está disponible, el panel usa
datos mock automáticamente (ver aviso en la parte superior de la pantalla).

## Autenticación y primer Admin

No existe registro público: la única forma de tener el primer usuario es el **bootstrap
seguro** que corre una sola vez, al arrancar el backend, solo si la tabla `Users` está
vacía. Lee `INITIAL_ADMIN_EMAIL`/`INITIAL_ADMIN_PASSWORD` de variables de entorno y nunca
guarda ni registra la contraseña en texto plano (solo su hash, vía
`PasswordHasher<User>` de ASP.NET).

Variables de entorno relevantes (ver `infra/.env.example`, sin valores reales):

```bash
# Bootstrap del primer Admin — solo se usa si Users está vacía. Déjalas vacías/comentadas
# para no crear ningún usuario por defecto.
INITIAL_ADMIN_EMAIL=admin@example.com
INITIAL_ADMIN_PASSWORD=<defínela tú, nunca la subas a git>

# JWT — genera tu propia clave con `openssl rand -base64 48`, nunca uses el valor de ejemplo
# fuera de tu máquina.
JWT_SIGNING_KEY=changeme_dev_only_jwt_signing_key_do_not_use_in_prod_32bytes_min
JWT_ISSUER=vision-support-dev
JWT_AUDIENCE=vision-support-clients
JWT_ACCESS_TOKEN_MINUTES=15
JWT_REFRESH_TOKEN_DAYS=14
```

Comportamiento del bootstrap (`server/Services/AdminBootstrapper.cs`):

- Si `Users` ya tiene al menos una fila, no hace nada (no vuelve a crear ni a tocar ningún
  usuario), sin importar qué contengan `INITIAL_ADMIN_EMAIL`/`INITIAL_ADMIN_PASSWORD`.
- Si `Users` está vacía y las variables faltan o la contraseña es demasiado corta: en
  `Production` **falla el arranque** (excepción clara, el backend no queda escuchando sin
  autenticación utilizable); en `Development` solo registra una advertencia y sigue sin
  crear ningún Admin (tendrás que definir las variables y reiniciar).
- Si `Users` está vacía y las variables son válidas: crea un único usuario `Admin` con esas
  credenciales.

Para crear tu primer Admin local:

```bash
cd infra
cp .env.example .env
# edita infra/.env y define INITIAL_ADMIN_EMAIL / INITIAL_ADMIN_PASSWORD
docker compose up --build
```

Luego, desde el panel web (http://localhost:5173) inicia sesión con esas credenciales, o por
REST:

```bash
curl -X POST http://localhost:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@example.com","password":"<tu-contraseña>"}'
```

La respuesta trae `accessToken` (JWT de corta duración) y `refreshToken` (revocable,
rotativo). Ya autenticado como Admin, usa `POST /api/users` para crear técnicos (`Support`) u
otros administradores — ver `server/README.md` para la tabla completa de endpoints
`/api/auth/*` y `/api/users/*`.

**HTTPS es obligatorio en producción**: fuera de `localhost`, sirve el backend y el panel
solo detrás de HTTPS/TLS (p. ej. un proxy inverso con certificado válido). El access token
viaja en la URL de la conexión WebSocket de SignalR (`?access_token=...`, requisito del
propio protocolo del navegador) y, sin TLS, cualquiera en la red podría leerlo.

**Los tokens no deben persistirse de forma insegura**: el panel web guarda el access token
solo en memoria (nunca en `localStorage`/`sessionStorage`) y el refresh token en
`localStorage` únicamente como concesión pragmática de este MVP (ver la decisión documentada
en `web/src/auth/authStore.ts` y en `web/README.md`) — antes de producción, migrar el refresh
token a una cookie `httpOnly` + `Secure` servida solo por HTTPS.

## Verificar que todo funciona

```bash
curl http://localhost:5080/api/health

# Sin token: 401 (los endpoints de dispositivos/sesiones ya requieren autenticación)
curl -i http://localhost:5080/api/devices

# Con token de un usuario ya logueado (ver sección anterior):
curl http://localhost:5080/api/devices \
  -H "Authorization: Bearer <accessToken>"

# 1) Como Admin, genera un código de emparejamiento de un solo uso (vigente 10 minutos):
curl -X POST http://localhost:5080/api/pairing-codes \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken-de-un-Admin>" \
  -d '{"label":"Dispositivo de prueba"}'
# Respuesta: {"id":"...","code":"ABCDE-FGHJK","expiresAt":"...","label":"Dispositivo de prueba"}
# El campo "code" solo se devuelve aquí — GET /api/pairing-codes nunca lo expone.

# 2) "Android" usa ese código para emparejarse (endpoint público, sin JWT: lo llama el propio
#    dispositivo). El guion es solo visual, da igual con o sin él, mayúsculas o minúsculas:
curl -X POST http://localhost:5080/api/devices/enroll \
  -H "Content-Type: application/json" \
  -d '{"code":"ABCDE-FGHJK","deviceCode":"DEV-TEST-1","name":"Dispositivo de prueba"}'
```

`POST /api/devices/enroll` es público a propósito (lo usa Android, que no tiene JWT — su
autenticación sigue siendo `deviceCode`+`pairingKey`, sin relación con el login de técnicos),
pero ya no existe alta automática: sin un código de emparejamiento válido, vigente y sin usar,
`enroll` responde `400` con `{"errorCode": "invalid"|"expired"|"used"|"revoked", "message": "..."}`
según el caso, y limita los intentos por IP (5 por minuto) para dificultar adivinar códigos por
fuerza bruta. La respuesta de éxito incluye `pairingKey`: guárdalo, es lo que necesitarás para
probar `RemoteHub.AnnounceDevicePresence` (ver `docs/signaling-protocol.md`). Puedes solicitar
una sesión de prueba así (usa el `id` del dispositivo devuelto arriba y el `accessToken` de un
técnico o admin ya logueado):

```bash
curl -X POST http://localhost:5080/api/sessions/request \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"deviceId":"<id-del-dispositivo>"}'
```

Esto ya fue verificado de punta a punta (generación de código → `enroll` → `AnnounceDevicePresence`
con pairing key → `SessionRequested` recibido por el dispositivo → `RespondToSession` →
`SessionResponded` recibido por el panel → login → `JoinSession` autenticado del técnico dueño →
`SendOffer`/`ReceiveOffer`), junto con el rechazo correcto de: código de emparejamiento
inválido/vencido/usado/revocado, dos intentos concurrentes con el mismo código (solo uno
prospera), pairing key incorrecta, respuesta de sesión por una conexión que no es el
dispositivo, `JoinSession` sin autenticar o de un técnico que no es el dueño, y señalización
enviada por un tercero no autorizado o después de finalizada la sesión.

### Probar expiración, revocación y reutilización del código

```bash
# Revocar un código activo (usa el "id" de la respuesta de creación):
curl -X POST http://localhost:5080/api/pairing-codes/<id>/revoke \
  -H "Authorization: Bearer <accessToken-de-un-Admin>"

# Reutilizar un código ya usado (repite el mismo curl de "enroll" de arriba): responde 400
# con errorCode "used". Esperar 10 minutos y reintentar un código nunca usado da errorCode
# "expired". Ambos casos, igual que un código revocado, quedan auditados en PairingCodeEvent
# (ver server/README.md) sin guardar el código en texto plano.

# Listar códigos y su estado (Active/Used/Revoked/Expired):
curl http://localhost:5080/api/pairing-codes \
  -H "Authorization: Bearer <accessToken-de-un-Admin>"
```

### Reemparejar un dispositivo de desarrollo existente

Los dispositivos ya emparejados antes de este cambio (o con cualquier `pairingKey` ya emitido)
siguen funcionando sin tocar nada. Si necesitas volver a emparejar uno —por ejemplo, tras
reinstalar la app Android o pulsar "Desvincular dispositivo"— genera un código nuevo y llama a
`enroll` otra vez con el **mismo** `deviceCode` de siempre: el backend detecta que ya existe y
le asigna un `pairingKey` nuevo en vez de crear un dispositivo duplicado.

## Cambiar el puerto del panel web (5173 ↔ 5178, u otro)

El backend valida CORS contra un origen explícito (nunca `AllowAnyOrigin`), configurable vía
`Cors:AllowedOrigin` (o la variable de entorno `Cors__AllowedOrigin`). En Docker Compose esto
se resuelve solo: `infra/.env` define `CORS_ALLOWED_ORIGIN=http://localhost:${WEB_PORT}`, así
que basta con cambiar `WEB_PORT`:

```bash
# infra/.env
WEB_PORT=5178
```

```bash
cd infra
docker compose up --build
```

o, sin editar `.env`:

```bash
WEB_PORT=5178 docker compose up --build
```

El panel queda en `http://localhost:5178` y el backend acepta ese origen automáticamente
(verificado con `docker compose config` para 5173, 5178, y un origen explícito distinto). Para
volver al 5173 por defecto, quita `WEB_PORT` de `.env` o ponlo en `5173`. Ver
`infra/README.md` para más detalle, incluyendo cómo forzar un origen totalmente distinto
(no solo otro puerto de `localhost`).

## Reconexión de dispositivo y conexiones "zombie"

Si Android pierde la red y reconecta con una conexión SignalR nueva antes de que el servidor
note la desconexión de la vieja, `RemoteHub.AnnounceDevicePresence` ahora:

- retira la conexión anterior de sus grupos (`device:{id}` y cualquier `session:{id}` activa);
- le envía un aviso `ForceDisconnected` (best-effort);
- dejarla operar es imposible de todas formas: `RespondToSession` y `SendOffer/Answer/
  IceCandidate` verifican `Device.ConnectionId` contra quien invoca, así que la conexión vieja
  recibe "No autorizado" aunque su socket siga técnicamente abierto.

`OnDisconnectedAsync` marca el dispositivo Offline con un `UPDATE ... WHERE ConnectionId = X`
atómico, no un "leer y luego guardar": si una reconexión ya reemplazó `ConnectionId` entre
medias, ese `UPDATE` no afecta ninguna fila y el dispositivo se queda `Online` con la conexión
nueva (en vez de quedar incorrectamente `Offline`).

Para verificarlo manualmente por REST/SignalR (sin Android): registra un dispositivo, conecta
al hub y llama `AnnounceDevicePresence(deviceCode, pairingKey)` dos veces seguidas con **dos
conexiones distintas** (misma pairing key); la primera conexión debe recibir
`ForceDisconnected` y, si intenta `RespondToSession`/`SendOffer` después, debe recibir
`HubException: No autorizado...`. Las pruebas automatizadas cubren esto exhaustivamente: ver
`server/VisionSupport.Server.Tests/RemoteHubTests.cs`.

## Coturn y credenciales TURN efímeras

Coturn ya está activo como servicio `coturn` en `infra/docker-compose.yml` (autenticación por
secreto compartido, ver `infra/coturn/README.md` para la tabla completa de variables y los
pasos detallados de LAN/VPS). Resumen:

- **Local con Android** (emulador o dispositivo en tu misma red): pon tu **IP LAN** (nunca
  `localhost`) en `TURN_PUBLIC_HOST` y `TURN_EXTERNAL_IP` en `infra/.env` — Mac:
  `ipconfig getifaddr en0`; Linux: `hostname -I`.
- **VPS**: `TURN_PUBLIC_HOST`/`TURN_EXTERNAL_IP` = la IP pública del VPS, con `3478/udp+tcp` y
  `49160-49200/udp` abiertos en el firewall/security group, y un `TURN_SHARED_SECRET` propio
  (`openssl rand -hex 32` — nunca el valor de ejemplo `changeme_dev_only_turn_secret`).
- **HTTPS/TLS es obligatorio antes de producción** (ver `infra/coturn/README.md`): este hito
  corre Coturn sin TLS (`--no-tls --no-dtls`, sin URLs `turns:`) porque es solo para desarrollo
  local.

El backend emite las credenciales vía el método de SignalR
`RemoteHub.GetTurnCredentials(sessionId)` — no hay endpoint REST equivalente, y solo responde
al dispositivo o técnico ya autorizados de una sesión `Accepted`/`Active` (ver
`docs/signaling-protocol.md` y `docs/architecture.md`).

Android y el panel web ya invocan `GetTurnCredentials` automáticamente (una vez por sesión,
después de aceptar/unirse y antes de crear el `PeerConnection`/`RTCPeerConnection`) — ver
`android/README.md` y `web/README.md` para el flujo completo con un dispositivo real. Lo de
abajo sigue siendo útil para probar el mecanismo de forma aislada, sin backend/cliente reales:

```bash
# 1) Backend arriba, dispositivo registrado, sesión Accepted (ver sección anterior) y, desde la
#    conexión de SignalR ya autorizada, invoca GetTurnCredentials(sessionId). Devuelve algo como:
# {"iceServers":[{"urls":["stun:...","turn:...?transport=udp","turn:...?transport=tcp"],
#   "username":"1790661231:<sessionId>","credential":"..."}],"expiresAt":"..."}

# 2) Valida esa asignación contra el Coturn real (usa -W, no -u/-w: use-auth-secret es un
#    mecanismo de credenciales de CORTA duración, distinto al -u/-w de larga duración de la
#    herramienta):
docker exec infra-coturn-1 turnutils_uclient -v \
  -W "$(grep TURN_SHARED_SECRET infra/.env | cut -d= -f2)" \
  -u "<username-recibido>" \
  -n 1 -e 127.0.0.1 -r 3480 127.0.0.1
# Debe verse "success" e "IPv4. Received relay addr: ...".
```

Esto ya se verificó end-to-end en este hito: se generaron credenciales reales desde el backend
en ejecución, se confirmó el rechazo para una conexión no autorizada de la sesión
(`HubException: No autorizado...`), y se validó la asignación TURN contra el Coturn real con
ese mismo username, usando el `TURN_SHARED_SECRET` configurado.

## Sobre WebRTC en este hito

El panel (`web/`) ya recibe y renderiza vídeo real vía WebRTC (ver `web/README.md` y
`docs/signaling-protocol.md`); solo recibe vídeo (`recvonly`), nunca envía cámara/pantalla
propia. Android y el panel ya obtienen credenciales TURN efímeras (`GetTurnCredentials`) antes
de crear su `PeerConnection`/`RTCPeerConnection` (ver `android/README.md` y `web/README.md`),
así que la conexión puede completarse tanto P2P directo como vía relay Coturn cuando el P2P
directo no es posible (redes distintas, NAT simétrico, emulador Android detrás de NAT).

## Diagnóstico básico de dispositivos

Con un dispositivo ya emparejado y conectado, Android envía automáticamente un snapshot de
diagnóstico (batería, red, almacenamiento, versión de SO/app) al conectar y cada 60 segundos, y
eventos inmediatos ante cambio de red relevante, batería crítica, o error propio de la app — ver
`server/README.md` (sección "Diagnóstico de dispositivos"), `web/README.md` (sección
"Diagnóstico") y `android/README.md` (sección "Diagnóstico básico", con pasos de prueba
detallados usando `adb shell dumpsys battery` para simular batería crítica de forma
determinista).

Para inspeccionarlo directamente por REST, con el `accessToken` de un técnico o Admin ya
logueado y el `id` del dispositivo (visible en el panel o en `GET /api/devices`):

```bash
# Último snapshot (204 si el dispositivo existe pero todavía no reportó ninguno):
curl -i http://localhost:5080/api/devices/<id>/diagnostics \
  -H "Authorization: Bearer <accessToken>"

# Histórico de eventos, más recientes primero (hasta 200 por dispositivo — ver retención
# en server/README.md):
curl http://localhost:5080/api/devices/<id>/diagnostics/events \
  -H "Authorization: Bearer <accessToken>"
```

Ambos endpoints solo requieren JWT válido (cualquier técnico o Admin, igual que
`GET /api/devices`); la escritura, en cambio, nunca es por REST — solo por SignalR y solo desde
la propia conexión ya autorizada del dispositivo (`RemoteHub.ReportDiagnosticsSnapshot`/
`ReportDiagnosticEvent`), así que no hay forma de enviar diagnóstico "a nombre de" otro
dispositivo ni de que un técnico lo haga por REST.

## VisionDiagnostics — apps propias integradas

Una app Android propia puede reportar sus propios eventos (info/warning/error/excepción) al
mismo panel, atribuidos a su paquete — ver `android/vision-diagnostics-sdk/README.md` para la
guía de integración completa y `android/README.md` (sección "Pasos de prueba: VisionDiagnostics")
para el flujo de prueba paso a paso con la app de ejemplo incluida
(`android/vision-diagnostics-demo/`).

Resumen para probarlo rápido, con Vision Support ya emparejado en un dispositivo/emulador:

```bash
cd android
./gradlew :app:installDebug :vision-diagnostics-demo:installDebug
```

Abre "VisionDiagnostics Demo" en ese mismo dispositivo, pulsa "Simular error", y revisa la
pestaña "Diagnóstico" del panel: debe aparecer un evento "Excepción de app" con fuente
`com.example.visiondiagnosticsdemo`. Ambas apps comparten el keystore de debug por defecto de
Android, así que no hace falta configurar nada de firma para esta prueba local — en producción,
tu app y Vision Support deben firmarse con la misma clave de release (ver el README del SDK).

Variable de entorno relevante en el backend (ver `infra/.env.example`):

```bash
# Retención de eventos de diagnóstico por dispositivo (los más antiguos se podan al superarla).
DIAGNOSTICS__MAXEVENTSPERDEVICE=200
```
