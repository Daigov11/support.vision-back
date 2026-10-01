using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Data;
using VisionSupport.Server.Models;
using Xunit;

namespace VisionSupport.Server.Tests;

/// <summary>
/// JoinSession es el único método del Hub que valida JWT (ver comentario en RemoteHub.cs).
/// Estas pruebas cubren esa identidad de técnico/admin de punta a punta, sustituyendo el
/// esquema de dev-auth por un ClaimsPrincipal con la misma forma que emite TokenService.
/// </summary>
public class RemoteHubJoinSessionTests
{
    private const string PairingKey = "0123456789abcdef0123456789abcdef";

    private static async Task<(Device Device, User Technician, RemoteSession Session)> SeedAcceptedSessionAsync(AppDbContext db)
    {
        var device = new Device { DeviceCode = "DEV-JOIN-1", Name = "Dispositivo", PairingKey = PairingKey, Status = DeviceStatus.Offline };
        var technician = new User
        {
            Email = "join-test@dev.local",
            DisplayName = "Técnico de prueba",
            Role = UserRole.Support,
            PasswordHash = "irrelevante-para-esta-prueba",
        };
        db.Devices.Add(device);
        db.Users.Add(technician);
        await db.SaveChangesAsync();

        var session = new RemoteSession
        {
            DeviceId = device.Id,
            TechnicianUserId = technician.Id,
            Status = SessionStatus.Accepted,
            StartedAt = DateTimeOffset.UtcNow,
        };
        db.RemoteSessions.Add(session);
        await db.SaveChangesAsync();

        return (device, technician, session);
    }

    [Fact]
    public async Task JoinSession_WithoutAuthentication_Throws()
    {
        using var server = HubTestServer.Create();
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            (_, _, session) = await SeedAcceptedSessionAsync(db);
        }

        var anonymousHub = server.CreateHub("conn-anon"); // sin ClaimsPrincipal
        var ex = await Assert.ThrowsAsync<HubException>(() => anonymousHub.JoinSession(session.Id));
        Assert.Contains("No autenticado", ex.Message);
    }

    [Fact]
    public async Task JoinSession_AuthenticatedAsDifferentUser_Throws()
    {
        using var server = HubTestServer.Create();
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            (_, _, session) = await SeedAcceptedSessionAsync(db);
        }

        var impostorPrincipal = TestAuth.CreateUserPrincipal(Guid.NewGuid()); // usuario real, pero no el dueño
        var impostorHub = server.CreateHub("conn-impostor", impostorPrincipal);

        var ex = await Assert.ThrowsAsync<HubException>(() => impostorHub.JoinSession(session.Id));
        Assert.Contains("No autorizado", ex.Message);
    }

    [Fact]
    public async Task JoinSession_AuthenticatedAsSessionOwner_Succeeds()
    {
        using var server = HubTestServer.Create();
        User technician;
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            (_, technician, session) = await SeedAcceptedSessionAsync(db);
        }

        var ownerPrincipal = TestAuth.CreateUserPrincipal(technician.Id, technician.Email, technician.DisplayName);
        var ownerHub = server.CreateHub("conn-owner", ownerPrincipal);

        await ownerHub.JoinSession(session.Id);

        using var assertDb = server.NewDbContext();
        var reloaded = await assertDb.RemoteSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal("conn-owner", reloaded.TechnicianConnectionId);
    }
}
