using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Jose;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Sessions;

/// <summary>
/// Stores complete authentication tickets on the server. Every mutation compares
/// the previously read value, so a late refresh cannot overwrite logout or newer tokens.
/// Redis failures never fall back to an independent in-process session.
/// </summary>
internal sealed class SessionStore(BffClient client, ConnectionMultiplexer connection, IOptions<ProxyOptions> options,
    TimeProvider timeProvider, IHostApplicationLifetime lifetime)
{
    private readonly CancellationToken _stopping = lifetime.ApplicationStopping;
    private readonly string _encryptionKey = options.Value.EncryptionKey;
    private IDatabase Database => connection.GetDatabase();

    private const string CompareExchangeScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
        if ARGV[2] == '' then
            redis.call('DEL', KEYS[1])
        else
            redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
        end
        return 1
        """;

    internal async Task<string> CreateAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var id = RandomNumberGenerator.GetHexString(64);
        var key = client.RedisKeyPrefix + "sessions:" + id;
        var state = new StoredSession(
            Convert.ToBase64String(TicketSerializer.Default.Serialize(ticket)),
            now + client.Options.SessionAbsoluteTimeout,
            now + client.Options.SessionIdleTimeout,
            key,
            RefreshAt: StoredSession.CalculateRefreshAt(DateTimeOffset.Parse(ticket.Properties.GetTokenValue("expires_at")!,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), now));
        var value = Encode(state);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!await Database.StringSetAsync(key, value, Lifetime(state), When.NotExists).WaitAsync(timeout.Token))
                throw new InvalidOperationException("Unable to allocate a unique server session.");
        }
        catch (OperationCanceledException e) when (timeout.IsCancellationRequested)
        {
            // An unacknowledged SET can leave an unreachable record until TTL.
            // Sign-in owns revocation of the credentials already issued by IdP.
            throw new SessionUnavailableException("Unable to confirm session creation within its server budget.", e);
        }
        catch (ObjectDisposedException e) when (_stopping.IsCancellationRequested)
        {
            throw new SessionUnavailableException("The session store is stopping.", e);
        }
        return id;
    }

    internal async Task<SessionSnapshot?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id)) return null;
        var key = client.RedisKeyPrefix + "sessions:" + id;
        for (var attempt = 0; attempt <= 16; attempt++)
        {
            string? value = await Database.StringGetAsync(key).WaitAsync(cancellationToken);
            if (value is null) return null;

            var state = Decode(value, key);
            var now = timeProvider.GetUtcNow();
            if (state is not null && state.AbsoluteExpiresAt > now && state.IdleExpiresAt > now)
                return new SessionSnapshot(id, value, state);

            // Expired and corrupt records share the same CAS deletion. A concurrent
            // update must be reread rather than deleting another request's result.
            if (await CompareExchangeRawAsync(key, value, null, TimeSpan.Zero, cancellationToken)) return null;
        }
        throw new SessionUnavailableException("Session changed repeatedly while reading its state.");
    }

    internal Task<bool> CompareExchangeAsync(SessionSnapshot expected, StoredSession? updated, CancellationToken cancellationToken)
    {
        var lifetime = updated is null ? TimeSpan.Zero : Lifetime(updated);
        string? value = null;
        if (updated is not null && lifetime > TimeSpan.Zero)
            value = updated == expected.State ? expected.Value : Encode(updated);
        return CompareExchangeRawAsync(client.RedisKeyPrefix + "sessions:" + expected.Id, expected.Value,
            value, lifetime, cancellationToken);
    }

    private async Task<bool> CompareExchangeRawAsync(string key, string expected, string? updated,
        TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Database.ScriptEvaluateAsync(CompareExchangeScript, [new RedisKey(key)],
            [expected, updated ?? "", Math.Max(1L, (long)Math.Ceiling(lifetime.TotalMilliseconds))])
            .WaitAsync(cancellationToken);
        return (long)result == 1;
    }

    internal async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id)) return;
        var key = client.RedisKeyPrefix + "sessions:" + id;
        await Database.KeyDeleteAsync(key).WaitAsync(cancellationToken);
    }

    internal async Task<AuthenticationTicket?> TakeAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id)) return null;
        var key = client.RedisKeyPrefix + "sessions:" + id;
        string? value = await Database.StringGetDeleteAsync(key).WaitAsync(cancellationToken);
        if (value is null) return null;
        try
        {
            return Decode(value, key)?.ReadTicket();
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Deletion has already committed. Corrupt credentials cannot be used or revoked.
            return null;
        }
    }

    private string Encode(StoredSession state) => JWT.Encode(JsonSerializer.Serialize(state), _encryptionKey,
        JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM);

    private StoredSession? Decode(string value, string key)
    {
        try
        {
            var state = JsonSerializer.Deserialize<StoredSession>(JWT.Decrypt(value, _encryptionKey,
                JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM));
            // Bind the authenticated payload to its application, client and session ID.
            return state?.SessionKey == key ? state : null;
        }
        catch (Exception e) when (e is JoseException or CryptographicException or JsonException or FormatException or ArgumentException)
        {
            // Invalid ciphertext is rejected and removed by CAS/GETDEL. Never log it.
            return null;
        }
    }

    private TimeSpan Lifetime(StoredSession state)
        => (state.AbsoluteExpiresAt < state.IdleExpiresAt ? state.AbsoluteExpiresAt : state.IdleExpiresAt) - timeProvider.GetUtcNow();
    private static bool IsValidId(string id) => id.Length == 64 && id.All(char.IsAsciiHexDigitUpper);
}

internal sealed record StoredSession(string Ticket, DateTimeOffset AbsoluteExpiresAt, DateTimeOffset IdleExpiresAt,
    string SessionKey, string? RefreshOperation = null, DateTimeOffset? RefreshDeadline = null, DateTimeOffset? RefreshAt = null)
{
    internal static DateTimeOffset CalculateRefreshAt(DateTimeOffset expiresAt, DateTimeOffset receivedAt)
        => expiresAt - TimeSpan.FromTicks(Math.Clamp((expiresAt - receivedAt).Ticks / 10, 0, TimeSpan.FromSeconds(30).Ticks));

    internal AuthenticationTicket ReadTicket() => TicketSerializer.Default.Deserialize(Convert.FromBase64String(Ticket))
        ?? throw new CryptographicException("Invalid authentication ticket.");
}

internal sealed record SessionSnapshot(string Id, string Value, StoredSession State);
