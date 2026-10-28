using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using MonixOne.OidcProxy.ModuleInitializers;
using MonixOne.OidcProxy.Sessions;
using StackExchange.Redis;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MonixOne.OidcProxy.Tests;

internal sealed class TestClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    internal void Advance(TimeSpan interval) => Interlocked.Add(ref _ticks, interval.Ticks);
}

internal sealed class FakeIdentityProvider : HttpMessageHandler
{
    internal const string Authority = "https://identity.example";
    internal const string ClientId = "test-bff";
    internal const string ClientSecret = "secret-for-tests-only";
    private RSA _rsa = RSA.Create(2048);
    private readonly List<RSA> _retiredKeys = [];
    private readonly ConcurrentDictionary<string, (string Nonce, string Challenge)> _codes = new();
    private readonly ConcurrentDictionary<string, string> _refreshTokens = new();
    internal RsaSecurityKey Key { get; private set; }
    internal int CodeExchanges;
    internal int RefreshExchanges;
    internal int Revocations;
    internal string? Failure;
    internal bool OmitRefreshToken;
    internal bool OmitIdToken;
    internal string? InvalidIdToken;
    internal string Role = "manager";
    internal TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource? RefreshRelease;
    internal string LastAccessToken = string.Empty;
    internal string LastRefreshToken = string.Empty;
    internal string? LastNonce;

    internal FakeIdentityProvider() => Key = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };

    internal void RotateSigningKey()
    {
        _retiredKeys.Add(_rsa);
        _rsa = RSA.Create(2048);
        Key = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    internal OpenIdConnectConfiguration Configuration()
    {
        var config = new OpenIdConnectConfiguration
        {
            Issuer = Authority,
            AuthorizationEndpoint = Authority + "/authorize",
            TokenEndpoint = Authority + "/token",
            EndSessionEndpoint = Authority + "/logout",
            RevocationEndpoint = Authority + "/revoke"
        };
        config.SigningKeys.Add(Key);
        return config;
    }

    internal string IssueCode(Uri authorize)
    {
        var query = QueryHelpers.ParseQuery(authorize.Query);
        LastNonce = query["nonce"].ToString();
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = (LastNonce, query["code_challenge"].ToString());
        return code;
    }

    internal string SeedRefreshToken(string? nonce = null)
    {
        var token = Guid.NewGuid().ToString("N");
        _refreshTokens[token] = nonce ?? string.Empty;
        return token;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
        if (request.RequestUri!.AbsolutePath == "/revoke")
        {
            Interlocked.Increment(ref Revocations);
            _refreshTokens.TryRemove(body["token"].ToString(), out _);
            return Json(new { });
        }
        var credentials = request.Headers.Authorization;
        var basic = credentials?.Scheme == "Basic" ? Encoding.UTF8.GetString(Convert.FromBase64String(credentials.Parameter!)) : null;
        if (basic != ClientId + ":" + ClientSecret && (body["client_id"] != ClientId || body["client_secret"] != ClientSecret))
            return Json(new { error = "invalid_client" }, HttpStatusCode.BadRequest);

        string? nonce;
        var refreshing = body["grant_type"] == "refresh_token";
        if (refreshing)
        {
            Interlocked.Increment(ref RefreshExchanges);
            if (!_refreshTokens.TryRemove(body["refresh_token"].ToString(), out var refreshState))
                return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            nonce = refreshState.Length == 0 ? null : refreshState;
            RefreshStarted.TrySetResult();
            if (RefreshRelease is not null) await RefreshRelease.Task.WaitAsync(ct);
            if (Failure == "transport") throw new HttpRequestException("Simulated lost token response");
            if (Failure == "invalid_grant") return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            if (Failure == "server_error") return Json(new { error = "server_error" }, HttpStatusCode.ServiceUnavailable);
        }
        else
        {
            Interlocked.Increment(ref CodeExchanges);
            if (!_codes.TryRemove(body["code"].ToString(), out var code))
                return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            if (Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(body["code_verifier"].ToString()))) != code.Challenge)
                return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            nonce = code.Nonce;
        }

        LastAccessToken = "opaque-access-" + Guid.NewGuid().ToString("N");
        LastRefreshToken = SeedRefreshToken(nonce);
        var response = new Dictionary<string, object?>
        {
            ["access_token"] = LastAccessToken,
            ["refresh_token"] = LastRefreshToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = Failure == "zero_expiry" ? 0 : 120,
            ["id_token"] = CreateIdToken(nonce)
        };
        if (OmitRefreshToken)
        {
            response.Remove("refresh_token");
            if (refreshing) _refreshTokens[body["refresh_token"].ToString()] = nonce ?? string.Empty;
        }
        if (OmitIdToken && refreshing) response.Remove("id_token");
        if (Failure == "missing_access") response.Remove("access_token");
        if (Failure == "wrong_type") response["token_type"] = "DPoP";
        return Json(response);
    }

    private string CreateIdToken(string? nonce)
    {
        var claims = new List<Claim>
        {
            new("sub", InvalidIdToken == "subject" ? "another-user" : "user-1"),
            new("name", "Test User"), new("role", Role)
        };
        if (InvalidIdToken == "missing_subject") claims.RemoveAll(c => c.Type == "sub");
        if (InvalidIdToken == "azp") claims.Add(new Claim("azp", "attacker-client"));
        if (nonce is not null && InvalidIdToken != "missing_nonce") claims.Add(new Claim("nonce", InvalidIdToken == "nonce" ? "wrong-nonce" : nonce));
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = InvalidIdToken == "issuer" ? "https://attacker.example" : Authority,
            Audience = InvalidIdToken == "audience" ? "another-client" : ClientId,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = InvalidIdToken == "missing_iat" ? null : now,
            NotBefore = now.AddHours(-1),
            Expires = InvalidIdToken == "expired" ? now.AddMinutes(-10) : now.AddHours(1),
            SigningCredentials = InvalidIdToken == "unsigned" ? null : new SigningCredentials(InvalidIdToken == "signature" ?
                new RsaSecurityKey(RSA.Create(2048)) { KeyId = Key.KeyId } : Key, SecurityAlgorithms.RsaSha256)
        });
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = JsonContent.Create(value) };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _rsa.Dispose();
        if (disposing) foreach (var key in _retiredKeys) key.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class BffHost : IAsyncDisposable
{
    internal static readonly Uri Origin = new("https://app.example");
    internal WebApplication App { get; }
    internal HttpClient Client { get; }
    internal TestClock Clock { get; }
    internal SessionStore Store => App.Services.GetRequiredService<SessionStore>();
    internal TokenSession Tokens => App.Services.GetRequiredService<TokenSession>();

    internal BffHost(WebApplication app, TestClock clock)
    {
        App = app;
        Clock = clock;
        Client = app.GetTestClient();
        Client.BaseAddress = Origin;
    }

    internal static async Task<BffHost> StartAsync(FakeIdentityProvider idp,
        ConnectionMultiplexer? redis = null, string? prefix = null, TestClock? clock = null,
        string project = "test-project", string environment = "test",
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = null, bool injectForeignPrincipal = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        clock ??= new TestClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        var config = new ProxyOptions();
        config.SetCustomHostName(Origin);
        config.SetLandingPage("/home");
        if (redis is not null) config.ConfigureRedisBackBone(redis, project, environment, prefix ?? Guid.NewGuid().ToString("N"));
        config.ConfigureYarp([
            new RouteConfig { RouteId = "api", ClusterId = "api", Match = new RouteMatch { Path = "/api/{**all}" } }
        ], [new ClusterConfig { ClusterId = "api", Destinations = new Dictionary<string, DestinationConfig>
            { ["one"] = new() { Address = "https://api.example/" } } }]);
        builder.Services.AddOidcProxy(config, oidc =>
        {
            oidc.Authority = FakeIdentityProvider.Authority;
            oidc.ClientId = FakeIdentityProvider.ClientId;
            oidc.ClientSecret = FakeIdentityProvider.ClientSecret;
            if (configurationManager is null) oidc.Configuration = idp.Configuration();
            else oidc.ConfigurationManager = configurationManager;
            oidc.Backchannel = new HttpClient(idp, disposeHandler: false);
            oidc.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
        });
        builder.Services.AddSingleton<IForwarderHttpClientFactory, EchoClientFactory>();
        var app = builder.Build();
        if (injectForeignPrincipal)
            app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "foreign-user")], "another-scheme"));
                return next(context);
            });
        app.UseOidcProxy();
        app.MapGet("/local", () => Results.Ok()).AsOidcProxyApi();
        await app.StartAsync();
        return new BffHost(app, clock);
    }

    internal async Task<string> SeedSessionAsync(FakeIdentityProvider idp, TimeSpan? accessLifetime = null)
    {
        var ticket = CreateTicket(idp.SeedRefreshToken(), Clock.GetUtcNow() + (accessLifetime ?? TimeSpan.FromMinutes(5)));
        return await Store.CreateAsync(ticket, CancellationToken.None);
    }

    internal static AuthenticationTicket CreateTicket(string refreshToken, DateTimeOffset expiresAt)
    {
        var properties = new AuthenticationProperties();
        properties.StoreTokens([
            new AuthenticationToken { Name = "access_token", Value = "initial-opaque-access" },
            new AuthenticationToken { Name = "refresh_token", Value = refreshToken },
            new AuthenticationToken { Name = "expires_at", Value = expiresAt.ToString("O", CultureInfo.InvariantCulture) }
        ]);
        return new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "user-1"), new Claim("role", "manager")], BffConstants.CookieScheme)), properties, BffConstants.CookieScheme);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }

    private sealed class EchoClientFactory : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new EchoHandler());
    }

    private sealed class EchoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { token = request.Headers.Authorization?.Parameter,
                    cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(";", cookies) : null })
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "evil=downstream; Path=/");
            response.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=3600");
            return Task.FromResult(response);
        }
    }
}

internal sealed class Browser(BffHost host)
{
    private readonly CookieContainer _cookies = new();
    internal string Cookies => _cookies.GetCookieHeader(BffHost.Origin);

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, bool csrf = true,
        string? origin = null, string? cookies = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? headers = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Content = content;
        request.Headers.TryAddWithoutValidation("Cookie", cookies ?? _cookies.GetCookieHeader(new Uri(BffHost.Origin, path)));
        if (csrf) request.Headers.Add("X-CSRF", "1");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (headers is not null)
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        var response = await host.Client.SendAsync(request, ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values) _cookies.SetCookies(BffHost.Origin, value);
        return response;
    }

    internal async Task<(string Callback, string Cookies)> BeginLoginAsync(FakeIdentityProvider idp)
    {
        var started = await SendAsync(HttpMethod.Get, "/.auth/login", csrf: false);
        Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
        var authorize = started.Headers.Location!;
        var state = QueryHelpers.ParseQuery(authorize.Query)["state"].ToString();
        var code = idp.IssueCode(authorize);
        var callback = QueryHelpers.AddQueryString("/.auth/login/callback", new Dictionary<string, string?>
            { ["code"] = code, ["state"] = state });
        return (callback, _cookies.GetCookieHeader(new Uri(BffHost.Origin, callback)));
    }

    internal async Task<HttpResponseMessage> LoginAsync(FakeIdentityProvider idp)
    {
        var attempt = await BeginLoginAsync(idp);
        return await SendAsync(HttpMethod.Get, attempt.Callback, csrf: false);
    }
}
