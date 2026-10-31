using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Sessions;

/// <summary>
/// Stores complete authentication tickets on the server. Every mutation compares
/// the previously read value, so a late refresh cannot overwrite logout or newer tokens.
/// Redis failures never fall back to an independent in-process session.
/// </summary>
internal sealed class SessionStore(
    BffClient client,
    ConnectionMultiplexer connection,
    IDataProtectionProvider protection,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    BffTelemetry telemetry
)
{
    private readonly CancellationToken _stopping = lifetime.ApplicationStopping;
    private readonly IDataProtector _protector = protection.CreateProtector(
        BffConstants.SessionProtectionPurpose,
        client.RedisKeyPrefix
    );
    private IDatabase Database => connection.GetDatabase();

    // Session indexes and their records share a Redis Cluster slot per client.
    private string SessionKey(string id) =>
        client.RedisKeyPrefix + "sessions:{" + client.RedisKeyPrefix + "}:" + id;

    private string IndexKey(string issuer, string? subject, string? sid) =>
        client.RedisKeyPrefix
        + "session-index:{"
        + client.RedisKeyPrefix
        + "}:"
        + Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { issuer, subject, sid }))
            )
        );

    private const string CreateScript = """
        if not redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2], 'NX') then return 0 end
        for i = 2, #KEYS do
            redis.call('ZREMRANGEBYSCORE', KEYS[i], '-inf', ARGV[3])
            redis.call('ZADD', KEYS[i], ARGV[4], KEYS[1])
            if redis.call('PTTL', KEYS[i]) < tonumber(ARGV[5]) then
                redis.call('PEXPIRE', KEYS[i], ARGV[5])
            end
        end
        return 1
        """;

    private const string LogoutScript = """
        if redis.call('EXISTS', KEYS[2]) == 1 then return {1, 0} end
        local removed = 0
        for i = 3, #KEYS do
            if redis.call('ZSCORE', KEYS[1], KEYS[i]) then
                removed = removed + redis.call('DEL', KEYS[i])
                redis.call('ZREM', KEYS[1], KEYS[i])
            end
        end
        if redis.call('ZCARD', KEYS[1]) == 0 then
            redis.call('SET', KEYS[2], '1', 'EX', ARGV[1])
            return {1, removed}
        end
        return {0, removed}
        """;

    private const string CompareExchangeScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
        if ARGV[2] == '' then
            redis.call('DEL', KEYS[1])
        else
            redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
        end
        return 1
        """;

    internal async Task<string> CreateAsync(
        AuthenticationTicket ticket,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var absoluteTimeout = ticket.Properties.IsPersistent
            ? client.Options.RememberedSessionTimeout
            : client.Options.SessionAbsoluteTimeout;
        var id = RandomNumberGenerator.GetHexString(64);
        var key = SessionKey(id);
        var state = new StoredSession(
            Convert.ToBase64String(TicketSerializer.Default.Serialize(ticket)),
            now + absoluteTimeout,
            now
                + (
                    ticket.Properties.IsPersistent
                        ? absoluteTimeout
                        : client.Options.SessionIdleTimeout
                ),
            key,
            RefreshAt: StoredSession.CalculateRefreshAt(
                DateTimeOffset.Parse(
                    ticket.Properties.GetTokenValue("expires_at")!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                ),
                now
            )
        );
        var value = Encode(state);
        var keys = new List<RedisKey> { key };
        ticket.Properties.Items.TryGetValue(BffConstants.ProviderIssuer, out var issuer);
        ticket.Properties.Items.TryGetValue(BffConstants.ProviderSubject, out var subject);
        ticket.Properties.Items.TryGetValue(BffConstants.ProviderSession, out var sid);
        if (!string.IsNullOrEmpty(issuer))
        {
            if (!string.IsNullOrEmpty(subject))
                keys.Add(IndexKey(issuer, subject, null));
            if (!string.IsNullOrEmpty(sid))
                keys.Add(IndexKey(issuer, null, sid));
            if (!string.IsNullOrEmpty(subject) && !string.IsNullOrEmpty(sid))
                keys.Add(IndexKey(issuer, subject, sid));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        timeout.CancelAfter(client.Options.SessionStoreTimeout);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (
                (long)
                    await Database
                        .ScriptEvaluateAsync(
                            CreateScript,
                            keys.ToArray(),
                            [
                                value,
                                Math.Max(1L, (long)Lifetime(state).TotalMilliseconds),
                                now.ToUnixTimeMilliseconds(),
                                state.AbsoluteExpiresAt.ToUnixTimeMilliseconds(),
                                (long)absoluteTimeout.TotalMilliseconds,
                            ]
                        )
                        .WaitAsync(timeout.Token) != 1
            )
                throw new InvalidOperationException("Unable to allocate a unique server session.");
        }
        catch (OperationCanceledException e) when (timeout.IsCancellationRequested)
        {
            // An unacknowledged creation script can leave an unreachable record until TTL.
            // Sign-in owns revocation of the credentials already issued by IdP.
            throw new SessionUnavailableException(
                "Unable to confirm session creation within its server budget.",
                e
            );
        }
        catch (ObjectDisposedException e) when (_stopping.IsCancellationRequested)
        {
            throw new SessionUnavailableException("The session store is stopping.", e);
        }
        telemetry.SessionCreated(client.Name);
        return id;
    }

    internal async Task<SessionSnapshot?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id))
            return null;
        var key = SessionKey(id);
        for (var attempt = 0; attempt <= 16; attempt++)
        {
            string? value = await Database.StringGetAsync(key).WaitAsync(cancellationToken);
            if (value is null)
                return null;

            var state = Decode(value, key);
            var now = timeProvider.GetUtcNow();
            if (state is not null && state.AbsoluteExpiresAt > now && state.IdleExpiresAt > now)
                return new SessionSnapshot(id, value, state);

            // Expired and corrupt records share the same CAS deletion. A concurrent
            // update must be reread rather than deleting another request's result.
            if (await CompareExchangeRawAsync(key, value, null, TimeSpan.Zero, cancellationToken))
                return null;
        }
        throw new SessionUnavailableException(
            "Session changed repeatedly while reading its state."
        );
    }

    internal Task<bool> CompareExchangeAsync(
        SessionSnapshot expected,
        StoredSession? updated,
        CancellationToken cancellationToken
    )
    {
        var lifetime = updated is null ? TimeSpan.Zero : Lifetime(updated);
        string? value = null;
        if (updated is not null && lifetime > TimeSpan.Zero)
            value = updated == expected.State ? expected.Value : Encode(updated);
        return CompareExchangeRawAsync(
            SessionKey(expected.Id),
            expected.Value,
            value,
            lifetime,
            cancellationToken
        );
    }

    private async Task<bool> CompareExchangeRawAsync(
        string key,
        string expected,
        string? updated,
        TimeSpan lifetime,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Database
            .ScriptEvaluateAsync(
                CompareExchangeScript,
                [new RedisKey(key)],
                [
                    expected,
                    updated ?? "",
                    Math.Max(1L, (long)Math.Ceiling(lifetime.TotalMilliseconds)),
                ]
            )
            .WaitAsync(cancellationToken);
        var changed = (long)result == 1;
        if (!changed)
            telemetry.CasConflict(client.Name);
        else if (updated is null)
            telemetry.SessionRemoved(client.Name);
        return changed;
    }

    internal async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id))
            return;
        var key = SessionKey(id);
        if (await Database.KeyDeleteAsync(key).WaitAsync(cancellationToken))
            telemetry.SessionRemoved(client.Name);
    }

    internal async Task<AuthenticationTicket?> TakeAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        if (!IsValidId(id))
            return null;
        var key = SessionKey(id);
        string? value = await Database.StringGetDeleteAsync(key).WaitAsync(cancellationToken);
        if (value is null)
            return null;
        telemetry.SessionRemoved(client.Name);
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

    private string Encode(StoredSession state) =>
        _protector.Protect(JsonSerializer.Serialize(state));

    internal async Task RevokeSessionsAsync(
        string issuer,
        string? subject,
        string? sid,
        string jwtId,
        CancellationToken cancellationToken
    )
    {
        var index = IndexKey(issuer, subject, sid);
        await Database
            .SortedSetRemoveRangeByScoreAsync(
                index,
                double.NegativeInfinity,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            )
            .WaitAsync(cancellationToken);
        // Index entries expire with the absolute lifetime; no scans or cleanup worker.
        // Each batch atomically deletes records and index members. The final batch also
        // commits the replay marker, so duplicate notifications cannot close a later login.
        for (var batch = 0; batch < client.Options.BackchannelLogout.MaximumBatches; batch++)
        {
            var members = await Database
                .SortedSetRangeByRankAsync(index, 0, client.Options.BackchannelLogout.BatchSize - 1)
                .WaitAsync(cancellationToken);
            var keys = new List<RedisKey> { index, LogoutKey(issuer, jwtId) };
            keys.AddRange(members.Select(member => (RedisKey)(string)member!));
            var result = (RedisResult[])
                (
                    await Database
                        .ScriptEvaluateAsync(
                            LogoutScript,
                            keys.ToArray(),
                            [
                                (long)
                                    Math.Ceiling(
                                        client
                                            .Options
                                            .BackchannelLogout
                                            .ReplayRetention
                                            .TotalSeconds
                                    ),
                            ]
                        )
                        .WaitAsync(cancellationToken)
                )!;
            telemetry.SessionRemoved(client.Name, (long)result[1]);
            if ((long)result[0] == 1)
                return;
        }
        throw new SessionUnavailableException(
            "Back-channel logout exceeded its bounded session batch limit."
        );
    }

    private string LogoutKey(string issuer, string jwtId) =>
        client.RedisKeyPrefix
        + "logout:{"
        + client.RedisKeyPrefix
        + "}:"
        + Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { issuer, jwtId }))
            )
        );

    private StoredSession? Decode(string value, string key)
    {
        try
        {
            var state = JsonSerializer.Deserialize<StoredSession>(_protector.Unprotect(value));
            // Bind the authenticated payload to its application, client and session ID.
            return state?.SessionKey == key ? state : null;
        }
        catch (Exception e)
            when (e
                    is CryptographicException
                        or JsonException
                        or FormatException
                        or ArgumentException
            )
        {
            // Invalid ciphertext is rejected and removed by CAS/GETDEL. Never log it.
            return null;
        }
    }

    private TimeSpan Lifetime(StoredSession state) =>
        (
            state.AbsoluteExpiresAt < state.IdleExpiresAt
                ? state.AbsoluteExpiresAt
                : state.IdleExpiresAt
        ) - timeProvider.GetUtcNow();

    private static bool IsValidId(string id) =>
        id.Length == 64 && id.All(char.IsAsciiHexDigitUpper);
}

internal sealed record StoredSession(
    string Ticket,
    DateTimeOffset AbsoluteExpiresAt,
    DateTimeOffset IdleExpiresAt,
    string SessionKey,
    string? RefreshOperation = null,
    DateTimeOffset? RefreshDeadline = null,
    DateTimeOffset? RefreshAt = null
)
{
    internal static DateTimeOffset CalculateRefreshAt(
        DateTimeOffset expiresAt,
        DateTimeOffset receivedAt
    ) =>
        expiresAt
        - TimeSpan.FromTicks(
            Math.Clamp((expiresAt - receivedAt).Ticks / 10, 0, TimeSpan.FromSeconds(30).Ticks)
        );

    internal AuthenticationTicket ReadTicket() =>
        TicketSerializer.Default.Deserialize(Convert.FromBase64String(Ticket))
        ?? throw new CryptographicException("Invalid authentication ticket.");
}

internal sealed record SessionSnapshot(string Id, string Value, StoredSession State);
