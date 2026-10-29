using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Sessions;

/// <summary>
/// Stores complete authentication tickets on the server. Every mutation compares
/// the previously read value, so a late refresh cannot overwrite logout or newer tokens.
/// Redis failures never fall back to an independent in-process session.
/// </summary>
internal sealed class SessionStore(BffClient client, ConnectionMultiplexer connection, IDataProtectionProvider protection, TimeProvider timeProvider)
{
    private readonly IDataProtector _protector = protection.CreateProtector("MonixOne.OidcProxy.Yarp.ServerSession.v6", client.CookieName);
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
        var now = timeProvider.GetUtcNow();
        var id = RandomNumberGenerator.GetHexString(64);
        var state = new StoredSession(
            Convert.ToBase64String(TicketSerializer.Default.Serialize(ticket)),
            now + client.Options.SessionAbsoluteTimeout,
            now + client.Options.SessionIdleTimeout);
        var value = Encode(state);
        var key = client.RedisKeyPrefix + "sessions:" + id;
        if (!await Database.StringSetAsync(key, value, Lifetime(state), When.NotExists).WaitAsync(cancellationToken))
            throw new InvalidOperationException("Unable to allocate a unique server session.");
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

            StoredSession? state = null;
            try
            {
                state = JsonSerializer.Deserialize<StoredSession>(_protector.Unprotect(value));
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                // Invalid protected data is rejected and removed below. Never log it.
            }
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
        return CompareExchangeRawAsync(client.RedisKeyPrefix + "sessions:" + expected.Id, expected.Value,
            lifetime > TimeSpan.Zero && updated is not null ? Encode(updated) : null, lifetime, cancellationToken);
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
            return (JsonSerializer.Deserialize<StoredSession>(_protector.Unprotect(value))
                ?? throw new CryptographicException("Invalid server session.")).ReadTicket();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            // Deletion has already committed. Corrupt credentials cannot be used or revoked.
            return null;
        }
    }

    private string Encode(StoredSession state) => _protector.Protect(JsonSerializer.Serialize(state));
    private TimeSpan Lifetime(StoredSession state)
        => (state.AbsoluteExpiresAt < state.IdleExpiresAt ? state.AbsoluteExpiresAt : state.IdleExpiresAt) - timeProvider.GetUtcNow();
    private static bool IsValidId(string id) => id.Length == 64 && id.All(char.IsAsciiHexDigitUpper);
}

internal sealed record StoredSession(string Ticket, DateTimeOffset AbsoluteExpiresAt, DateTimeOffset IdleExpiresAt,
    string? RefreshOperation = null, DateTimeOffset? RefreshDeadline = null)
{
    internal AuthenticationTicket ReadTicket() => TicketSerializer.Default.Deserialize(Convert.FromBase64String(Ticket))
        ?? throw new CryptographicException("Invalid authentication ticket.");
}

internal sealed record SessionSnapshot(string Id, string Value, StoredSession State);
