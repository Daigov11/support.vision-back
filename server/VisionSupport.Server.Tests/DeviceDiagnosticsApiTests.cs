using System.Net;
using System.Net.Http.Json;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;
using Xunit;

namespace VisionSupport.Server.Tests;

/// <summary>
/// Pruebas de integración HTTP reales para GET /api/devices/{id}/diagnostics y
/// .../diagnostics/events: requieren JWT (cualquier técnico o Admin, igual que
/// GET /api/devices — no hay restricción adicional por dispositivo en la lectura; la
/// restricción real de "no otro dispositivo" vive en la escritura vía Hub, ver
/// RemoteHubDiagnosticsTests), 404 para un dispositivo que no existe, y 204 quieto para uno que
/// existe pero todavía no reportó nada.
/// </summary>
public class DeviceDiagnosticsApiTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public DeviceDiagnosticsApiTests(TestApiFactory factory)
    {
        _factory = factory;
    }

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@test.local";
    private static string UniqueDeviceCode(string label) => $"dev-{label}-{Guid.NewGuid():N}";

    private async Task<string> LoginAsNewSupportAsync(string label)
    {
        var email = UniqueEmail(label);
        await _factory.SeedUserAsync(email, "correct-password-1", role: UserRole.Support);
        var response = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "correct-password-1"));
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokensResponse>();
        return tokens!.AccessToken;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task GetDiagnostics_WithoutToken_IsUnauthorized()
    {
        var device = await _factory.SeedDeviceAsync(UniqueDeviceCode("noauth"));

        var response = await _factory.Client.GetAsync($"/api/devices/{device.Id}/diagnostics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDiagnosticEvents_WithoutToken_IsUnauthorized()
    {
        var device = await _factory.SeedDeviceAsync(UniqueDeviceCode("noauth-events"));

        var response = await _factory.Client.GetAsync($"/api/devices/{device.Id}/diagnostics/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDiagnostics_ForUnknownDevice_ReturnsNotFound()
    {
        var accessToken = await LoginAsNewSupportAsync("diag-404");

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{Guid.NewGuid()}/diagnostics", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDiagnostics_ForDeviceWithoutSnapshotYet_ReturnsNoContent()
    {
        var accessToken = await LoginAsNewSupportAsync("diag-empty");
        var device = await _factory.SeedDeviceAsync(UniqueDeviceCode("empty"));

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{device.Id}/diagnostics", accessToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetDiagnostics_ForDeviceWithSnapshot_ReturnsItsData()
    {
        var accessToken = await LoginAsNewSupportAsync("diag-data");
        var device = await _factory.SeedDeviceAsync(UniqueDeviceCode("withdata"));
        await _factory.SeedDeviceHealthSnapshotAsync(device.Id, batteryPercent: 55);

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{device.Id}/diagnostics", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DeviceHealthSnapshotDto>();
        Assert.Equal(device.Id, dto!.DeviceId);
        Assert.Equal(55, dto.BatteryPercent);
        Assert.Equal("Wifi", dto.NetworkType);
    }

    [Fact]
    public async Task GetDiagnostics_AsAnyAuthenticatedTechnician_IsVisible()
    {
        // Igual que GET /api/devices: cualquier técnico o Admin autenticado ve cualquier
        // dispositivo, no solo "el suyo" — no existe un concepto de dispositivo asignado a un
        // técnico concreto en este producto (ver docs/architecture.md).
        var supportToken = await LoginAsNewSupportAsync("diag-support-visibility");
        var adminEmail = UniqueEmail("diag-admin-visibility");
        await _factory.SeedUserAsync(adminEmail, "correct-password-1", role: UserRole.Admin);
        var adminLogin = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(adminEmail, "correct-password-1"));
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<AuthTokensResponse>())!.AccessToken;

        var device = await _factory.SeedDeviceAsync(UniqueDeviceCode("shared"));
        await _factory.SeedDeviceHealthSnapshotAsync(device.Id);

        var supportResponse = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{device.Id}/diagnostics", supportToken));
        var adminResponse = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{device.Id}/diagnostics", adminToken));

        Assert.Equal(HttpStatusCode.OK, supportResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task GetDiagnosticEvents_ForUnknownDevice_ReturnsNotFound()
    {
        var accessToken = await LoginAsNewSupportAsync("diag-events-404");

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{Guid.NewGuid()}/diagnostics/events", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDiagnosticEvents_ForDeviceWithoutEvents_ReturnsEmptyList()
    {
        var accessToken = await LoginAsNewSupportAsync("diag-events-empty");
        var device = await _factory.SeedDeviceAsync(UniqueDeviceCode("no-events"));

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{device.Id}/diagnostics/events", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<DeviceDiagnosticEventDto>>();
        Assert.Empty(events!);
    }

    [Fact]
    public async Task GetDiagnosticEvents_ReturnsNewestFirst()
    {
        var accessToken = await LoginAsNewSupportAsync("diag-events-order");
        var device = await _factory.SeedDeviceAsync(UniqueDeviceCode("order"));
        var now = DateTimeOffset.UtcNow;
        await _factory.SeedDiagnosticEventAsync(device.Id, DeviceDiagnosticEventType.NetworkChanged, "oldest", now.AddMinutes(-10));
        await _factory.SeedDiagnosticEventAsync(device.Id, DeviceDiagnosticEventType.BatteryCritical, "newest", now);
        await _factory.SeedDiagnosticEventAsync(device.Id, DeviceDiagnosticEventType.AgentError, "middle", now.AddMinutes(-5));

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{device.Id}/diagnostics/events", accessToken));

        var events = await response.Content.ReadFromJsonAsync<List<DeviceDiagnosticEventDto>>();
        Assert.Equal(["newest", "middle", "oldest"], events!.Select(e => e.Message));
    }

    [Fact]
    public async Task GetDiagnosticEvents_IncludesApplicationSourceFields()
    {
        var accessToken = await LoginAsNewSupportAsync("diag-events-source");
        var device = await _factory.SeedDeviceAsync(UniqueDeviceCode("source"));
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        await _factory.SeedDiagnosticEventAsync(
            device.Id,
            DeviceDiagnosticEventType.ApplicationException,
            "NullPointerException al cargar el carrito",
            sourcePackage: "com.example.tuapp",
            sourceAppVersion: "3.2.1",
            code: "CART_LOAD_FAILED",
            occurredAt: occurredAt);

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/devices/{device.Id}/diagnostics/events", accessToken));

        var events = await response.Content.ReadFromJsonAsync<List<DeviceDiagnosticEventDto>>();
        var evt = Assert.Single(events!);
        Assert.Equal("ApplicationException", evt.Type);
        Assert.Equal("com.example.tuapp", evt.SourcePackage);
        Assert.Equal("3.2.1", evt.SourceAppVersion);
        Assert.Equal("CART_LOAD_FAILED", evt.Code);
        Assert.Equal(occurredAt, evt.OccurredAt);
    }
}
