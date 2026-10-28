using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using MonixOne.OidcProxy.ModuleInitializers;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Sessions;

/// <summary>
/// Stores complete authentication tickets on the server. Every mutation compares
/// the previously read value, so a late refresh cannot overwrite logout or newer tokens.
/// Redis failures never fall back to an independent in-process session.
/// </summary>
internal sealed class SessionStore(ProxyOptions options, IDataProtectionProvider protection, TimeProvider timeProvider)
    : IDisposable
{
    private readonly IDataProtector _protector = protection.CreateProtector("MonixOne.OidcProxy.ServerSession.v6", options.CookieName);
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly object _memoryGate = new();
    private IDatabase? Database => options.Redis?.GetDatabase();

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
            now + options.SessionAbsoluteTimeout,
            now + options.SessionIdleTimeout);
        var value = Encode(state);
        var key = options.RedisKeyPrefix + "sessions:" + id;
        if (Database is { } database)
        {
            if (!await database.StringSetAsync(key, value, Lifetime(state), When.NotExists).WaitAsync(cancellationToken))
                throw new InvalidOperationException("Unable to allocate a unique server session.");
        }
        else
        {
            lock (_memoryGate)
                _memory.Set(key, value, Lifetime(state));
        }
        return id;
    }

    internal Task<SessionSnapshot?> ReadAsync(string id, CancellationToken cancellationToken)
        => ReadCoreAsync(id, cancellationToken, 16);

    private async Task<SessionSnapshot?> ReadCoreAsync(string id, CancellationToken cancellationToken, int attemptsLeft)
    {
        if (!IsValidId(id)) return null;
        var key = options.RedisKeyPrefix + "sessions:" + id;
        string? value;
        if (Database is { } database)
            value = await database.StringGetAsync(key).WaitAsync(cancellationToken);
        else
            lock (_memoryGate) value = _memory.Get<string>(key);
        if (value is null) return null;

        StoredSession state;
        try
        {
            state = JsonSerializer.Deserialize<StoredSession>(_protector.Unprotect(value))
                ?? throw new CryptographicException("Invalid server session.");
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Do not authenticate a corrupt or tampered ticket, and do not log its contents.
            if (await CompareExchangeRawAsync(key, value, null, TimeSpan.Zero, cancellationToken)) return null;
            if (attemptsLeft > 0) return await ReadCoreAsync(id, cancellationToken, attemptsLeft - 1);
            throw new SessionUnavailableException("Session changed repeatedly while rejecting a corrupt record.");
        }

        var snapshot = new SessionSnapshot(id, value, state);
        if (state.AbsoluteExpiresAt <= timeProvider.GetUtcNow() || state.IdleExpiresAt <= timeProvider.GetUtcNow())
        {
            if (await CompareExchangeAsync(snapshot, null, cancellationToken)) return null;
            if (attemptsLeft > 0) return await ReadCoreAsync(id, cancellationToken, attemptsLeft - 1);
            throw new SessionUnavailableException("Session changed repeatedly while expiring a record.");
        }
        return snapshot;
    }

    internal Task<bool> CompareExchangeAsync(SessionSnapshot expected, StoredSession? updated, CancellationToken cancellationToken)
    {
        var lifetime = updated is null ? TimeSpan.Zero : Lifetime(updated);
        return CompareExchangeRawAsync(options.RedisKeyPrefix + "sessions:" + expected.Id, expected.Value,
            lifetime > TimeSpan.Zero && updated is not null ? Encode(updated) : null, lifetime, cancellationToken);
    }

    private async Task<bool> CompareExchangeRawAsync(string key, string expected, string? updated,
        TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Database is { } database)
        {
            var result = await database.ScriptEvaluateAsync(CompareExchangeScript, [new RedisKey(key)],
                [expected, updated ?? "", Math.Max(1L, (long)Math.Ceiling(lifetime.TotalMilliseconds))])
                .WaitAsync(cancellationToken);
            return (long)result == 1;
        }
        lock (_memoryGate)
        {
            if (_memory.Get<string>(key) != expected) return false;
            if (updated is null) _memory.Remove(key);
            else _memory.Set(key, updated, lifetime);
            return true;
        }
    }

    internal async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id)) return;
        var key = options.RedisKeyPrefix + "sessions:" + id;
        if (Database is { } database)
            await database.KeyDeleteAsync(key).WaitAsync(cancellationToken);
        else
            lock (_memoryGate) _memory.Remove(key);
    }

    private string Encode(StoredSession state) => _protector.Protect(JsonSerializer.Serialize(state));
    private TimeSpan Lifetime(StoredSession state)
        => (state.AbsoluteExpiresAt < state.IdleExpiresAt ? state.AbsoluteExpiresAt : state.IdleExpiresAt) - timeProvider.GetUtcNow();
    private static bool IsValidId(string id) => id.Length == 64 && id.All(char.IsAsciiHexDigitUpper);
    public void Dispose() => _memory.Dispose();
}

internal sealed record StoredSession(string Ticket, DateTimeOffset AbsoluteExpiresAt, DateTimeOffset IdleExpiresAt,
    string? RefreshOperation = null, DateTimeOffset? RefreshDeadline = null)
{
    internal AuthenticationTicket ReadTicket() => TicketSerializer.Default.Deserialize(Convert.FromBase64String(Ticket))
        ?? throw new CryptographicException("Invalid authentication ticket.");
}

internal sealed record SessionSnapshot(string Id, string Value, StoredSession State);
