namespace MonixOne.OidcProxy.OpenIdConnect;

public class OidcProxyConfig : ProxyConfig
{
    public OpenIdConnectConfig Oidc { get; set; } = new();
}
