using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;
using Xunit;

namespace VisionSupport.Server.Tests;

/// <summary>
/// Pruebas de integración HTTP reales (TestServer real, pipeline de autenticación/autorización
/// real) para /api/auth y /api/users. A diferencia de instanciar un controlador directamente,
/// esto SÍ evalúa [Authorize]/[Authorize(Roles=...)] de verdad — imprescindible para probar que
/// un rol Support recibe 403 al intentar crear usuarios.
/// </summary>
public class AuthApiTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public AuthApiTests(TestApiFactory factory)
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

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokensAndUser()
    {
        var email = UniqueEmail("login-ok");
        await _factory.SeedUserAsync(email, "correct-password-1");

        var response = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "correct-password-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokensResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrEmpty(tokens!.AccessToken));
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
        Assert.Equal(email, tokens.User.Email);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401WithGenericMessage()
    {
        var email = UniqueEmail("login-badpw");
        await _factory.SeedUserAsync(email, "correct-password-1");

        var response = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Email o contraseña incorrectos", body);
    }

    [Fact]
    public async Task Login_WithNonExistentEmail_ReturnsSameGenericMessageAsWrongPassword()
    {
        var existingEmail = UniqueEmail("login-exists");
        await _factory.SeedUserAsync(existingEmail, "correct-password-1");

        var wrongPasswordResponse = await _factory.Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(existingEmail, "wrong-password"));
        var nonExistentResponse = await _factory.Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(UniqueEmail("never-registered"), "whatever"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, nonExistentResponse.StatusCode);
        Assert.Equal(
            await wrongPasswordResponse.Content.ReadAsStringAsync(),
            await nonExistentResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_WithInactiveUser_ReturnsUnauthorized()
    {
        var email = UniqueEmail("login-inactive");
        await _factory.SeedUserAsync(email, "correct-password-1", isActive: false);

        var response = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "correct-password-1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _factory.Client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithValidToken_ReturnsCurrentUser()
    {
        var email = UniqueEmail("me-ok");
        await _factory.SeedUserAsync(email, "correct-password-1");
        var login = await LoginAsync(email, "correct-password-1");

        var response = await _factory.Client.SendAsync(Authorized(HttpMethod.Get, "/api/auth/me", login.AccessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal(email, me!.Email);
    }

    [Fact]
    public async Task Refresh_RotatesToken_OldOneBecomesUnusable()
    {
        var email = UniqueEmail("refresh-rotate");
        await _factory.SeedUserAsync(email, "correct-password-1");
        var login = await LoginAsync(email, "correct-password-1");

        var refreshResponse = await _factory.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var rotated = await refreshResponse.Content.ReadFromJsonAsync<AuthTokensResponse>();

        // El refresh token SIEMPRE es distinto (bytes aleatorios). El access token puede coincidir
        // si login+refresh caen en el mismo segundo (el claim "exp" de JWT no tiene sub-segundos y
        // el resto de claims son idénticos) — eso no es un problema de seguridad, así que no se
        // afirma su unicidad aquí, solo la del refresh token (lo que sí importa para la rotación).
        Assert.NotEqual(login.RefreshToken, rotated!.RefreshToken);

        // El refresh token viejo (ya rotado) no debe volver a servir.
        var reuseOldResponse = await _factory.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuseOldResponse.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReusingRevokedToken_AlsoRevokesTheNewlyRotatedOne()
    {
        var email = UniqueEmail("refresh-reuse");
        await _factory.SeedUserAsync(email, "correct-password-1");
        var login = await LoginAsync(email, "correct-password-1");

        var rotatedResponse = await _factory.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken));
        var rotated = await rotatedResponse.Content.ReadFromJsonAsync<AuthTokensResponse>();

        // Reutilizar el token viejo (ya revocado) es la señal de robo: debe tumbar TODA la familia,
        // incluido el que se acaba de emitir en la rotación anterior.
        await _factory.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken));

        var tryNewOne = await _factory.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(rotated!.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, tryNewOne.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var email = UniqueEmail("logout-ok");
        await _factory.SeedUserAsync(email, "correct-password-1");
        var login = await LoginAsync(email, "correct-password-1");

        var logoutResponse = await _factory.Client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var refreshAfterLogout = await _factory.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithUnknownToken_IsIdempotentNoContent()
    {
        var response = await _factory.Client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest("no-existe-este-token"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_AsSupportRole_IsForbidden()
    {
        var email = UniqueEmail("support-denied");
        await _factory.SeedUserAsync(email, "correct-password-1", role: UserRole.Support);
        var login = await LoginAsync(email, "correct-password-1");

        var request = Authorized(HttpMethod.Post, "/api/users", login.AccessToken);
        request.Content = JsonContent.Create(new CreateUserRequest(UniqueEmail("victim"), "Nuevo", "Support", "password123"));

        var response = await _factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_WithoutToken_IsUnauthorized()
    {
        var response = await _factory.Client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_AsAdmin_Succeeds_AndNeverReturnsPasswordHash()
    {
        var adminEmail = UniqueEmail("admin-creator");
        await _factory.SeedUserAsync(adminEmail, "correct-password-1", role: UserRole.Admin);
        var login = await LoginAsync(adminEmail, "correct-password-1");

        var newUserEmail = UniqueEmail("created-by-admin");
        var request = Authorized(HttpMethod.Post, "/api/users", login.AccessToken);
        request.Content = JsonContent.Create(new CreateUserRequest(newUserEmail, "Técnico Nuevo", "Support", "password123"));

        var response = await _factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);

        var created = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal(newUserEmail, created!.Email);
        Assert.Equal("Support", created.Role);
        Assert.True(created.IsActive);
    }

    [Fact]
    public async Task DeactivateUser_RevokesTheirRefreshTokens()
    {
        var adminEmail = UniqueEmail("admin-deactivator");
        await _factory.SeedUserAsync(adminEmail, "correct-password-1", role: UserRole.Admin);
        var adminLogin = await LoginAsync(adminEmail, "correct-password-1");

        var victimEmail = UniqueEmail("about-to-be-deactivated");
        var victim = await _factory.SeedUserAsync(victimEmail, "correct-password-1");
        var victimLogin = await LoginAsync(victimEmail, "correct-password-1");

        var patchRequest = Authorized(HttpMethod.Patch, $"/api/users/{victim.Id}", adminLogin.AccessToken);
        patchRequest.Content = JsonContent.Create(new UpdateUserRequest(null, null, false, null));
        var patchResponse = await _factory.Client.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var refreshAttempt = await _factory.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(victimLogin.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAttempt.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithDuplicateEmail_ReturnsConflict()
    {
        var adminEmail = UniqueEmail("admin-dup");
        await _factory.SeedUserAsync(adminEmail, "correct-password-1", role: UserRole.Admin);
        var login = await LoginAsync(adminEmail, "correct-password-1");

        var duplicateEmail = UniqueEmail("duplicate-target");
        await _factory.SeedUserAsync(duplicateEmail, "irrelevant-password");

        var request = Authorized(HttpMethod.Post, "/api/users", login.AccessToken);
        request.Content = JsonContent.Create(new CreateUserRequest(duplicateEmail, "Otro", "Support", "password123"));

        var response = await _factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithInvalidRole_ReturnsBadRequest()
    {
        var adminEmail = UniqueEmail("admin-badrole");
        await _factory.SeedUserAsync(adminEmail, "correct-password-1", role: UserRole.Admin);
        var login = await LoginAsync(adminEmail, "correct-password-1");

        var request = Authorized(HttpMethod.Post, "/api/users", login.AccessToken);
        request.Content = JsonContent.Create(new CreateUserRequest(UniqueEmail("x"), "X", "SuperUser", "password123"));

        var response = await _factory.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<AuthTokensResponse> LoginAsync(string email, string password)
    {
        var response = await _factory.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokensResponse>();
        return tokens!;
    }
}
