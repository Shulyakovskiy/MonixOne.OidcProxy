namespace MonixOne.OidcProxy.EntraId;

public sealed class EntraIdProxyConfig : ProxyConfig
{
    public EntraIdConfig EntraId { get; set; } = new();
}

public sealed class EntraIdConfig
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = [];
}
