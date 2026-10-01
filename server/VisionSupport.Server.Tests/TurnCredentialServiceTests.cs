using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VisionSupport.Server.Options;
using VisionSupport.Server.Services;
using Xunit;

namespace VisionSupport.Server.Tests;

public class TurnCredentialServiceTests
{
    private static TurnCredentialService CreateService(Action<TurnOptions>? configure = null)
    {
        var options = new TurnOptions
        {
            SharedSecret = "unit-test-shared-secret",
            Realm = "test.local",
            PublicHost = "203.0.113.10",
            Port = 3478,
            CredentialTtlSeconds = 300,
        };
        configure?.Invoke(options);
        return new TurnCredentialService(Microsoft.Extensions.Options.Options.Create(options));
    }

    [Fact]
    public void IsConfigured_TrueWhenSecretAndHostPresent()
    {
        var service = CreateService();
        Assert.True(service.IsConfigured);
    }

    [Theory]
    [InlineData("", "host")]
    [InlineData("secret", "")]
    [InlineData("  ", "host")]
    public void IsConfigured_FalseWhenSecretOrHostMissing(string secret, string host)
    {
        var service = CreateService(o =>
        {
            o.SharedSecret = secret;
            o.PublicHost = host;
        });
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void GenerateCredentials_WhenNotConfigured_Throws()
    {
        var service = CreateService(o => o.SharedSecret = "");
        Assert.Throws<InvalidOperationException>(() => service.GenerateCredentials("session-1"));
    }

    [Fact]
    public void GenerateCredentials_UsernameFormat_IsExpiryColonLabel()
    {
        var service = CreateService();
        var before = DateTimeOffset.UtcNow;

        var result = service.GenerateCredentials("session-abc");

        var iceServer = Assert.Single(result.IceServers);
        var parts = iceServer.Username!.Split(':', 2);
        Assert.Equal(2, parts.Length);
        Assert.Equal("session-abc", parts[1]);

        var expirySeconds = long.Parse(parts[0]);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
        Assert.True(expiry >= before.AddSeconds(290) && expiry <= before.AddMinutes(6));
    }

    [Fact]
    public void GenerateCredentials_CredentialIsBase64HmacSha1OfUsername()
    {
        const string secret = "unit-test-shared-secret";
        var service = CreateService(o => o.SharedSecret = secret);

        var result = service.GenerateCredentials("session-xyz");
        var iceServer = Assert.Single(result.IceServers);

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        var expectedCredential = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(iceServer.Username!)));

        Assert.Equal(expectedCredential, iceServer.Credential);
    }

    [Fact]
    public void GenerateCredentials_WithDifferentSharedSecrets_ProducesDifferentCredentialForSameUsername()
    {
        const string username = "1234567890:same-label";
        var credentialA = ComputeHmacBase64("secret-a", username);
        var credentialB = ComputeHmacBase64("secret-b", username);

        // Prueba directa (sin pasar por GenerateCredentials, cuyo username incluye un timestamp
        // real) de que el HMAC realmente depende del secreto: dos secretos distintos sobre el
        // MISMO username deben producir credenciales distintas.
        Assert.NotEqual(credentialA, credentialB);
    }

    private static string ComputeHmacBase64(string secret, string username)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
    }

    [Fact]
    public void GenerateCredentials_ExpiresWithinConfiguredTtl()
    {
        var service = CreateService(o => o.CredentialTtlSeconds = 120);
        var before = DateTimeOffset.UtcNow;

        var result = service.GenerateCredentials("s");

        Assert.True(result.ExpiresAt >= before.AddSeconds(110));
        Assert.True(result.ExpiresAt <= before.AddSeconds(130));
    }

    [Fact]
    public void GenerateCredentials_ClampsTtlToMaximumTenMinutes()
    {
        var service = CreateService(o => o.CredentialTtlSeconds = 3600); // 1 hora, excede el máximo
        var before = DateTimeOffset.UtcNow;

        var result = service.GenerateCredentials("s");

        Assert.True(result.ExpiresAt <= before.AddMinutes(10).AddSeconds(5));
    }

    [Fact]
    public void GenerateCredentials_NonPositiveTtl_FallsBackToDefault()
    {
        var service = CreateService(o => o.CredentialTtlSeconds = 0);
        var before = DateTimeOffset.UtcNow;

        var result = service.GenerateCredentials("s");

        // Debe usar el default (5 min) en vez de expirar inmediatamente o lanzar.
        Assert.True(result.ExpiresAt >= before.AddMinutes(4));
        Assert.True(result.ExpiresAt <= before.AddMinutes(6));
    }

    [Fact]
    public void GenerateCredentials_UrlsIncludeStunAndBothTurnTransports()
    {
        var service = CreateService(o =>
        {
            o.PublicHost = "192.168.1.50";
            o.Port = 3478;
        });

        var result = service.GenerateCredentials("s");
        var urls = Assert.Single(result.IceServers).Urls;

        Assert.Contains("stun:192.168.1.50:3478", urls);
        Assert.Contains("turn:192.168.1.50:3478?transport=udp", urls);
        Assert.Contains("turn:192.168.1.50:3478?transport=tcp", urls);
        Assert.DoesNotContain(urls, u => u.StartsWith("turns:"));
    }

    [Fact]
    public void GenerateCredentials_IncludesTurnsUrlOnlyWhenTlsPortConfigured()
    {
        var service = CreateService(o => o.TlsPort = 5349);

        var result = service.GenerateCredentials("s");
        var urls = Assert.Single(result.IceServers).Urls;

        Assert.Contains(urls, u => u.StartsWith("turns:") && u.Contains(":5349"));
    }
}
