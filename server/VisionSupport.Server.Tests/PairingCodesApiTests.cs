using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;
using Xunit;

namespace VisionSupport.Server.Tests;

/// <summary>
/// Pruebas de integración HTTP reales para /api/pairing-codes: solo Admin puede crear/listar/
/// revocar, el código en texto plano solo se entrega en la creación, y un código ya usado no
/// puede revocarse.
/// </summary>
public class PairingCodesApiTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public PairingCodesApiTests(TestApiFactory factory)
    {
        _factory = factory;
    }

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@test.local";

    private HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<(User Admin, string AccessToken)> SeedAdminAndLoginAsync(string label)
    {
        var email = UniqueEmail(label);
        var admin = await _factory.SeedUserAsync(email, "correct-password-1", role: UserRole.Admin);
        var loginResponse = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "correct-password-1"));
        loginResponse.EnsureSuccessStatusCode();
        var tokens = await loginResponse.Content.ReadFromJsonAsync<AuthTokensResponse>();
        return (admin, tokens!.AccessToken);
    }

    [Fact]
    public async Task CreatePairingCode_AsAdmin_ReturnsCodeOnce()
    {
        var (_, accessToken) = await SeedAdminAndLoginAsync("pc-create");

        var request = Authorized(HttpMethod.Post, "/api/pairing-codes", accessToken);
        request.Content = JsonContent.Create(new CreatePairingCodeRequest("Portátil de recepción"));
        var response = await _factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreatePairingCodeResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Code));
        Assert.Equal("Portátil de recepción", body.Label);
        Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(body.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(5));
    }

    [Fact]
    public async Task CreatePairingCode_AsSupportRole_IsForbidden()
    {
        var email = UniqueEmail("pc-support");
        await _factory.SeedUserAsync(email, "correct-password-1", role: UserRole.Support);
        var loginResponse = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "correct-password-1"));
        var tokens = await loginResponse.Content.ReadFromJsonAsync<AuthTokensResponse>();

        var request = Authorized(HttpMethod.Post, "/api/pairing-codes", tokens!.AccessToken);
        request.Content = JsonContent.Create(new CreatePairingCodeRequest(null));
        var response = await _factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetPairingCodes_AsSupportRole_IsForbidden()
    {
        var email = UniqueEmail("pc-list-support");
        await _factory.SeedUserAsync(email, "correct-password-1", role: UserRole.Support);
        var loginResponse = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "correct-password-1"));
        var tokens = await loginResponse.Content.ReadFromJsonAsync<AuthTokensResponse>();

        var response = await _factory.Client.SendAsync(Authorized(HttpMethod.Get, "/api/pairing-codes", tokens!.AccessToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetPairingCodes_NeverExposesCodeValue()
    {
        var (admin, accessToken) = await SeedAdminAndLoginAsync("pc-noleak");
        await _factory.SeedPairingCodeAsync(admin.Id, label: "no-debe-aparecer-en-texto-plano");

        var response = await _factory.Client.SendAsync(Authorized(HttpMethod.Get, "/api/pairing-codes", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("codeHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"code\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RevokePairingCode_WhenActive_MarksAsRevoked()
    {
        var (admin, accessToken) = await SeedAdminAndLoginAsync("pc-revoke");
        var (code, _) = await _factory.SeedPairingCodeAsync(admin.Id);

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/pairing-codes/{code.Id}/revoke", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PairingCodeDto>();
        Assert.Equal("Revoked", dto!.Status);
    }

    [Fact]
    public async Task RevokePairingCode_WhenAlreadyUsed_ReturnsConflict()
    {
        var (admin, accessToken) = await SeedAdminAndLoginAsync("pc-revoke-used");
        var (code, _) = await _factory.SeedPairingCodeAsync(admin.Id, usedAt: DateTimeOffset.UtcNow);

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/pairing-codes/{code.Id}/revoke", accessToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RevokePairingCode_ThatDoesNotExist_ReturnsNotFound()
    {
        var (_, accessToken) = await SeedAdminAndLoginAsync("pc-revoke-404");

        var response = await _factory.Client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/pairing-codes/{Guid.NewGuid()}/revoke", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
