# Migraciones EF Core

`InitialCreate` ya está generada (crea `Users`, `Devices`, `RemoteSessions`, `SessionEvents`,
FKs e índices). Se generó con el SDK de .NET 8 dentro de un contenedor Docker efímero,
porque el entorno de desarrollo donde se creó este scaffolding no tenía el SDK instalado:

```bash
docker run --rm -v "$(pwd)/server:/src" -w /src \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e ConnectionStrings__Default="Host=postgres;Port=5432;Database=visionsupport;Username=visionsupport;Password=changeme_dev_only" \
  --network infra_default \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c "dotnet tool install --global dotnet-ef --version 8.*; export PATH=\"\$PATH:/root/.dotnet/tools\"; dotnet ef migrations add InitialCreate --output-dir Migrations"
```

(requiere que `docker compose up -d postgres` esté corriendo antes, para que el contenedor
efímero pueda conectarse a la misma red `infra_default`).

`Program.cs` aplica las migraciones pendientes automáticamente al arrancar en `Development`
(`db.Database.Migrate()`), tanto en local como dentro de Docker — no hace falta ningún paso
manual adicional para tener las tablas en una base nueva.

Si en el futuro cambias las entidades (`Models/*.cs`) o el mapeo (`Data/AppDbContext.cs`),
genera una nueva migración con el mismo patrón, cambiando el nombre:

```bash
dotnet ef migrations add NombreDeLaMigracion --output-dir Migrations
```

O, si tienes el SDK de .NET 8 instalado localmente, simplemente:

```bash
cd server
dotnet ef migrations add NombreDeLaMigracion
```
