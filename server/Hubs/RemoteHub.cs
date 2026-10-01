using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VisionSupport.Server.Authentication;
using VisionSupport.Server.Data;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;
using VisionSupport.Server.Options;
using VisionSupport.Server.Services;

namespace VisionSupport.Server.Hubs;

/// <summary>
/// Canal único de señalización en tiempo real entre la app Android, el panel web y el backend.
/// Solo transporta presencia y metadatos WebRTC (offer/answer/ICE); nunca vídeo, audio,
/// control remoto ni datos de otras apps.
///
/// Modelo de autorización:
/// - Identidad de DISPOSITIVO: PairingKey emitida en POST /api/devices/enroll (requiere un
///   código de emparejamiento de un solo uso generado por un Admin), verificada en
///   AnnounceDevicePresence, y luego el Device.ConnectionId persistido es la prueba de que una
///   conexión concreta "es" ese dispositivo para el resto de la sesión. Completamente separado
///   del JWT de técnicos: Android nunca tiene ni necesita un access token.
/// - Identidad de TÉCNICO/ADMIN: JWT Bearer (ver Services/TokenService.cs), enviado por el panel
///   como access_token en la query string de la conexión SignalR (el navegador no puede mandar
///   cabeceras propias en el handshake de WebSocket). Verificada en JoinSession contra
///   RemoteSession.TechnicianUserId; RemoteSession.TechnicianConnectionId persiste luego cuál es
///   "el" técnico autorizado para esa sesión concreta.
/// - SendOffer/SendAnswer/SendIceCandidate solo aceptan al dispositivo o técnico ya verificados
///   de esa sesión, y solo si la sesión está Accepted/Active. No llevan [Authorize] porque los
///   llama tanto el dispositivo (sin JWT) como el técnico (con JWT).
/// </summary>
public class RemoteHub : Hub
{
    private readonly AppDbContext _db;
    private readonly TurnCredentialService _turnCredentials;
    private readonly DiagnosticsOptions _diagnosticsOptions;

    public RemoteHub(AppDbContext db, TurnCredentialService turnCredentials, IOptions<DiagnosticsOptions> diagnosticsOptions)
    {
        _db = db;
        _turnCredentials = turnCredentials;
        _diagnosticsOptions = diagnosticsOptions.Value;
    }

    /// <summary>El panel web llama esto al conectar para recibir presencia y solicitudes de sesión.</summary>
    public async Task JoinPanel()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Panel);
    }

    /// <summary>
    /// La app Android llama esto al conectar/reconectar para anunciarse como online.
    /// Requiere el PairingKey emitido en el registro: sin él, cualquiera que conociera un
    /// deviceCode (no es secreto) podría suplantar a ese dispositivo.
    ///
    /// Reconexión segura: si el dispositivo ya tenía otra conexión registrada (p. ej. Android
    /// perdió la red y volvió a conectar sin que el servidor detectara aún la desconexión),
    /// esa conexión anterior queda inmediatamente sin autorización — RespondToSession y
    /// SendOffer/Answer/IceCandidate solo aceptan a la conexión indicada por
    /// Device.ConnectionId, que aquí pasa a ser la nueva — y además la retiramos explícitamente
    /// de sus grupos para que no reciba más eventos duplicados ni parezca seguir activa.
    /// </summary>
    public async Task AnnounceDevicePresence(string deviceCode, string pairingKey)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceCode == deviceCode);
        if (device is null)
        {
            throw new HubException($"Dispositivo con código '{deviceCode}' no está registrado.");
        }

        if (!SecretsMatch(device.PairingKey, pairingKey))
        {
            throw new HubException("Clave de emparejamiento inválida para este dispositivo.");
        }

        var previousConnectionId = device.ConnectionId;
        var isReconnect = !string.IsNullOrEmpty(previousConnectionId) && previousConnectionId != Context.ConnectionId;

        device.Status = DeviceStatus.Online;
        device.ConnectionId = Context.ConnectionId;
        device.LastSeenAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        if (isReconnect)
        {
            // La autorización real ya no depende de esto (cada acción sensible revalida
            // Device.ConnectionId), pero igual la sacamos del grupo para que deje de recibir
            // SessionRequested/broadcasts duplicados, y le pedimos (best-effort; SignalR no
            // expone una forma portable de forzar el cierre de OTRA conexión) que se desconecte.
            await Groups.RemoveFromGroupAsync(previousConnectionId!, HubGroups.Device(device.Id));
            await Clients.Client(previousConnectionId!).SendAsync(
                "ForceDisconnected", "Reemplazada por una nueva conexión del mismo dispositivo.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Device(device.Id));

        // Si había una sesión Accepted/Active en curso (p. ej. esta reconexión ocurrió a mitad
        // de una sesión de soporte por un corte de red breve), la conexión nueva debe quedar en
        // su sala de señalización y la anterior debe salir; si no, el intercambio de
        // offer/answer/ICE quedaría inconsistente (mensajes que no llegan a nadie o rechazados).
        var activeSessionIds = await _db.RemoteSessions
            .Where(s => s.DeviceId == device.Id && (s.Status == SessionStatus.Accepted || s.Status == SessionStatus.Active))
            .Select(s => s.Id)
            .ToListAsync();

        foreach (var sessionId in activeSessionIds)
        {
            if (isReconnect)
            {
                await Groups.RemoveFromGroupAsync(previousConnectionId!, HubGroups.Session(sessionId));
            }
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Session(sessionId));
        }

        await Clients.Group(HubGroups.Panel).SendAsync(
            "DevicePresenceChanged",
            new DevicePresenceMessage(device.Id, device.DeviceCode, device.Status.ToString(), DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// El dispositivo acepta o rechaza una sesión solicitada previamente vía REST.
    /// Solo la conexión que anunció ese dispositivo (Device.ConnectionId) puede responder.
    /// SignalR no admite parámetros opcionales en métodos de Hub: el cliente debe enviar
    /// siempre los 3 argumentos explícitamente (usar null si no hay motivo de rechazo).
    /// </summary>
    public async Task RespondToSession(Guid sessionId, bool accepted, string? reason)
    {
        var session = await _db.RemoteSessions.Include(s => s.Device).FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null)
        {
            throw new HubException($"Sesión '{sessionId}' no encontrada.");
        }

        if (session.Device is null || session.Device.ConnectionId != Context.ConnectionId)
        {
            throw new HubException("No autorizado: esta conexión no corresponde al dispositivo de la sesión.");
        }

        if (session.Status != SessionStatus.Requested)
        {
            throw new HubException("La sesión ya no está pendiente de respuesta.");
        }

        session.Status = accepted ? SessionStatus.Accepted : SessionStatus.Rejected;
        if (accepted)
        {
            session.StartedAt = DateTimeOffset.UtcNow;
            session.Device.Status = DeviceStatus.InSession;
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Session(sessionId));
        }

        _db.SessionEvents.Add(new SessionEvent
        {
            RemoteSessionId = sessionId,
            Type = accepted ? SessionEventType.SessionAccepted : SessionEventType.SessionRejected,
            Payload = reason
        });
        await _db.SaveChangesAsync();

        await Clients.Group(HubGroups.Panel).SendAsync(
            "SessionResponded",
            new SessionRespondedMessage(sessionId, session.DeviceId, accepted, reason));
    }

    /// <summary>
    /// El panel web se une a la sala de una sesión aceptada para intercambiar SDP/ICE.
    /// Único método del Hub exclusivamente de técnico/admin (Android nunca lo llama), así que
    /// es el único que puede llevar [Authorize] sin romper la conexión anónima del dispositivo.
    /// La comprobación manual de abajo es defensa en profundidad: [Authorize] ya lo exige a
    /// nivel de SignalR, pero repetirlo aquí deja un mensaje claro y mantiene el método
    /// verificable invocándolo directamente (como hacen las pruebas unitarias del Hub).
    /// </summary>
    [Authorize]
    public async Task JoinSession(Guid sessionId)
    {
        if (Context.User?.Identity?.IsAuthenticated != true)
        {
            throw new HubException("No autenticado: se requiere iniciar sesión.");
        }

        var session = await _db.RemoteSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null)
        {
            throw new HubException($"Sesión '{sessionId}' no encontrada.");
        }

        if (session.Status is not (SessionStatus.Accepted or SessionStatus.Active))
        {
            throw new HubException("La sesión no está aceptada ni activa.");
        }

        var technicianId = Context.User!.GetUserId();
        if (technicianId != session.TechnicianUserId)
        {
            throw new HubException("No autorizado: no eres el técnico de esta sesión.");
        }

        session.TechnicianConnectionId = Context.ConnectionId;
        await _db.SaveChangesAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Session(sessionId));
    }

    public async Task SendOffer(OfferMessage message)
    {
        await AuthorizeSessionParticipantAsync(message.SessionId);
        await LogSignalingEvent(message.SessionId, SessionEventType.Offer, message.Sdp);
        await Clients.OthersInGroup(HubGroups.Session(message.SessionId)).SendAsync("ReceiveOffer", message);
    }

    public async Task SendAnswer(AnswerMessage message)
    {
        await AuthorizeSessionParticipantAsync(message.SessionId);
        await LogSignalingEvent(message.SessionId, SessionEventType.Answer, message.Sdp);
        await Clients.OthersInGroup(HubGroups.Session(message.SessionId)).SendAsync("ReceiveAnswer", message);
    }

    public async Task SendIceCandidate(IceCandidateMessage message)
    {
        await AuthorizeSessionParticipantAsync(message.SessionId);
        await LogSignalingEvent(message.SessionId, SessionEventType.IceCandidate, message.Candidate);
        await Clients.OthersInGroup(HubGroups.Session(message.SessionId)).SendAsync("ReceiveIceCandidate", message);
    }

    /// <summary>
    /// Entrega credenciales TURN efímeras (username/credential HMAC de corta duración, nunca
    /// las credenciales estáticas del servidor) para que el dispositivo o el técnico configuren
    /// su <c>RTCPeerConnection</c> cuando la conectividad P2P directa no sea posible. Reutiliza
    /// exactamente la misma autorización que offer/answer/ICE: solo el dispositivo o el técnico
    /// ya vinculados a una sesión Accepted/Active pueden pedirlas — rechaza sesión inexistente,
    /// pendiente, finalizada, o una conexión "zombie" reemplazada por una reconexión.
    /// No hay ningún endpoint REST público equivalente: esta es la única vía de emisión.
    /// </summary>
    public async Task<TurnCredentialsDto> GetTurnCredentials(Guid sessionId)
    {
        await AuthorizeSessionParticipantAsync(sessionId);

        if (!_turnCredentials.IsConfigured)
        {
            throw new HubException("TURN no está configurado en este servidor.");
        }

        // La sesión es la "etiqueta" (no sensible) del username, útil para correlacionar en los
        // logs de Coturn qué credencial corresponde a qué sesión de soporte.
        return _turnCredentials.GenerateCredentials(sessionId.ToString());
    }

    /// <summary>
    /// Snapshot periódico/al conectar del estado del dispositivo (batería, red, almacenamiento,
    /// identificación de SO/app — ver docs/architecture.md). Sobrescribe el único
    /// DeviceHealthSnapshot de este dispositivo (no es histórico; para eso ver
    /// ReportDiagnosticEvent). Solo la conexión ya autorizada por AnnounceDevicePresence puede
    /// reportar, y siempre para SU PROPIO Device — no toma un deviceId del cliente, así que
    /// estructuralmente ninguna conexión puede informar datos "de otro dispositivo".
    /// </summary>
    public async Task ReportDiagnosticsSnapshot(DiagnosticsSnapshotMessage message)
    {
        var device = await RequireDeviceConnectionAsync();

        var snapshot = await _db.DeviceHealthSnapshots.FirstOrDefaultAsync(s => s.DeviceId == device.Id);
        if (snapshot is null)
        {
            snapshot = new DeviceHealthSnapshot { DeviceId = device.Id };
            _db.DeviceHealthSnapshots.Add(snapshot);
        }

        snapshot.BatteryPercent = message.BatteryPercent;
        snapshot.IsCharging = message.IsCharging;
        snapshot.NetworkConnected = message.NetworkConnected;
        snapshot.NetworkType = Truncate(message.NetworkType, 32);
        snapshot.WifiSignalLevel = message.WifiSignalLevel;
        snapshot.StorageTotalBytes = message.StorageTotalBytes;
        snapshot.StorageUsedBytes = message.StorageUsedBytes;
        snapshot.StorageAvailableBytes = message.StorageAvailableBytes;
        snapshot.Manufacturer = Truncate(message.Manufacturer, 64) ?? string.Empty;
        snapshot.Model = Truncate(message.Model, 64) ?? string.Empty;
        snapshot.AndroidVersion = Truncate(message.AndroidVersion, 64) ?? string.Empty;
        snapshot.AppVersion = Truncate(message.AppVersion, 32) ?? string.Empty;
        snapshot.ReceivedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        await Clients.Group(HubGroups.Panel).SendAsync("DeviceDiagnosticsUpdated", DeviceHealthSnapshotDto.FromModel(snapshot));
    }

    /// <summary>
    /// Evento inmediato de diagnóstico: propio del agente (cambio de red relevante, batería
    /// crítica, error propio ya saneado) o de una app propia integrada vía el SDK
    /// `VisionDiagnostics` (Info/Warning/Error/Exception — ver
    /// android/vision-diagnostics-sdk). Misma autorización que ReportDiagnosticsSnapshot: la
    /// conexión solo puede reportar para su propio Device — nunca Logcat ni una excepción cruda
    /// sin sanear.
    ///
    /// Para los cuatro tipos "Application*", `SourcePackage` es obligatorio: ya fue verificado
    /// por Android (UID real, no un valor declarado) en el `ContentProvider` del agente antes de
    /// llegar aquí (ver DiagnosticEventMessage). Para los tres tipos propios del agente, se
    /// ignora cualquier SourcePackage/SourceAppVersion/Code que llegara — esos campos solo tienen
    /// sentido para eventos de una app integrada.
    /// </summary>
    public async Task ReportDiagnosticEvent(DiagnosticEventMessage message)
    {
        var device = await RequireDeviceConnectionAsync();

        if (!Enum.TryParse<DeviceDiagnosticEventType>(message.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            throw new HubException("Tipo de evento de diagnóstico inválido.");
        }

        var isApplicationEvent = type is DeviceDiagnosticEventType.ApplicationInfo
            or DeviceDiagnosticEventType.ApplicationWarning
            or DeviceDiagnosticEventType.ApplicationError
            or DeviceDiagnosticEventType.ApplicationException;

        if (isApplicationEvent && string.IsNullOrWhiteSpace(message.SourcePackage))
        {
            throw new HubException("SourcePackage es obligatorio para eventos de aplicaciones integradas.");
        }

        var evt = new DeviceDiagnosticEvent
        {
            DeviceId = device.Id,
            Type = type,
            Message = Truncate(message.Message, 500),
            Code = isApplicationEvent ? Truncate(message.Code, 64) : null,
            SourcePackage = isApplicationEvent ? Truncate(message.SourcePackage, 150) : null,
            SourceAppVersion = isApplicationEvent ? Truncate(message.SourceAppVersion, 32) : null,
            OccurredAt = isApplicationEvent ? message.OccurredAt : null,
        };
        _db.DeviceDiagnosticEvents.Add(evt);
        await _db.SaveChangesAsync();

        // Poda de retención al escribir (ver docs/architecture.md): evita crecimiento
        // ilimitado sin necesitar un job de limpieza aparte.
        await PruneOldDiagnosticEventsAsync(device.Id);

        await Clients.Group(HubGroups.Panel).SendAsync("DeviceDiagnosticEventReceived", DeviceDiagnosticEventDto.FromModel(evt));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Busca el dispositivo por Id/DeviceCode primero (dato inmutable, seguro de leer sin
        // condición de carrera) y solo entonces marca Offline con un UPDATE condicionado a que
        // ConnectionId SIGA siendo el que se está desconectando en ese preciso instante. Esto
        // evita la carrera: si AnnounceDevicePresence de una conexión nueva ya reemplazó
        // Device.ConnectionId entre que empezamos a procesar esta desconexión y que la
        // guardamos, el UPDATE afecta 0 filas y no tocamos el estado (que ya es correcto:
        // Online con la conexión nueva).
        var device = await _db.Devices
            .Where(d => d.ConnectionId == Context.ConnectionId)
            .Select(d => new { d.Id, d.DeviceCode })
            .FirstOrDefaultAsync();

        if (device is not null)
        {
            // DateTimeOffset.UtcNow capturado en una variable: EF Core no traduce la llamada
            // estática directamente dentro de un SetProperty (falla en tiempo de ejecución con
            // "does not represent a valid value"); como valor cerrado (closure) sí lo traduce.
            var disconnectedAt = DateTimeOffset.UtcNow;

            var stillCurrentConnection = await _db.Devices
                .Where(d => d.Id == device.Id && d.ConnectionId == Context.ConnectionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.Status, DeviceStatus.Offline)
                    .SetProperty(d => d.ConnectionId, (string?)null)
                    .SetProperty(d => d.LastSeenAt, disconnectedAt))
                > 0;

            if (stillCurrentConnection)
            {
                await Clients.Group(HubGroups.Panel).SendAsync(
                    "DevicePresenceChanged",
                    new DevicePresenceMessage(device.Id, device.DeviceCode, DeviceStatus.Offline.ToString(), disconnectedAt));
            }
        }

        var session = await _db.RemoteSessions.FirstOrDefaultAsync(s => s.TechnicianConnectionId == Context.ConnectionId);
        if (session is not null)
        {
            session.TechnicianConnectionId = null;
            await _db.SaveChangesAsync();
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Verifica que la sesión exista, esté Accepted/Active, y que la conexión que llama sea
    /// el dispositivo o el técnico ya vinculados a ella. Es el único punto de entrada para
    /// reenviar offer/answer/ICE: evita que un tercero inyecte señalización en una sesión ajena.
    /// </summary>
    private async Task<RemoteSession> AuthorizeSessionParticipantAsync(Guid sessionId)
    {
        var session = await _db.RemoteSessions.Include(s => s.Device).FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null)
        {
            throw new HubException($"Sesión '{sessionId}' no encontrada.");
        }

        if (session.Status is not (SessionStatus.Accepted or SessionStatus.Active))
        {
            throw new HubException("La sesión no está aceptada ni activa.");
        }

        var isDevice = session.Device?.ConnectionId == Context.ConnectionId;
        var isTechnician = session.TechnicianConnectionId == Context.ConnectionId;
        if (!isDevice && !isTechnician)
        {
            throw new HubException("No autorizado para esta sesión.");
        }

        return session;
    }

    private async Task LogSignalingEvent(Guid sessionId, SessionEventType type, string payload)
    {
        _db.SessionEvents.Add(new SessionEvent
        {
            RemoteSessionId = sessionId,
            Type = type,
            Payload = payload
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Resuelve el Device dueño de ESTA conexión (la misma prueba de identidad que
    /// RespondToSession/SendOffer/etc.: Device.ConnectionId == Context.ConnectionId, fijado por
    /// AnnounceDevicePresence). Usado por ReportDiagnosticsSnapshot/ReportDiagnosticEvent: como
    /// el Device sale de la conexión y no de un parámetro del cliente, ninguna conexión puede
    /// reportar diagnóstico "en nombre de" otro dispositivo.
    /// </summary>
    private async Task<Device> RequireDeviceConnectionAsync()
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.ConnectionId == Context.ConnectionId);
        if (device is null)
        {
            throw new HubException("No autorizado: esta conexión no corresponde a un dispositivo emparejado y anunciado.");
        }
        return device;
    }

    /// <summary>
    /// Política de retención (ver Options/DiagnosticsOptions.cs y docs/architecture.md):
    /// conserva como máximo <c>Diagnostics:MaxEventsPerDevice</c> eventos por dispositivo (200
    /// por defecto). Se poda en cada escritura en vez de con un job aparte porque el volumen
    /// esperado por dispositivo es pequeño — nunca hace falta un DELETE masivo.
    /// </summary>
    private async Task PruneOldDiagnosticEventsAsync(Guid deviceId)
    {
        // Orden en memoria (no vía ORDER BY en SQL): mismo motivo que en otros lugares del
        // proyecto (ver PairingCodesController) — SQLite, usado en las pruebas de integración,
        // no admite ordenar por DateTimeOffset en el servidor; Postgres sí, pero mantenemos un
        // único camino de código para ambos. El volumen por dispositivo es pequeño (como mucho
        // MaxDiagnosticEventsPerDevice + 1 filas), así que ordenar en memoria es intrascendente.
        var ids = await _db.DeviceDiagnosticEvents
            .Where(e => e.DeviceId == deviceId)
            .Select(e => new { e.Id, e.CreatedAt })
            .ToListAsync();

        var idsToKeep = ids
            .OrderByDescending(e => e.CreatedAt)
            .Take(_diagnosticsOptions.MaxEventsPerDevice)
            .Select(e => e.Id)
            .ToHashSet();

        await _db.DeviceDiagnosticEvents
            .Where(e => e.DeviceId == deviceId && !idsToKeep.Contains(e.Id))
            .ExecuteDeleteAsync();
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>Comparación en tiempo constante para no filtrar el PairingKey por temporización.</summary>
    private static bool SecretsMatch(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        if (expectedBytes.Length != actualBytes.Length)
        {
            // CryptographicOperations.FixedTimeEquals exige igual longitud; si difieren, ya
            // sabemos que no coinciden (la longitud de un PairingKey no es sensible).
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
