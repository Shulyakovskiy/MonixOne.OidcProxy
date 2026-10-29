namespace MonixOne.OidcProxy.OpenIdConnect;

public class OpenIdConnectConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = [];
    public bool DisablePushedAuthorization { get; set; }

    public virtual bool Validate(out IEnumerable<string> errors)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(ClientId)) failures.Add("ClientId is required.");
        if (string.IsNullOrWhiteSpace(ClientSecret)) failures.Add("ClientSecret is required for the confidential BFF client.");
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority) || authority.Scheme != "https" ||
            authority.UserInfo.Length != 0 || authority.Query.Length != 0 || authority.Fragment.Length != 0)
            failures.Add("Authority must be an HTTPS URL without credentials, query or fragment.");
        errors = failures;
        return failures.Count == 0;
    }
}
