# Infraestructura local — Vision Support

Orquesta PostgreSQL, el backend, el panel web y el relay TURN (Coturn) para desarrollo local
con Docker Compose. Ver `coturn/README.md` para la configuración detallada de Coturn
(variables, LAN local, VPS).

## Uso

```bash
cd infra
cp .env.example .env
docker compose up --build
```

- Backend: `http://localhost:${SERVER_HTTP_PORT:-5080}` (Swagger en `/swagger`)
- Panel web: `http://localhost:${WEB_PORT:-5173}`
- PostgreSQL: `localhost:${POSTGRES_PORT:-5432}`

Validar la configuración sin levantar contenedores:

```bash
docker compose config
```

## Puertos

| Servicio | Puerto host (por defecto) | Variable |
| --- | --- | --- |
| PostgreSQL | 5432 | `POSTGRES_PORT` |
| Backend | 5080 (mapea al 8080 interno) | `SERVER_HTTP_PORT` |
| Panel web | 5173 (mapea al 80 interno de Nginx) | `WEB_PORT` |
| Coturn (STUN/TURN) | 3478/udp+tcp | `TURN_PORT` |
| Coturn (relay de medios) | 49160–49200/udp | `TURN_RELAY_MIN_PORT` / `TURN_RELAY_MAX_PORT` |

## Cambiar el puerto del panel (CORS sincronizado)

Si el 5173 ya está en uso (por ejemplo por otro proyecto tuyo), cambia **solo** `WEB_PORT` en
`infra/.env`:

```bash
# infra/.env
WEB_PORT=5178
```

`CORS_ALLOWED_ORIGIN` en ese mismo archivo está definido como `http://localhost:${WEB_PORT}`,
así que el backend acepta automáticamente el origen correcto sin tocar nada más. Luego:

```bash
cd infra
docker compose up --build
```

y el panel queda en `http://localhost:5178`. Para volver a 5173, cambia `WEB_PORT` de nuevo (o
borra `infra/.env` y cópialo otra vez desde `.env.example`).

Para arrancar con un puerto puntual sin editar `.env`:

```bash
WEB_PORT=5178 docker compose up --build
```

(`docker compose config` con ese mismo prefijo te deja ver el origen CORS resuelto antes de
levantar nada). Si alguna vez necesitas un origen que no sea `http://localhost:<puerto>` (otro
host o dominio), reemplaza la línea de `CORS_ALLOWED_ORIGIN` en `.env` por un valor literal en
vez de `http://localhost:${WEB_PORT}`.

## Notas

- Ningún valor de `.env.example` es un secreto real: son credenciales de solo-desarrollo
  para una base de datos que solo escucha en `localhost`.
- Los datos de PostgreSQL persisten en el volumen nombrado `postgres_data`.
- `web` construye el bundle de producción de Vite y lo sirve con Nginx, usando la
  configuración en `nginx/web.conf`. Las URLs del backend/hub se inyectan en tiempo de
  build vía `VITE_API_BASE_URL` / `VITE_HUB_URL` (derivadas de `SERVER_HTTP_PORT`).
- `server` aplica las migraciones EF Core pendientes automáticamente al arrancar en
  `Development` (`InitialCreate` ya está generada y versionada). Verificado: `docker compose
  up --build` desde cero crea las tablas `Users`, `Devices`, `RemoteSessions` y
  `SessionEvents` en PostgreSQL sin pasos manuales.
- El backend valida en `RemoteHub` que solo el dispositivo emparejado (pairing key) y el
  técnico dueño de una sesión puedan responderla, unirse a ella o intercambiar señalización.
- Reconexión de dispositivo: si Android vuelve a anunciarse con una conexión SignalR nueva
  mientras la anterior sigue técnicamente abierta, esa conexión anterior queda sin autorización
  de inmediato (se retira de sus grupos y deja de poder responder sesiones o enviar
  señalización), y `OnDisconnectedAsync` nunca marca el dispositivo Offline si su conexión ya
  fue reemplazada. Pruebas en `server/VisionSupport.Server.Tests/RemoteHubTests.cs`.
- Coturn usa autenticación por secreto compartido (`TURN_SHARED_SECRET`), nunca usuarios
  estáticos; el backend emite credenciales TURN efímeras (máximo 10 min) por
  `RemoteHub.GetTurnCredentials(sessionId)`, solo a quien ya esté autorizado en esa sesión. Ver
  `coturn/README.md` para configurar `TURN_PUBLIC_HOST`/`TURN_EXTERNAL_IP` en LAN local o VPS,
  y `docs/architecture.md` para el detalle del mecanismo.

## Producción

El despliegue de producción (dominio `support.apiworking.com.pe` / `api.support.apiworking.com.pe`)
NO usa el servicio `web` de `docker-compose.yml` ni publica el puerto de PostgreSQL. En su lugar:

- `docker-compose.prod.yml` levanta solo `postgres` (sin puerto publicado), `coturn` y `server`
  (`server` escucha únicamente en `127.0.0.1:${SERVER_HTTP_PORT}`, nunca en todas las interfaces).
- El panel se compila con Vite (`VITE_API_BASE_URL`/`VITE_HUB_URL` apuntando al dominio de la API)
  y el `dist/` resultante se sirve directamente con el Nginx nativo del servidor, sin contenedor.
- `nginx/production/*.conf.example` son los vhosts de Nginx (fuera de Docker) para ambos dominios:
  uno estático para el panel, otro con soporte de `Upgrade`/`Connection` para la API + SignalR.
  TLS se gestiona con `certbot --nginx`, igual que el resto de sitios del servidor.
- `.env.production.example` documenta las variables requeridas; cópialo a `infra/.env` en el
  servidor (nunca en el repositorio) con valores reales generados para ese despliegue.

```bash
cd infra
cp .env.production.example .env   # editar con secretos reales, solo en el servidor
docker compose -f docker-compose.prod.yml --env-file .env up -d --build
```

Para actualizar un despliegue existente tras un `git pull`:

```bash
cd /opt/vision-support
git pull
docker compose -f infra/docker-compose.prod.yml --env-file infra/.env up -d --build
```
