using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MonixOne.OidcProxy.ModuleInitializers;
using MonixOne.OidcProxy.OpenIdConnect;

namespace MonixOne.OidcProxy.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData("http://app.example")]
    [InlineData("https://user:password@app.example")]
    [InlineData("https://app.example/path")]
    [InlineData("https://app.example/?query=1")]
    [InlineData("https://app.example/#fragment")]
    public void PublicOriginMustBeAnExplicitHttpsOrigin(string origin)
        => Assert.Throws<ArgumentException>(() => new ProxyOptions().SetCustomHostName(new Uri(origin)));

    [Theory]
    [InlineData("https://identity.example/?query=1")]
    [InlineData("https://identity.example/#fragment")]
    [InlineData("https://user:secret@identity.example")]
    [InlineData("http://identity.example")]
    [InlineData("not-a-uri")]
    public void IdentityAuthorityMustBeHttpsWithoutCredentialsOrQuery(string authority)
    {
        var config = new OpenIdConnectConfig { Authority = authority, ClientId = "client", ClientSecret = "sensitive-secret" };
        Assert.False(config.Validate(out var errors));
        Assert.DoesNotContain("sensitive-secret", string.Join(" ", errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("nested/path")]
    [InlineData("encoded%2fpath")]
    [InlineData("invalid?query")]
    public void AuthenticationEndpointPrefixMustBeOneSafePathSegment(string prefix)
    {
        var options = Options();
        options.EndpointName = prefix;
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void ProtocolSecurityRequirementsCannotBeDisabledByBasicConfiguration()
    {
        using var idp = new FakeIdentityProvider();
        var services = new ServiceCollection().AddLogging();
        services.AddOidcProxy(Options(), oidc =>
        {
            oidc.Authority = FakeIdentityProvider.Authority;
            oidc.ClientId = FakeIdentityProvider.ClientId;
            oidc.ClientSecret = FakeIdentityProvider.ClientSecret;
            oidc.Configuration = idp.Configuration();
            oidc.ResponseType = "token";
            oidc.UsePkce = false;
            oidc.SaveTokens = false;
            oidc.RequireHttpsMetadata = false;
            oidc.ProtocolValidator.RequireNonce = false;
            oidc.TokenValidationParameters.ValidateIssuer = false;
            oidc.TokenValidationParameters.ValidateAudience = false;
            oidc.TokenValidationParameters.ValidateLifetime = false;
        });
        using var provider = services.BuildServiceProvider();
        var actual = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(BffConstants.OidcScheme);
        Assert.Equal("code", actual.ResponseType);
        Assert.True(actual.UsePkce);
        Assert.True(actual.SaveTokens);
        Assert.True(actual.RequireHttpsMetadata);
        Assert.True(actual.ProtocolValidator.RequireNonce);
        Assert.True(actual.TokenValidationParameters.ValidateIssuer);
        Assert.True(actual.TokenValidationParameters.ValidateAudience);
        Assert.True(actual.TokenValidationParameters.ValidateLifetime);
    }

    [Fact]
    public void ScopeNamesRemainCaseSensitive()
    {
        var config = new OidcProxyConfig
        {
            CustomHostName = BffHost.Origin,
            Mode = Mode.AuthenticateOnly,
            Oidc = new OpenIdConnectConfig
            {
                Authority = FakeIdentityProvider.Authority, ClientId = "client", ClientSecret = "test-secret",
                Scopes = ["CustomScope", "customscope"]
            }
        };
        var services = new ServiceCollection().AddLogging();
        services.AddOidcProxy(config);
        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(BffConstants.OidcScheme);
        Assert.Contains("CustomScope", oidc.Scope);
        Assert.Contains("customscope", oidc.Scope);
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("")]
    public void EntraWrapperRejectsAmbiguousMultiTenantIssuerConfiguration(string tenant)
    {
        var config = new MonixOne.OidcProxy.EntraId.EntraIdProxyConfig
        {
            EntraId = new() { TenantId = tenant, ClientId = "client", ClientSecret = "not-logged" }
        };
        Assert.Throws<ArgumentException>(() => MonixOne.OidcProxy.EntraId.ModuleInitializer.AddEntraIdProxy(new ServiceCollection(), config));
    }

    [Fact]
    public void OptionsDoNotLeakBetweenApplicationHosts()
    {
        var first = new ServiceCollection().AddLogging();
        var second = new ServiceCollection().AddLogging();
        var a = Options();
        a.SetCustomHostName(new Uri("https://first.example"));
        var b = Options();
        b.SetCustomHostName(new Uri("https://second.example"));
        first.AddOidcProxy(a, oidc => { oidc.ClientId = "first"; oidc.Authority = FakeIdentityProvider.Authority; });
        second.AddOidcProxy(b, oidc => { oidc.ClientId = "second"; oidc.Authority = FakeIdentityProvider.Authority; });
        using var one = first.BuildServiceProvider();
        using var two = second.BuildServiceProvider();
        Assert.Equal("first.example", one.GetRequiredService<ProxyOptions>().PublicOrigin!.Host);
        Assert.Equal("second.example", two.GetRequiredService<ProxyOptions>().PublicOrigin!.Host);
    }

    [Fact]
    public async Task Auth0WrapperUsesTheSharedHandlerAndKeepsAudienceAndPublicRedirect()
    {
        var services = new ServiceCollection().AddLogging();
        var config = new MonixOne.OidcProxy.Auth0.Auth0ProxyConfig
        {
            Mode = Mode.AuthenticateOnly, CustomHostName = BffHost.Origin,
            Auth0 = new() { Domain = "tenant.auth0.com", ClientId = "auth0-client", ClientSecret = "not-logged", Audience = "api-audience", Scopes = ["CustomScope"] }
        };
        MonixOne.OidcProxy.Auth0.ModuleInitializer.AddAuth0Proxy(services, config);
        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(BffConstants.OidcScheme);
        Assert.Equal("https://tenant.auth0.com", oidc.Authority);
        Assert.Contains("CustomScope", oidc.Scope);
        var context = new Microsoft.AspNetCore.Authentication.OpenIdConnect.RedirectContext(
            new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme(BffConstants.OidcScheme, null, typeof(OpenIdConnectHandler)),
            oidc, new Microsoft.AspNetCore.Authentication.AuthenticationProperties())
        {
            ProtocolMessage = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectMessage()
        };
        await oidc.Events.RedirectToIdentityProvider(context);
        Assert.Equal("api-audience", context.ProtocolMessage.GetParameter("audience"));
        Assert.Equal("https://app.example/.auth/login/callback", context.ProtocolMessage.RedirectUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://tenant.auth0.com")]
    [InlineData("tenant.auth0.com/escape")]
    public void Auth0RejectsMalformedDomain(string domain)
    {
        var config = new MonixOne.OidcProxy.Auth0.Auth0ProxyConfig { Auth0 = new() { Domain = domain } };
        Assert.Throws<ArgumentException>(() => MonixOne.OidcProxy.Auth0.ModuleInitializer.AddAuth0Proxy(new ServiceCollection(), config));
    }

    [Fact]
    public void ConcreteEntraTenantUsesAnExactTenantSpecificAuthority()
    {
        var tenant = Guid.NewGuid();
        var services = new ServiceCollection().AddLogging();
        var config = new MonixOne.OidcProxy.EntraId.EntraIdProxyConfig
        {
            Mode = Mode.AuthenticateOnly, CustomHostName = BffHost.Origin,
            EntraId = new() { TenantId = tenant.ToString(), ClientId = "entra-client", ClientSecret = "not-logged", Scopes = ["api://scope/Read"] }
        };
        MonixOne.OidcProxy.EntraId.ModuleInitializer.AddEntraIdProxy(services, config);
        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(BffConstants.OidcScheme);
        Assert.Equal($"https://login.microsoftonline.com/{tenant}/v2.0", oidc.Authority);
        Assert.Contains("api://scope/Read", oidc.Scope);
    }

    private static ProxyOptions Options()
    {
        var options = new ProxyOptions { Mode = Mode.AuthenticateOnly };
        options.SetCustomHostName(BffHost.Origin);
        return options;
    }
}
