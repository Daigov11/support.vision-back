# Vision Support — Backend e infraestructura

API ASP.NET Core (net8.0) + SignalR + PostgreSQL + Coturn para el sistema de soporte remoto
atendido Vision Support. El panel web (React) vive en un repositorio aparte
(`support.vision-front`); el agente Android no se publica en ningún repositorio todavía.

- `server/` — API REST + hub SignalR (`RemoteHub`). Ver `server/README.md`.
- `infra/` — Docker Compose, Coturn y configuración de Nginx para el panel. Ver `infra/README.md`.
- `docs/` — Arquitectura, protocolo de señalización y guía de desarrollo local.

## Desarrollo local

Ver `docs/local-development.md`.

## Despliegue en producción

Ver `infra/README.md` para la configuración de producción (dominios, TLS, CORS, Coturn).
