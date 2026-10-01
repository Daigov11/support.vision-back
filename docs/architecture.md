# Arquitectura — Vision Support

## Separación control plane / media plane

Vision Support separa explícitamente dos planos:

### Control plane (backend + panel web)

Gestionado por `server/` (ASP.NET Core + PostgreSQL + SignalR):

- Identidad de usuarios técnicos.
- Registro y estado de dispositivos (`online` / `offline` / `en sesión`).
- Ciclo de vida de sesiones de soporte (solicitud, aceptación, rechazo, fin).
- **Señalización WebRTC**: intercambio de `offer`/`answer`/candidatos ICE a través del
  hub SignalR `RemoteHub`.
- **Credenciales TURN efímeras** para el relay Coturn (ver sección dedicada más abajo).

El backend **nunca** ve ni transporta un solo fotograma de vídeo o audio. Solo mueve
metadatos: JSON de presencia, JSON de señalización, y filas en PostgreSQL.

### Media plane (WebRTC, fuera del backend)

Una vez que dos partes (app Android y panel web) han intercambiado `offer`/`answer`/ICE
a través del control plane, establecen una conexión **WebRTC directa**:

- **P2P** cuando la red lo permite (mismo NAT, red local, o NAT compatible).
- A través de un **relay TURN (Coturn)** cuando la conectividad directa no es posible.

En ningún caso el vídeo pasa por `server/`. El backend solo facilitó el "apretón de
manos" inicial.

```
Android  ──(vídeo WebRTC, P2P o vía TURN)──  Panel web
   │                                             │
   └──────────(señalización SignalR)─────────────┘
                       │
                  server/ (control plane)
                       │
                 PostgreSQL (usuarios,
                 dispositivos, sesiones,
                 eventos de señalización)
```

## Entidades principales

- **User**: técnico (o admin) que opera el panel.
- **Device**: dispositivo Android registrado, con `deviceCode` único y estado
  (`Offline` / `Online` / `InSession`).
- **RemoteSession**: una sesión de soporte entre un técnico y un dispositivo, con su
  ciclo de vida (`Requested` → `Accepted`/`Rejected` → `Ended`).
- **SessionEvent**: auditoría de cada mensaje de señalización de una sesión (tipo +
  payload JSON), útil para depuración y trazabilidad — nunca contiene vídeo/audio.
- **PairingCode**: código de emparejamiento de un solo uso que un Admin genera para autorizar
  el alta de un dispositivo nuevo; guarda solo el hash del código, nunca el valor en texto
  plano (ver `server/README.md`).
- **PairingCodeEvent**: auditoría de cada creación/uso/revocación/intento fallido de un
  `PairingCode` (IP y marca de tiempo, nunca el código).

## Autenticación y autorización

Dos mecanismos independientes, sin relación entre sí:

- **Técnicos y administradores**: login por email/contraseña
  (`POST /api/auth/login`) que emite un JWT de acceso de corta duración más un refresh token
  revocable/rotativo (hasheado en PostgreSQL, nunca en texto plano). No existe registro
  público: solo un Admin crea cuentas (`POST /api/users`). Roles `Admin`/`Support` aplicados
  vía `[Authorize(Roles = ...)]` en los controladores. Ver `server/README.md` para el detalle
  completo (endpoints, bootstrap del primer Admin, rotación de refresh tokens).
- **Dispositivos (Android)**: un `PairingKey` emitido una sola vez al emparejar el dispositivo
  (`POST /api/devices/enroll`), que Android debe presentar en `RemoteHub.AnnounceDevicePresence`.
  No usa JWT; es un mecanismo completamente separado del login de técnicos. No existe alta
  automática: `enroll` exige un código de emparejamiento de un solo uso que un Admin genera
  desde el panel (`POST /api/pairing-codes`), de alta entropía, vigente 10 minutos y limitado
  por IP — ver `server/README.md` y `docs/local-development.md`.

El `RemoteHub` aplica control de acceso real con estos datos: solo el dispositivo emparejado
puede responder sus propias solicitudes de sesión, solo el técnico autenticado que la
solicitó puede unirse a ella como panel (`[Authorize]` en `JoinSession`, comparado contra
`RemoteSession.TechnicianUserId`), y solo esos dos participantes pueden intercambiar
offer/answer/ICE de esa sesión concreta (ver `docs/signaling-protocol.md`).

## Credenciales TURN efímeras (Coturn)

Cuando la conectividad P2P directa falla (NATs distintos, redes móviles, emulador Android
detrás de NAT), el cliente necesita un relay TURN. El backend **nunca** expone el secreto
compartido de Coturn ni credenciales públicas fijas: emite credenciales de corta duración
(máximo 10 minutos) mediante el mecanismo "TURN REST API" que Coturn entiende de forma nativa
con `--use-auth-secret`:

```
username   = "{timestamp-unix-de-expiración}:{idSesión}"
credential = Base64(HMAC-SHA1(TURN_SHARED_SECRET, username))
```

Coturn valida el HMAC y el timestamp por su cuenta; el backend nunca llama a Coturn
directamente, solo comparten el mismo `TURN_SHARED_SECRET` (ver `infra/.env.example`).

- **No hay endpoint REST público** para esto. La única vía es el método de SignalR
  `RemoteHub.GetTurnCredentials(sessionId)`, que reutiliza exactamente la misma autorización
  que `SendOffer`/`SendAnswer`/`SendIceCandidate`: solo el dispositivo o el técnico ya
  vinculados a una sesión `Accepted`/`Active` pueden pedirlas (rechaza sesión inexistente,
  pendiente, finalizada, o una conexión "zombie" reemplazada por una reconexión — ver
  `docs/signaling-protocol.md`).
- Ni el secreto ni la credencial calculada se registran en logs (`TurnCredentialService` no
  usa `ILogger` en absoluto); tampoco los clientes (`SignalRSignalingClient` en Android,
  `useRemoteHub` en el panel) registran `username`/`credential` en Logcat ni en consola.
- Android y el panel web ya llaman a `GetTurnCredentials(sessionId)` una vez por sesión,
  después de que esta queda `Accepted`/unida y siempre antes de crear el `PeerConnection`/
  `RTCPeerConnection` (ver `android/README.md` y `web/README.md`). Las credenciales solo se
  mantienen en memoria durante la sesión; si no se pueden obtener, ambos clientes muestran un
  error comprensible y liberan lo que ya se haya iniciado, en vez de degradar silenciosamente a
  solo P2P.

## Diagnóstico básico de dispositivos

Permite al técnico ver el estado actual e historial reciente de cada Android emparejado
(batería, red, almacenamiento, versión de SO/app, errores propios) sin leer Logcat ni datos de
otras apps. Dos entidades, dos ciclos de vida distintos:

- **`DeviceHealthSnapshot`**: el **último** estado reportado, una fila por `Device` (se
  sobrescribe en cada snapshot, nunca crece). Se envía al conectar y cada 60 segundos mientras
  el dispositivo sigue conectado.
- **`DeviceDiagnosticEvent`**: histórico append-only de eventos puntuales — tres tipos propios
  del agente (`NetworkChanged`, `BatteryCritical`, `AgentError`) y cuatro de una app propia
  integrada vía el SDK `VisionDiagnostics` (`ApplicationInfo`/`ApplicationWarning`/
  `ApplicationError`/`ApplicationException` — ver más abajo). Sujeto a una política de
  **retención configurable** (`Options/DiagnosticsOptions.cs`, `Diagnostics__MaxEventsPerDevice`,
  200 por defecto): se podan los más antiguos en cada escritura
  (`RemoteHub.PruneOldDiagnosticEventsAsync`), sin necesidad de un job de limpieza aparte. 200 es
  un valor inicial razonable para un volumen esperado bajo; configurable porque varias apps
  propias reportando al mismo dispositivo puede justificar un límite distinto.

**Autorización** (dos direcciones distintas, ver `server/README.md` para el detalle de las
pruebas):

- **Lectura** (`GET /api/devices/{id}/diagnostics[/events]`): igual que `GET /api/devices` —
  cualquier técnico o Admin autenticado (JWT) puede ver el diagnóstico de cualquier dispositivo;
  no hay un concepto de "dispositivo asignado a un técnico" en este producto.
- **Escritura** (`RemoteHub.ReportDiagnosticsSnapshot`/`ReportDiagnosticEvent`): nunca por REST,
  solo por SignalR, y solo la conexión ya autorizada por `AnnounceDevicePresence` (el mismo
  `Device.ConnectionId == Context.ConnectionId` que protege `RespondToSession`/`SendOffer`/etc.
  — ver más arriba). El `Device` se resuelve de la conexión que llama, nunca de un `deviceId`
  que el mensaje pudiera llevar: estructuralmente, ninguna conexión puede reportar diagnóstico
  "en nombre de" otro dispositivo.

**Qué nunca se envía ni se persiste**: contraseñas, `pairingKey`, JWT, contenido de pantalla,
contactos, ubicación, ni Logcat/logs de otras apps. Android no solicita permisos nuevos para
esto salvo `ACCESS_WIFI_STATE` (permiso "normal", concedido en la instalación sin diálogo) para
leer el tipo de red y, cuando el sistema lo permite sin `ACCESS_FINE_LOCATION` (que
deliberadamente no se solicita), la intensidad de la señal Wi-Fi — ver `android/README.md`.

**Rendimiento en Android**: sin foreground service adicional ni *polling* agresivo — una sola
corrutina con `delay()` de 60 s sobre el scope ya existente de `SessionCoordinator`, más un
`ConnectivityManager.NetworkCallback` y un `BroadcastReceiver` de batería basados en callbacks
del sistema (no en sondeo). Todo se detiene al desconectar del hub o al desvincular el
dispositivo (`DiagnosticsReporter.stop()`).

## SDK `VisionDiagnostics` — apps propias integradas

Una app Android propia (distinta de Vision Support, p. ej. `com.example.tuapp`) puede reportar
sus propios eventos estructurados al panel, sin que Vision Support lea su Logcat ni sus archivos.
Ver `android/vision-diagnostics-sdk/README.md` para la guía de integración completa; resumen de
la arquitectura:

```
App propia (con el SDK VisionDiagnostics)
    │  VisionDiagnostics.info/warn/error/recordException/addBreadcrumb
    ▼
ContentProvider.call() — IPC entre procesos, protegido por permiso `signature`
    │
    ▼
DiagnosticsProvider (dentro del proceso de Vision Support)
    │  1. El sistema ya bloqueó la llamada si el proceso no tiene el permiso `signature`
    │     (solo se concede a apps firmadas con el MISMO certificado que Vision Support).
    │  2. Resuelve el paquete llamador con Binder.getCallingUid() + PackageManager — nunca
    │     confía en un packageName que el propio mensaje pudiera declarar.
    │  3. Aplica límite de frecuencia por paquete y vuelve a acotar tamaño/forma del payload
    │     (defensa en profundidad: el SDK ya sanea, esto no depende de que lo haga bien).
    ▼
DiagnosticsEventQueue (SQLite, persistente) — encola el evento aunque no haya conexión
    │
    ▼
QueuedEventsFlusher — drena la cola por el Hub ya autorizado, con reintento y backoff
    │  (mismo canal que RemoteHub.ReportDiagnosticEvent; el Device se resuelve de la conexión
    │   SignalR ya autorizada por AnnounceDevicePresence, nunca de un id que la app propia
    │   pudiera enviar — esa app JAMÁS ve el pairingKey ni tiene acceso a él)
    ▼
Backend (DeviceDiagnosticEvent con SourcePackage/SourceAppVersion/Code/OccurredAt)
    ▼
Panel web (pestaña Diagnóstico: fuente, nivel, filtros — ver web/README.md)
```

Por qué un `ContentProvider` con permiso `signature` (en vez de, por ejemplo, un servicio de red
propio o un Intent broadcast): es el mecanismo estándar de Android para IPC local entre apps con
control de acceso reforzado por el propio sistema operativo — no depende de que ninguna de las
dos apps tenga red, y la garantía de "solo mismo certificado de firma" la impone el SO al resolver
el permiso, no una comprobación de aplicación que un cliente modificado pudiera saltarse.

**Persistencia y reintento** (`diagnostics/queue/` en el módulo `app`): los eventos de apps
integradas se encolan en SQLite antes de intentar subirlos — si el agente no tiene conexión con
el backend en ese momento, el evento sobrevive un reinicio del proceso y se reintenta con backoff
exponencial (2 s → 60 s) en cuanto haya conexión. La cola tiene un tope de tamaño (500 filas) para
acotar el crecimiento si el backend estuviera caído mucho tiempo, y se vacía por completo al
"Desvincular dispositivo" (`SessionCoordinator.unlink`). Los tres tipos de evento propios del
agente (`NetworkChanged`/`BatteryCritical`/`AgentError`) siguen siendo *best-effort* sin cola,
sin cambios respecto a como se implementaron originalmente — son de bajo impacto y alta
frecuencia relativa frente a los de una app integrada.

## Alcance explícitamente fuera de este hito

Por diseño y por las reglas del proyecto, **no** se implementa (ni se implementará vía
el backend/panel/infra):

- Control remoto real o `AccessibilityService`.
- Lectura de logs u otras apps del dispositivo.
- Grabación de pantalla, audio, o transferencia de archivos.
- TLS/`turns:` en Coturn (obligatorio antes de producción, ver `infra/coturn/README.md`).
  HTTPS/TLS del backend y del panel también es requisito de producción, no de este hito.
