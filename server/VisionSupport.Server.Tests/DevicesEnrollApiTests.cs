using System.Net;
using System.Net.Http.Json;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;
using Xunit;

namespace VisionSupport.Server.Tests;

/// <summary>
/// Pruebas de integración HTTP reales para POST /api/devices/enroll: código válido, expirado,
/// revocado, reutilizado, intento concurrente (solo uno gana la carrera) y límite de tasa por IP.
/// No requiere autenticación (lo llama Android, que nunca tiene JWT), pero sí un código de
/// emparejamiento válido — ya no existe alta automática de dispositivos.
///
/// A diferencia de AuthApiTests/PairingCodesApiTests (que comparten un TestApiFactory vía
/// IClassFixture), aquí cada test crea su propia instancia: el rate limiter por IP es estado
/// compartido dentro de un mismo host de prueba, y varios tests de enroll en la misma ventana de
/// 1 minuto se pisarían entre sí (o dispararían un 429 inesperado) si compartieran factory.
/// </summary>
public class DevicesEnrollApiTests : IAsyncLifetime
{
    private readonly TestApiFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();
    public Task DisposeAsync() => _factory.DisposeAsync();

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@test.local";
    private static string UniqueDeviceCode(string label) => $"dev-{label}-{Guid.NewGuid():N}";

    private async Task<User> SeedAdminAsync(string label) =>
        await _factory.SeedUserAsync(UniqueEmail(label), "correct-password-1", role: UserRole.Admin);

    [Fact]
    public async Task Enroll_WithValidCode_CreatesDeviceAndReturnsPairingKey()
    {
        var admin = await SeedAdminAsync("enroll-ok");
        var (_, plainCode) = await _factory.SeedPairingCodeAsync(admin.Id);
        var deviceCode = UniqueDeviceCode("ok");

        var response = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest(plainCode, deviceCode, "Teléfono de prueba"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EnrollDeviceResponse>();
        Assert.Equal(deviceCode, body!.DeviceCode);
        Assert.False(string.IsNullOrWhiteSpace(body.PairingKey));
    }

    [Fact]
    public async Task Enroll_AcceptsCodeRegardlessOfDashesOrCase()
    {
        var admin = await SeedAdminAsync("enroll-normalize");
        var (_, plainCode) = await _factory.SeedPairingCodeAsync(admin.Id);
        // Simula cómo Android/el humano podría escribirlo: en minúsculas y con el guion de
        // display que PairingCodeService.FormatForDisplay añadiría en el panel web.
        var typedCode = $"{plainCode[..5]}-{plainCode[5..]}".ToLowerInvariant();

        var response = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest(typedCode, UniqueDeviceCode("norm"), "Teléfono"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Enroll_WithUnknownCode_ReturnsInvalidError()
    {
        var response = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest("NOEXISTE123", UniqueDeviceCode("invalid"), "Teléfono"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<EnrollErrorResponse>();
        Assert.Equal("invalid", error!.ErrorCode);
    }

    [Fact]
    public async Task Enroll_WithExpiredCode_ReturnsExpiredError()
    {
        var admin = await SeedAdminAsync("enroll-expired");
        var (_, plainCode) = await _factory.SeedPairingCodeAsync(admin.Id, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest(plainCode, UniqueDeviceCode("expired"), "Teléfono"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<EnrollErrorResponse>();
        Assert.Equal("expired", error!.ErrorCode);
    }

    [Fact]
    public async Task Enroll_WithRevokedCode_ReturnsRevokedError()
    {
        var admin = await SeedAdminAsync("enroll-revoked");
        var (_, plainCode) = await _factory.SeedPairingCodeAsync(admin.Id, revokedAt: DateTimeOffset.UtcNow);

        var response = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest(plainCode, UniqueDeviceCode("revoked"), "Teléfono"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<EnrollErrorResponse>();
        Assert.Equal("revoked", error!.ErrorCode);
    }

    [Fact]
    public async Task Enroll_WithAlreadyUsedCode_ReturnsUsedError()
    {
        var admin = await SeedAdminAsync("enroll-used");
        var (_, plainCode) = await _factory.SeedPairingCodeAsync(admin.Id);

        var first = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest(plainCode, UniqueDeviceCode("used-1"), "Teléfono"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest(plainCode, UniqueDeviceCode("used-2"), "Otro teléfono"));

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<EnrollErrorResponse>();
        Assert.Equal("used", error!.ErrorCode);
    }

    [Fact]
    public async Task Enroll_WithSameDeviceCodeTwice_RotatesPairingKeyInsteadOfDuplicating()
    {
        var admin = await SeedAdminAsync("enroll-repair");
        var deviceCode = UniqueDeviceCode("repair");

        var (_, firstCode) = await _factory.SeedPairingCodeAsync(admin.Id);
        var firstResponse = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest(firstCode, deviceCode, "Teléfono"));
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<EnrollDeviceResponse>();

        var (_, secondCode) = await _factory.SeedPairingCodeAsync(admin.Id);
        var secondResponse = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest(secondCode, deviceCode, "Teléfono reinstalado"));
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<EnrollDeviceResponse>();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(firstBody!.DeviceId, secondBody!.DeviceId);
        Assert.NotEqual(firstBody.PairingKey, secondBody.PairingKey);
    }

    [Fact]
    public async Task Enroll_ConcurrentRequestsWithSameCode_OnlyOneSucceeds()
    {
        var admin = await SeedAdminAsync("enroll-concurrent");
        var (_, plainCode) = await _factory.SeedPairingCodeAsync(admin.Id);

        // Por debajo de TestApiFactory.TestPairingEnrollRateLimit a propósito: esta prueba busca
        // el resultado de la condición de carrera sobre el código, no la del rate limiter.
        var requests = Enumerable.Range(0, 6)
            .Select(i => _factory.Client.PostAsJsonAsync(
                "/api/devices/enroll", new EnrollDeviceRequest(plainCode, UniqueDeviceCode($"race-{i}"), "Teléfono")))
            .ToArray();
        var responses = await Task.WhenAll(requests);

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Enroll_MissingFields_ReturnsInvalidError()
    {
        var response = await _factory.Client.PostAsJsonAsync(
            "/api/devices/enroll", new EnrollDeviceRequest("", "", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<EnrollErrorResponse>();
        Assert.Equal("invalid", error!.ErrorCode);
    }

    [Fact]
    public async Task Enroll_ExceedingRateLimitPerIp_Returns429()
    {
        // TestApiFactory configura el límite de prueba en TestApiFactory.TestPairingEnrollRateLimit
        // (bajo a propósito). El TestServer no distingue IPs entre peticiones del mismo cliente,
        // así que todas caen en la misma partición ("unknown"/loopback) — perfecto para esta prueba.
        HttpResponseMessage? lastResponse = null;
        for (var i = 0; i < TestApiFactory.TestPairingEnrollRateLimit + 2; i++)
        {
            lastResponse = await _factory.Client.PostAsJsonAsync(
                "/api/devices/enroll", new EnrollDeviceRequest("CODIGO-INVALIDO", UniqueDeviceCode($"rl-{i}"), "Teléfono"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse!.StatusCode);
    }
}
