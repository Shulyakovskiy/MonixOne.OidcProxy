namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Bounds signed provider logout processing for one BFF client.
/// Bind through Bff:Clients:&lt;name&gt;:BackchannelLogout.
/// </summary>
public sealed class BackchannelLogoutOptions
{
    /// <summary>
    /// Default encoded form limit: 64 KiB. Hosts can change MaximumRequestBodyBytes.
    /// </summary>
    private const long DefaultMaximumRequestBodyBytes = 64 * 1024;

    /// <summary>
    /// Default compact JWT limit: 32 Ki characters. Hosts can change MaximumTokenLength.
    /// </summary>
    private const int DefaultMaximumTokenLength = 32 * 1024;

    /// <summary>
    /// Default maximum records deleted atomically by one Redis script.
    /// </summary>
    private const int DefaultBatchSize = 100;

    /// <summary>
    /// Default maximum Redis batches per notification before requesting redelivery.
    /// </summary>
    private const int DefaultMaximumBatches = 100;

    /// <summary>
    /// Maximum form request body size in bytes, including URL encoding.
    /// </summary>
    public long MaximumRequestBodyBytes { get; set; } = DefaultMaximumRequestBodyBytes;

    /// <summary>
    /// Maximum compact logout JWT length in characters before validation.
    /// </summary>
    public int MaximumTokenLength { get; set; } = DefaultMaximumTokenLength;

    /// <summary>
    /// Maximum age of iat, including for providers that omit exp.
    /// </summary>
    public TimeSpan MaximumTokenAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Allowed identity provider clock difference for iat and token lifetime checks.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Server-owned budget for local invalidation after the logout token is validated.
    /// </summary>
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retains successful logout IDs. Must exceed MaximumTokenAge plus ClockSkew
    /// so duplicate delivery cannot invalidate a later login.
    /// </summary>
    public TimeSpan ReplayRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum records deleted by one Redis script to bound server blocking.
    /// </summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>
    /// Maximum batches per notification. Incomplete processing fails and remains retryable.
    /// </summary>
    public int MaximumBatches { get; set; } = DefaultMaximumBatches;

    internal bool IsValid =>
        MaximumTokenLength > 0
        && MaximumRequestBodyBytes > MaximumTokenLength
        && MaximumTokenAge > TimeSpan.Zero
        && ClockSkew >= TimeSpan.Zero
        && OidcProxyClientOptions.IsValidTimeout(ProcessingTimeout)
        && ReplayRetention > MaximumTokenAge
        && ReplayRetention - MaximumTokenAge > ClockSkew
        && BatchSize > 0
        && MaximumBatches > 0;
}
