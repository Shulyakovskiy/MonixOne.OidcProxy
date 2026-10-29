using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using WireMock.Types;
using WireMock.Util;

namespace MonixOne.OidcProxy.Yarp.Tests;

/// <summary>
/// Provides HTTPS OIDC failure responses through WireMock. Real OpenIddict tests
/// separately verify protocol interoperability; this fixture controls fault windows.
/// </summary>
internal sealed class OidcWireMock : IDisposable
{
    internal const string ClientId = "test-bff";
    internal const string ClientSecret = "test-only-client-secret";
    private readonly InfrastructureFixture _infrastructure;
    private readonly ConcurrentDictionary<string, (string Nonce, string Challenge)> _codes = new();
    private readonly ConcurrentDictionary<string, string> _refreshTokens = new();
    private readonly List<RSA> _keys = [];
    private RsaSecurityKey _key;
    internal WireMockServer Server { get; }
    internal Uri Origin { get; }
    internal int RefreshRequests;
    internal int CodeRequests;
    internal string? TokenFailure { get; set; }
    internal string? InvalidIdToken { get; set; }
    internal bool EncryptIdentityTokens { get; set; }
    internal bool OmitRefreshIdentityToken { get; set; }
    internal bool OmitRotatedRefreshToken { get; set; }
    internal SymmetricSecurityKey EncryptionKey { get; } = new(RandomNumberGenerator.GetBytes(32));
    internal string LastAccessToken = string.Empty;
    internal string LastRefreshToken = string.Empty;
    internal TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource RevocationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource? ReleaseRefresh { get; set; }
    internal ConcurrentQueue<string> RevokedTokens { get; } = new();
    internal ConcurrentQueue<IRequestMessage> BackendRequests { get; } = new();
    internal int ActiveRefreshTokens => _refreshTokens.Count;

    internal OidcWireMock(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
        var key = RSA.Create(2048);
        _keys.Add(key);
        _key = new RsaSecurityKey(key) { KeyId = "initial-key" };
        Server = WireMockServer.Start(new WireMockServerSettings
        {
            UseSSL = true,
            CertificateSettings = new WireMockCertificateSettings { X509Certificate = infrastructure.Certificate }
        });
        Origin = new Uri(Server.Urls[0]);
        Server.Given(Request.Create().WithPath("/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create().WithCallback(_ => Json(new
            {
                issuer = Origin.AbsoluteUri.TrimEnd('/'),
                authorization_endpoint = Url("/authorize"),
                token_endpoint = Url("/token"),
                jwks_uri = Url("/jwks"),
                end_session_endpoint = Url("/logout"),
                revocation_endpoint = Url("/revoke"),
                response_types_supported = new[] { "code" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post" }
            })));
        Server.Given(Request.Create().WithPath("/jwks").UsingGet()).RespondWith(Response.Create().WithCallback(_ =>
            Json(new
            {
                keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(
                new RsaSecurityKey(_key.Rsa.ExportParameters(false)) { KeyId = _key.KeyId }) }
            })));
        Server.Given(Request.Create().WithPath("/authorize").UsingGet()).RespondWith(Response.Create().WithCallback(request =>
        {
            var query = QueryHelpers.ParseQuery(new Uri(request.Url).Query);
            var code = Guid.NewGuid().ToString("N"); // Unique one-use credentials, not random business test data.
            _codes[code] = (query["nonce"].ToString(), query["code_challenge"].ToString());
            var response = new ResponseMessage { StatusCode = 302 };
            response.AddHeader("Location", QueryHelpers.AddQueryString(query["redirect_uri"].ToString(),
                new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"].ToString() }));
            return response;
        }));
        Server.Given(Request.Create().WithPath("/token").UsingPost()).RespondWith(Response.Create().WithCallback(ExchangeAsync));
        Server.Given(Request.Create().WithPath("/revoke").UsingPost()).RespondWith(Response.Create().WithCallback(request =>
        {
            var token = QueryHelpers.ParseQuery(request.Body ?? string.Empty)["token"].ToString();
            RevokedTokens.Enqueue(token);
            _refreshTokens.TryRemove(token, out _);
            RevocationCompleted.TrySetResult();
            return Json(new { });
        }));
        Server.Given(Request.Create().WithPath("/api/*")).RespondWith(Response.Create().WithCallback(request =>
        {
            BackendRequests.Enqueue(request);
            var response = Json(new { service = "resource-api", subject = "user-1", environment = "Testing", utc = "2026-01-01T00:00:00Z" });
            response.AddHeader("Set-Cookie", "downstream=must-not-reach-browser; Path=/");
            return response;
        }));
    }

    internal HttpClient CreateIdentityBrowser() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == _infrastructure.Certificate.Thumbprint
    })
    { BaseAddress = Origin };

    internal BffApplicationFactory CreateBff(string? keyNamespace = null, StackExchange.Redis.ConnectionMultiplexer? connection = null)
        => new(_infrastructure, Origin, ClientId, ClientSecret, Origin, keyNamespace, connection);

    internal void RotateSigningKey()
    {
        var rsa = RSA.Create(2048);
        _keys.Add(rsa);
        _key = new RsaSecurityKey(rsa) { KeyId = "rotated-key" };
    }

    private string Url(string path) => new Uri(Origin, path).AbsoluteUri;

    private async Task<IResponseMessage> ExchangeAsync(IRequestMessage request)
    {
        var form = QueryHelpers.ParseQuery(request.Body ?? string.Empty);
        if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret)
            return Json(new { error = "invalid_client" }, 400);
        var refreshing = form["grant_type"] == "refresh_token";
        string? nonce;
        if (refreshing)
        {
            Interlocked.Increment(ref RefreshRequests);
            if (TokenFailure is not null)
            {
                RefreshStarted.TrySetResult();
                return TokenFailure switch
                {
                    "429" => Json(new { error = "slow_down" }, 429),
                    "503" => Json(new { error = "temporarily_unavailable" }, 503),
                    "500" => Json(new { error = "server_error" }, 500),
                    "malformed" => new ResponseMessage { StatusCode = 200, BodyData = new BodyData { BodyAsString = "{invalid-json", DetectedBodyType = BodyType.String } },
                    _ => Json(new { error = "invalid_grant" }, 400)
                };
            }
            if (!_refreshTokens.TryRemove(form["refresh_token"].ToString(), out nonce))
                return Json(new { error = "invalid_grant" }, 400);
        }
        else
        {
            Interlocked.Increment(ref CodeRequests);
            if (!_codes.TryRemove(form["code"].ToString(), out var code) ||
                code.Challenge != Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))))
                return Json(new { error = "invalid_grant" }, 400);
            nonce = code.Nonce;
        }
        LastAccessToken = "opaque-access-" + Guid.NewGuid().ToString("N");
        LastRefreshToken = refreshing && OmitRotatedRefreshToken ? form["refresh_token"].ToString() : Guid.NewGuid().ToString("N");
        _refreshTokens[LastRefreshToken] = nonce!;
        var response = Json(new
        {
            access_token = LastAccessToken,
            refresh_token = refreshing && OmitRotatedRefreshToken ? null : LastRefreshToken,
            token_type = "Bearer",
            expires_in = 120,
            id_token = refreshing && OmitRefreshIdentityToken ? null : CreateIdToken(nonce!)
        });
        if (refreshing)
        {
            RefreshStarted.TrySetResult();
            if (ReleaseRefresh is not null) await ReleaseRefresh.Task;
        }
        return response;
    }

    private string CreateIdToken(string nonce)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = InvalidIdToken == "issuer" ? "https://wrong.example" : Origin.AbsoluteUri.TrimEnd('/'),
            Audience = InvalidIdToken == "audience" ? "wrong-client" : ClientId,
            Subject = new ClaimsIdentity([new Claim("sub", InvalidIdToken == "subject" ? "other-user" : "user-1"),
                new Claim("nonce", InvalidIdToken == "nonce" ? "wrong-nonce" : nonce)]),
            Expires = InvalidIdToken == "expired" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddHours(1),
            IssuedAt = DateTime.UtcNow.AddMinutes(-15),
            NotBefore = DateTime.UtcNow.AddMinutes(-15),
            SigningCredentials = InvalidIdToken == "unsigned" ? null : new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            EncryptingCredentials = EncryptIdentityTokens
                ? new EncryptingCredentials(EncryptionKey, SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512) : null
        });

    private static ResponseMessage Json(object value, int status = 200)
    {
        var response = new ResponseMessage
        {
            StatusCode = status,
            BodyData = new BodyData { BodyAsString = JsonSerializer.Serialize(value), DetectedBodyType = BodyType.String }
        };
        response.AddHeader("Content-Type", "application/json");
        return response;
    }

    public void Dispose()
    {
        ReleaseRefresh?.TrySetResult();
        Server.Dispose();
        foreach (var key in _keys) key.Dispose();
    }
}
