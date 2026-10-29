namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Defines the application/environment scope shared by the BFF host's clients.
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>
    /// The application segment isolating Redis and Data Protection keys.
    /// </summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>
    /// The deployment environment segment isolating Redis keys.
    /// </summary>
    public string EnvironmentName { get; set; } = string.Empty;

    /// <summary>
    /// The namespace distinguishing independent gateways.
    /// </summary>
    public string KeyNamespace { get; set; } = "oidcproxy";

    /// <summary>
    /// The password used by jose-jwt PBES2 to encrypt session data in Redis.
    /// Supply a strong random secret through configuration and
    /// keep the same value across restarts and replicas of this BFF.
    /// </summary>
    public string EncryptionKey { get; set; } = string.Empty;

    internal string RedisKeyPrefix =>
        $"{ApplicationName.ToLowerInvariant()}:{EnvironmentName.ToLowerInvariant()}:{KeyNamespace.ToLowerInvariant()}:";

    internal static bool IsKeySegment(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
