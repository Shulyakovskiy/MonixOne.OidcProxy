namespace MonixOne.OidcProxy.Auth0;

public sealed class Auth0ProxyConfig : ProxyConfig
{
    public Auth0Config Auth0 { get; set; } = new();
}

public sealed class Auth0Config
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = [];
}
