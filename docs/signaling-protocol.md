# Protocolo de señalización — RemoteHub (SignalR)

Endpoint del hub: `/hubs/remote` (WebSocket vía SignalR).

SignalR (ASP.NET Core) serializa los mensajes en JSON con propiedades en **camelCase**.
Los tipos C# de referencia están en `server/Hubs/SignalingMessages.cs`; el espejo en
TypeScript está en `web/src/types/signaling.ts`.

El backend **solo reenvía y registra** estos mensajes; no interpreta ni transforma el
contenido de `sdp` / `candidate`.

⚠️ **SignalR no admite parámetros opcionales en métodos de Hub.** Todo método listado abajo
debe invocarse con el número exacto de argumentos de su firma — enviar de menos produce un
error genérico de invocación en vez de usar el valor por defecto de C#. Cuando no aplique
(por ejemplo `reason`), envía `null` explícitamente.

## Modelo de autorización

El hub distingue dos identidades, independientes entre sí:

- **Identidad de dispositivo (Android)**: `PairingKey`, un secreto de 64 caracteres hex que
  el backend genera y devuelve **una sola vez**, en la respuesta de
  `POST /api/devices/enroll` (requiere un código de emparejamiento de un solo uso generado por
  un Admin — ver `docs/local-development.md` y `server/README.md`; ya no hay alta automática).
  Android debe guardarlo y presentarlo en
  `AnnounceDevicePresence`. Sin la clave correcta, el backend rechaza la conexión como ese
  dispositivo (`HubException`). Una vez anunciado, `Device.ConnectionId` (persistido en BD)
  es la prueba de que una conexión concreta "es" ese dispositivo para el resto de acciones
  (`RespondToSession`, `SendOffer`/`SendAnswer`/`SendIceCandidate`). Esto no cambió con la
  autenticación de técnicos: Android sigue sin JWT, nunca inicia sesión.
- **Identidad de técnico (panel web)**: JWT emitido por `POST /api/auth/login` (ver
  `server/README.md`), validado por `Microsoft.AspNetCore.Authentication.JwtBearer`.
  `RemoteHub.JoinSession` exige `[Authorize]` **y** que el `sub` (id de usuario) del token
  coincida con `RemoteSession.TechnicianUserId` (el técnico que solicitó la sesión por REST);
  `RemoteSession.TechnicianConnectionId` persiste luego cuál es "el" técnico autorizado para
  esa sesión.

El navegador no puede mandar cabeceras personalizadas en el *handshake* de WebSocket, así
que el cliente de SignalR manda el `accessToken` (JWT) como `Authorization: Bearer <token>`
en las peticiones HTTP (negociación) o como `?access_token=<token>` en la URL de conexión
WebSocket/SSE — el backend acepta ambas formas mediante
`JwtBearerEvents.OnMessageReceived` (solo para rutas bajo `/hubs/remote`; en el resto de la
API el token va siempre por cabecera). En Node.js el cliente de SignalR usa la cabecera
`Authorization`; en el navegador, la query string.

`SendOffer`/`SendAnswer`/`SendIceCandidate` solo se reenvían si: la sesión existe, está en
estado `Accepted`/`Active`, **y** quien invoca es el dispositivo o el técnico ya vinculados a
esa sesión concreta — así se evita que un tercero inyecte señalización falsa en una sesión
ajena, aunque conozca su `sessionId`.

## Grupos

| Grupo | Quién se une | Cuándo |
| --- | --- | --- |
| `panel` | Panel web (técnico) | Al conectar, llamando a `JoinPanel()` |
| `device:{deviceId}` | App Android | Al anunciarse online (con PairingKey válida), vía `AnnounceDevicePresence` |
| `session:{sessionId}` | Ambas partes | El dispositivo se une al aceptar la sesión; el panel llama a `JoinSession(sessionId)` tras ver `SessionResponded` con `accepted: true` (y ser el técnico dueño) |

## Métodos que invoca el cliente (`connection.invoke(...)`)

| Método | Quién lo llama | Parámetros (todos obligatorios) | Efecto / autorización |
| --- | --- | --- | --- |
| `JoinPanel()` | Panel web | — | Se une al grupo `panel` |
| `AnnounceDevicePresence(deviceCode, pairingKey)` | Android | `deviceCode: string`, `pairingKey: string` | Rechaza si `pairingKey` no coincide con la del registro. Si es válida: marca `Online`, guarda `connectionId`, se une a `device:{deviceId}`. Reconexión: si había otra conexión previa, la retira de sus grupos y le manda `ForceDisconnected` (ver abajo) |
| `RespondToSession(sessionId, accepted, reason)` | Android | `sessionId: guid`, `accepted: bool`, `reason: string \| null` | Rechaza si la conexión no es `Device.ConnectionId` de esa sesión, o si la sesión ya no está `Requested`. Si acepta: marca `Accepted`, se une a `session:{sessionId}` |
| `JoinSession(sessionId)` | Panel web | `sessionId: guid` | Requiere identidad de técnico autenticada y que sea `RemoteSession.TechnicianUserId`. Rechaza si la sesión no está `Accepted`/`Active` |
| `SendOffer(message)` | Quien inicia la oferta (normalmente panel) | `OfferMessage` | Requiere ser dispositivo o técnico ya vinculados a la sesión (ver arriba) |
| `SendAnswer(message)` | Quien responde (normalmente Android) | `AnswerMessage` | Igual que `SendOffer` |
| `SendIceCandidate(message)` | Ambas partes | `IceCandidateMessage` | Igual que `SendOffer` |
| `GetTurnCredentials(sessionId)` | Dispositivo o técnico (cuando P2P directo falla) | `sessionId: guid` | Devuelve `TurnCredentialsDto` (ver abajo). Misma autorización que `SendOffer`: rechaza sesión inexistente/pendiente/finalizada o conexión "zombie" |

### Forma de `TurnCredentialsDto` (respuesta de `GetTurnCredentials`)

```ts
IceServerDto        { urls: string[]; username: string | null; credential: string | null }
TurnCredentialsDto  { iceServers: IceServerDto[]; expiresAt: string }
```

`username` tiene el formato `"{timestampUnixDeExpiración}:{sessionId}"`; `credential` es
`Base64(HMAC-SHA1(TURN_SHARED_SECRET, username))`. Expira en 5 minutos por defecto (máximo
absoluto 10 minutos, ver `docs/architecture.md` y `server/Services/TurnCredentialService.cs`).
No existe ningún endpoint REST equivalente: esta es la única vía de emisión.

## Eventos que recibe el cliente (`connection.on(...)`)

| Evento | Grupo destino | Payload | Cuándo |
| --- | --- | --- | --- |
| `DevicePresenceChanged` | `panel` | `DevicePresenceMessage` | Un dispositivo se conecta o se desconecta |
| `SessionRequested` | `device:{deviceId}` | `SessionRequestedMessage` | El técnico solicita sesión (vía `POST /api/sessions/request`) |
| `SessionResponded` | `panel` | `SessionRespondedMessage` | El dispositivo acepta o rechaza |
| `ReceiveOffer` | `session:{sessionId}` (excepto emisor) | `OfferMessage` | Reenvío de una oferta SDP |
| `ReceiveAnswer` | `session:{sessionId}` (excepto emisor) | `AnswerMessage` | Reenvío de una respuesta SDP |
| `ReceiveIceCandidate` | `session:{sessionId}` (excepto emisor) | `IceCandidateMessage` | Reenvío de un candidato ICE |
| `ForceDisconnected` | Solo la conexión reemplazada | `string` (motivo) | Otra conexión del mismo dispositivo acaba de anunciarse (reconexión); esta conexión debe cerrarse — ya no puede responder sesiones ni enviar señalización aunque no cierre su socket |
| `SessionEnded` | `session:{sessionId}` y `panel` | `SessionEndedMessage` | La sesión finaliza (vía `POST /api/sessions/{id}/end`) |

## Forma de los mensajes

```ts
DevicePresenceMessage    { deviceId: string; deviceCode: string; status: "Offline"|"Online"|"InSession"; timestamp: string }
SessionRequestedMessage  { sessionId: string; deviceId: string; technicianUserId: string; technicianName: string; requestedAt: string }
SessionRespondedMessage  { sessionId: string; deviceId: string; accepted: boolean; reason: string | null }
OfferMessage             { sessionId: string; sdp: string }
AnswerMessage             { sessionId: string; sdp: string }
IceCandidateMessage      { sessionId: string; candidate: string; sdpMid: string | null; sdpMLineIndex: number | null }
SessionEndedMessage      { sessionId: string; reason: string | null }
```

## Flujo completo de una sesión

1. Android se empareja por REST (`POST /api/devices/enroll`, con un código de emparejamiento de
   un solo uso generado por un Admin desde el panel) y guarda el `pairingKey` devuelto (solo se
   entrega esa vez).
2. Android arranca/reconecta → `AnnounceDevicePresence(deviceCode, pairingKey)` → backend
   verifica la clave, marca `Online` → `DevicePresenceChanged` a `panel`.
3. Técnico pulsa "Solicitar sesión" en el panel → `POST /api/sessions/request` (con su JWT de
   `/api/auth/login`) → backend crea la `RemoteSession` (`Requested`) → emite
   `SessionRequested` a `device:{deviceId}`.
4. Android responde → `RespondToSession(sessionId, true, null)` → backend verifica que es la
   conexión del dispositivo dueño, marca `Accepted`, dispositivo entra a
   `session:{sessionId}` → emite `SessionResponded` a `panel`.
5. Panel ve `accepted: true` → `JoinSession(sessionId)` → backend verifica que es el técnico
   dueño → panel entra a `session:{sessionId}`. Ambas partes llaman `GetTurnCredentials(sessionId)`
   una vez, justo después de unirse/aceptar la sesión y siempre antes de crear su
   `RTCPeerConnection`/`PeerConnection` (Android: justo antes de `WebRtcManager.createPeerConnection`;
   panel: justo antes de construir `RemoteScreenPeer`), y usan esos `iceServers` al crearlo —
   ver `android/README.md` y `web/README.md`. Luego intercambian `SendOffer` / `SendAnswer` /
   `SendIceCandidate` con Android; si la conectividad P2P directa no es posible, el candidate
   pair seleccionado termina siendo `relay` a través de Coturn en vez de `host`/`srflx`.
6. Cualquiera de las partes o el técnico finaliza → `POST /api/sessions/{id}/end` → backend
   marca `Ended` → emite `SessionEnded` a `session:{sessionId}` y `panel`. A partir de aquí,
   `SendOffer`/`SendAnswer`/`SendIceCandidate` para esa sesión quedan rechazados.

Cada paso 3–6 también queda registrado como `SessionEvent` en PostgreSQL para auditoría.
