# Coturn (activo en desarrollo)

Coturn ya está integrado en `infra/docker-compose.yml` como el servicio `coturn`, iniciado
con `docker compose up` igual que el resto del stack. Usa autenticación por **secreto
compartido** (`--use-auth-secret`), nunca usuarios estáticos: el backend
(`server/Services/TurnCredentialService.cs`) genera un `username`/`credential` efímero
(máximo 10 minutos) por sesión, usando el mismo secreto (`TURN_SHARED_SECRET`).

El servicio se configura por línea de comandos directamente en `docker-compose.yml` (no monta
un `turnserver.conf`), a partir de las variables de `infra/.env.example`:

| Variable | Para qué | Local | VPS |
| --- | --- | --- | --- |
| `TURN_SHARED_SECRET` | Firma HMAC de las credenciales efímeras | genera uno con `openssl rand -hex 32` | igual, pero un secreto propio y distinto |
| `TURN_REALM` | Realm STUN/TURN | cualquier valor, p. ej. `visionsupport.local` | igual |
| `TURN_PORT` | Puerto STUN/TURN (UDP+TCP) | `3478` | `3478`, abierto en el firewall/security group |
| `TURN_RELAY_MIN_PORT` / `TURN_RELAY_MAX_PORT` | Rango de puertos de relay de medios | `49160`–`49200` | igual, abierto en UDP en el firewall |
| `TURN_PUBLIC_HOST` | Host/IP que el backend pone en las URLs `turn:`/`stun:` | tu **IP LAN** (no `localhost`) | la **IP pública** del VPS |
| `TURN_EXTERNAL_IP` | IP que el propio Coturn anuncia como dirección de relay | la misma IP LAN | la misma IP pública |

`turnserver.conf.example` en esta carpeta documenta el equivalente en formato de archivo de
configuración, útil si en algún momento corres Coturn fuera de Docker (p. ej. como servicio
del sistema en un VPS) en vez de con el `command:` de `docker-compose.yml`.

## Desarrollo local con Android (emulador o dispositivo en la misma LAN)

1. Averigua la IP LAN de tu máquina (la que usan otros dispositivos de tu red para
   alcanzarte), **no** `127.0.0.1`/`localhost`:
   - macOS: `ipconfig getifaddr en0` (o `en1`/`en0` según tu interfaz Wi-Fi/Ethernet).
   - Linux: `hostname -I`.
2. En `infra/.env`, pon esa IP en `TURN_PUBLIC_HOST` y `TURN_EXTERNAL_IP`.
3. `docker compose up --build` — Coturn publica `3478/udp+tcp` y el rango de relay en tu
   máquina; cualquier dispositivo en la misma red (incluido el emulador de Android, que sale a
   la red a través del host) puede alcanzarlos.
4. Si tienes firewall activo en tu máquina (p. ej. el Firewall de macOS), permite conexiones
   entrantes para el proceso de Docker / los puertos UDP 3478 y 49160-49200.

## VPS (producción/staging)

1. El VPS necesita una **IP pública** propia (no solo estar detrás de un NAT sin mapeo).
2. Abre en el firewall/security group: `3478/udp`, `3478/tcp`, y `49160-49200/udp` (o el rango
   que configures).
3. `TURN_PUBLIC_HOST` y `TURN_EXTERNAL_IP` = la IP pública del VPS.
4. Genera un `TURN_SHARED_SECRET` real (`openssl rand -hex 32`) — el valor de ejemplo
   (`changeme_dev_only_turn_secret`) es solo para tu máquina local, nunca lo uses en un VPS.
5. **HTTPS/TLS es obligatorio en producción**: el navegador exige un contexto seguro
   (HTTPS) para `getUserMedia`/`RTCPeerConnection` fuera de `localhost`, y el backend debe
   servirse por HTTPS para que el panel (servido también por HTTPS) pueda llamar a SignalR sin
   contenido mixto. Este hito no configura TLS (`--no-tls --no-dtls` en Coturn, sin `turns:` en
   las credenciales); antes de exponer esto fuera de tu red local, pon un proxy TLS (p. ej.
   Nginx/Caddy con Let's Encrypt) delante del backend, y certificados reales en Coturn
   (`cert`/`pkey` en `turnserver.conf.example`, más `TURN_TLS_PORT`/`Turn__TlsPort` si se
   habilita más adelante) para poder ofrecer también una URL `turns:`.

## Verificación manual (sin Android/web)

Con el stack corriendo, genera credenciales reales llamando a
`RemoteHub.GetTurnCredentials(sessionId)` (ver `docs/signaling-protocol.md`) y valida la
asignación TURN contra el Coturn real:

```bash
docker exec infra-coturn-1 turnutils_uclient -v \
  -W "$(grep TURN_SHARED_SECRET infra/.env | cut -d= -f2)" \
  -u "<username-recibido-del-backend>" \
  -n 1 -e 127.0.0.1 -r 3480 127.0.0.1
```

Un `success` con `Received relay addr: ...` confirma que el secreto/algoritmo son correctos.
(`turnutils_uclient -u/-w` sin `-W` usa el mecanismo de credenciales de **larga duración**, que
coturn en modo `--use-auth-secret` no soporta — por eso hace falta `-W` incluso pasando ya un
`username` propio; ver detalle en `docs/local-development.md`).
