using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Jose;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class EncryptionKeyTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Startup_MissingEncryptionKey_RejectsBeforeRequests(string? encryptionKey)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.EncryptionKey = encryptionKey;

        // Act
        var failure = Should.Throw<OptionsValidationException>(() => host.CreateBrowser());

        // Assert
        failure.Message.ShouldContain("Bff:EncryptionKey");
        identity.CodeRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Login_SessionDataIsJwe_DecryptsDirectlyWithTheConfigurationKey()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(provider);
        var key = SessionKey(host.KeyNamespace);
        var encrypted = (string)(await infrastructure.Connection.GetDatabase().StringGetAsync(key))!;
        var json = JWT.Decrypt(encrypted, infrastructure.EncryptionKey,
            JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM);
        var payload = JsonNode.Parse(json)!;
        var ticket = TicketSerializer.Default.Deserialize(Convert.FromBase64String(payload["Ticket"]!.GetValue<string>()))!;

        // Assert: the Redis session needs no Data Protection APIs or key ring to decrypt.
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        payload["SessionKey"]!.GetValue<string>().ShouldBe(key.ToString());
        JWT.Headers(encrypted)["alg"].ShouldBe("PBES2-HS256+A128KW");
        JWT.Headers(encrypted)["enc"].ShouldBe("A256GCM");
        (ticket.Properties.GetTokenValue("access_token") == identity.LastAccessToken).ShouldBeTrue();
        encrypted.Contains(identity.LastAccessToken!, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task Login_FrameworkKeysStayEncryptedInRedis_WithoutAFileSystemRepository()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(provider);
        var entries = await infrastructure.Connection.GetDatabase()
            .ListRangeAsync("test-spa:testing:" + host.KeyNamespace + ":data-protection");

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        entries.ShouldNotBeEmpty();
        foreach (var entry in entries)
        {
            var xml = XElement.Parse(entry.ToString());
            xml.Descendants("masterKey").ShouldBeEmpty();
            var decrypted = JWT.Decrypt(xml.Descendants("jwe").Single().Value, infrastructure.EncryptionKey,
                JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM);
            XElement.Parse(decrypted).Name.LocalName.ShouldBe("masterKey");
        }
    }

    [Fact]
    public async Task Proxy_AfterHostRestart_UsesTheSameSessionAndCookieFromRedis()
    {
        // Arrange: the setting is a string password, not a raw AES key.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        var scope = Guid.NewGuid().ToString("N");
        var password = "PBES2 test secret: " + Guid.NewGuid().ToString("N");
        string cookie;
        await using (var first = identity.CreateBff(scope))
        {
            first.EncryptionKey = password;
            using var browser = first.CreateBrowser();
            using var provider = identity.CreateIdentityBrowser();
            using var login = await browser.LoginAsync(provider);
            cookie = SessionCookie(login);
        }
        await using var restarted = identity.CreateBff(scope);
        restarted.EncryptionKey = password;
        using var other = restarted.CreateBrowser(handleCookies: false);
        other.DefaultRequestHeaders.Add("Cookie", cookie);

        // Act
        using var response = await other.ApiAsync(cancellationToken: ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.BackendRequests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("algorithm")]
    [InlineData("wrong-key")]
    [InlineData("invalid-json")]
    [InlineData("missing-binding")]
    public async Task Proxy_InvalidJweSession_RejectsAndRemovesTheRecord(string defect)
    {
        // Arrange: the cookie remains valid; only the Redis session changes.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        var database = infrastructure.Connection.GetDatabase();
        var key = SessionKey(host.KeyNamespace);
        var encrypted = (string)(await database.StringGetAsync(key))!;
        var parts = encrypted.Split('.');
        if (defect == "ciphertext")
        {
            var ciphertext = Base64UrlEncoder.DecodeBytes(parts[3]);
            ciphertext[0] ^= 1;
            parts[3] = Base64UrlEncoder.Encode(ciphertext);
            encrypted = string.Join('.', parts);
        }
        else if (defect == "algorithm")
        {
            parts[0] = Base64UrlEncoder.Encode(Base64UrlEncoder.Decode(parts[0])
                .Replace("A256GCM", "A128GCM", StringComparison.Ordinal));
            encrypted = string.Join('.', parts);
        }
        else
        {
            var json = JWT.Decrypt(encrypted, infrastructure.EncryptionKey,
                JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM);
            if (defect == "invalid-json") json = "{";
            if (defect == "missing-binding")
            {
                var payload = JsonNode.Parse(json)!;
                payload.AsObject().Remove("SessionKey");
                json = payload.ToJsonString();
            }
            encrypted = JWT.Encode(json, defect == "wrong-key" ? "a different test secret" : infrastructure.EncryptionKey,
                JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM);
        }
        await database.StringSetAsync(key, encrypted);

        // Act
        using var response = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await database.KeyExistsAsync(key)).ShouldBeFalse();
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("session")]
    [InlineData("client")]
    [InlineData("namespace")]
    public async Task Proxy_JweCopiedToAnotherSession_RejectsWithoutAffectingTheOriginal(string boundary)
    {
        // Arrange: valid encryption must not allow moving a record between identities or clients.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var source = identity.CreateBff();
        if (boundary == "client") source.AddClient("second", new Uri("https://second.example"), identity);
        await using var separate = identity.CreateBff();
        var target = boundary == "namespace" ? separate : source;
        using var original = source.CreateBrowser();
        using var victim = target.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var firstLogin = await original.LoginAsync(provider);
        var sourceKey = SessionKey(source.KeyNamespace);
        using var secondLogin = await victim.LoginAsync(provider,
            origin: boundary == "client" ? new Uri("https://second.example") : null);
        var targetKey = SessionKeys(target.KeyNamespace, boundary == "client" ? "second" : "spa").Single(key => key != sourceKey);
        var database = infrastructure.Connection.GetDatabase();
        await database.StringSetAsync(targetKey, await database.StringGetAsync(sourceKey));

        // Act
        using var rejected = await victim.ApiAsync(boundary == "client" ? "https://second.example/api/status" : "/api/status", ct);
        using var accepted = await original.ApiAsync(cancellationToken: ct);

        // Assert
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await database.KeyExistsAsync(targetKey)).ShouldBeFalse();
        (await database.KeyExistsAsync(sourceKey)).ShouldBeTrue();
        identity.BackendRequests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Startup_UnprotectedLegacyFrameworkKeys_RequiresAnExplicitReset()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().PersistKeysToStackExchangeRedis(infrastructure.Connection,
            "test-spa:testing:" + host.KeyNamespace + ":data-protection");
        using var legacy = services.BuildServiceProvider();
        legacy.GetRequiredService<IDataProtectionProvider>().CreateProtector("legacy").Protect("old-cookie");

        // Act
        var failure = Should.Throw<OptionsValidationException>(() => host.CreateBrowser());

        // Assert
        failure.Message.ShouldContain("KeyNamespace");
        identity.CodeRequests.ShouldBe(0);
    }

    private RedisKey[] SessionKeys(string scope, string client = "spa") => infrastructure.Connection
        .GetServer(infrastructure.Connection.GetEndPoints().Single())
        .Keys(pattern: "test-spa:testing:" + scope + ":" + client + ":sessions:*").ToArray();

    private RedisKey SessionKey(string scope) => SessionKeys(scope).Single();

    private static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith(OidcProxyDefaults.CookieName("spa") + "=", StringComparison.Ordinal)).Split(';')[0];
}
