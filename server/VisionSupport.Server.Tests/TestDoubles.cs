using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VisionSupport.Server.Data;
using VisionSupport.Server.Hubs;
using VisionSupport.Server.Options;
using VisionSupport.Server.Services;

namespace VisionSupport.Server.Tests;

/// <summary>
/// Grabadora de lo que el Hub envía a un cliente concreto o a un grupo. Un mensaje real de
/// SignalR usa <c>SendAsync</c>, que internamente llama a este único método.
/// </summary>
internal sealed class RecordingClientProxy : IClientProxy
{
    public List<(string Method, object?[] Args)> Sent { get; } = [];

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        Sent.Add((method, args));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sustituto de <see cref="IGroupManager"/> que además recuerda la pertenencia efectiva a cada
/// grupo, para poder afirmar "esta conexión ya NO está en el grupo X" tras una reconexión.
/// </summary>
internal sealed class FakeGroupManager : IGroupManager
{
    public List<(string ConnectionId, string GroupName)> Added { get; } = [];
    public List<(string ConnectionId, string GroupName)> Removed { get; } = [];

    private readonly Dictionary<string, HashSet<string>> _membersByGroup = new();

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Added.Add((connectionId, groupName));
        if (!_membersByGroup.TryGetValue(groupName, out var members))
        {
            members = [];
            _membersByGroup[groupName] = members;
        }
        members.Add(connectionId);
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Removed.Add((connectionId, groupName));
        if (_membersByGroup.TryGetValue(groupName, out var members))
        {
            members.Remove(connectionId);
        }
        return Task.CompletedTask;
    }

    public bool IsMember(string connectionId, string groupName) =>
        _membersByGroup.TryGetValue(groupName, out var members) && members.Contains(connectionId);
}

/// <summary>
/// Sustituto de <see cref="IHubCallerClients"/> compartido por todas las instancias de Hub de
/// un mismo test (como en el servidor real, los grupos/clientes son estado del servidor, no de
/// una invocación concreta). Simplificación consciente: <c>OthersInGroup</c> no excluye al
/// emisor (no hace falta para lo que afirman estas pruebas: qué se envió a qué grupo/cliente).
/// </summary>
internal sealed class FakeHubCallerClients : IHubCallerClients
{
    private readonly Dictionary<string, RecordingClientProxy> _byConnectionId = new();
    private readonly Dictionary<string, RecordingClientProxy> _byGroup = new();

    public RecordingClientProxy CallerProxy { get; } = new();
    public RecordingClientProxy FallbackProxy { get; } = new();

    public IClientProxy Caller => CallerProxy;
    public IClientProxy Others => FallbackProxy;
    public IClientProxy All => FallbackProxy;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => FallbackProxy;

    public IClientProxy Client(string connectionId) =>
        _byConnectionId.TryGetValue(connectionId, out var proxy) ? proxy : _byConnectionId[connectionId] = new RecordingClientProxy();

    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => FallbackProxy;

    public IClientProxy Group(string groupName) =>
        _byGroup.TryGetValue(groupName, out var proxy) ? proxy : _byGroup[groupName] = new RecordingClientProxy();

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Group(groupName);

    public IClientProxy Groups(IReadOnlyList<string> groupNames) => FallbackProxy;

    public IClientProxy OthersInGroup(string groupName) => Group(groupName);

    public IClientProxy User(string userId) => FallbackProxy;

    public IClientProxy Users(IReadOnlyList<string> userIds) => FallbackProxy;

    public RecordingClientProxy ForConnection(string connectionId) => (RecordingClientProxy)Client(connectionId);

    public RecordingClientProxy ForGroup(string groupName) => (RecordingClientProxy)Group(groupName);
}

/// <summary>Construye un ClaimsPrincipal con la misma forma que emite TokenService.CreateAccessToken.</summary>
internal static class TestAuth
{
    public static ClaimsPrincipal CreateUserPrincipal(Guid userId, string email = "user@test.local", string displayName = "Usuario de prueba", string role = "Support")
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Role, role),
        ], authenticationType: "TestAuth");
        return new ClaimsPrincipal(identity);
    }
}

/// <summary>Sustituto mínimo de <see cref="HubCallerContext"/> para una conexión simulada.</summary>
internal sealed class FakeHubCallerContext : HubCallerContext
{
    public FakeHubCallerContext(string connectionId, ClaimsPrincipal? user = null)
    {
        ConnectionId = connectionId;
        User = user;
    }

    public override string ConnectionId { get; }
    public override string? UserIdentifier => User?.Identity?.Name;
    public override ClaimsPrincipal? User { get; }
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;
    public override void Abort()
    {
    }
}

/// <summary>
/// "Servidor" de prueba: una base SQLite en memoria compartida (igual que en producción, cada
/// invocación del Hub recibe su propio <see cref="AppDbContext"/> de un mismo scope de EF Core,
/// todos apuntando a la misma base) más un <see cref="FakeGroupManager"/> y
/// <see cref="FakeHubCallerClients"/> compartidos entre todas las conexiones simuladas.
/// </summary>
internal sealed class HubTestServer : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public FakeGroupManager Groups { get; } = new();
    public FakeHubCallerClients Clients { get; } = new();

    /// <summary>
    /// Mutable: los tests pueden cambiar estos valores (p. ej. vaciar SharedSecret) ANTES de
    /// llamar a <see cref="CreateHub"/>, que construye un TurnCredentialService fresco con el
    /// snapshot actual en cada llamada.
    /// </summary>
    public TurnOptions TurnOptions { get; } = new()
    {
        SharedSecret = "test-shared-secret-not-a-real-one",
        Realm = "test.local",
        PublicHost = "127.0.0.1",
        Port = 3478,
        CredentialTtlSeconds = 300,
    };

    /// <summary>
    /// Mutable igual que <see cref="TurnOptions"/>: los tests pueden bajar MaxEventsPerDevice
    /// antes de llamar a <see cref="CreateHub"/> para probar la retención sin escribir cientos
    /// de eventos reales.
    /// </summary>
    public DiagnosticsOptions DiagnosticsOptions { get; } = new();

    private HubTestServer(SqliteConnection connection, DbContextOptions<AppDbContext> options)
    {
        _connection = connection;
        _options = options;
    }

    public static HubTestServer Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        using (var db = new AppDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        return new HubTestServer(connection, options);
    }

    public AppDbContext NewDbContext() => new(_options);

    /// <summary>
    /// Crea una nueva instancia de Hub "conectada" con el connectionId dado, con su propio
    /// AppDbContext (como en producción, donde cada invocación recibe un DbContext con scope
    /// propio) pero compartiendo los grupos/clientes del resto del servidor simulado.
    /// </summary>
    public RemoteHub CreateHub(string connectionId, ClaimsPrincipal? user = null)
    {
        var db = NewDbContext();
        var turnOptionsSnapshot = new TurnOptions
        {
            SharedSecret = TurnOptions.SharedSecret,
            Realm = TurnOptions.Realm,
            PublicHost = TurnOptions.PublicHost,
            Port = TurnOptions.Port,
            TlsPort = TurnOptions.TlsPort,
            CredentialTtlSeconds = TurnOptions.CredentialTtlSeconds,
        };
        var turnService = new TurnCredentialService(Microsoft.Extensions.Options.Options.Create(turnOptionsSnapshot));
        var diagnosticsOptionsSnapshot = new DiagnosticsOptions { MaxEventsPerDevice = DiagnosticsOptions.MaxEventsPerDevice };

        return new RemoteHub(db, turnService, Microsoft.Extensions.Options.Options.Create(diagnosticsOptionsSnapshot))
        {
            Context = new FakeHubCallerContext(connectionId, user),
            Clients = Clients,
            Groups = Groups,
        };
    }

    public void Dispose() => _connection.Dispose();
}
