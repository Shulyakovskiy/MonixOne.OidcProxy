using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Sessions;

internal sealed class TokenSession(
    SessionStore store,
    BffClient client,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<TokenSession> logger,
    BffTelemetry telemetry
)
{
    private const int MaxStateAttempts = 4096;
    private const int MaxRefreshPolls = 256;
    private const int MaxCompletionAttempts = 16;
    private static readonly TimeSpan InitialPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaximumPollDelay = TimeSpan.FromMilliseconds(500);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _refreshes = new(
        StringComparer.Ordinal
    );
    private readonly CancellationToken _stopping = applicationLifetime.ApplicationStopping;

    internal async Task<AuthenticationTicket?> GetActiveTicketAsync(
        string id,
        CancellationToken cancellationToken,
        SessionSnapshot? initial = null,
        bool renewSession = true
    )
    {
        var activityAt = timeProvider.GetUtcNow();
        var snapshot = initial;
        for (var attempt = 0; attempt < MaxStateAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot ??= await store.ReadAsync(id, cancellationToken);
            if (snapshot is null)
                return null;
            var now = timeProvider.GetUtcNow();
            var reused = ReferenceEquals(snapshot, initial);
            if (
                snapshot.State.AbsoluteExpiresAt <= now
                || snapshot.State.IdleExpiresAt <= now
                || snapshot.State.RefreshOperation is not null
                    && snapshot.State.RefreshDeadline <= now
            )
            {
                if (await store.CompareExchangeAsync(snapshot, null, cancellationToken))
                    return null;
                snapshot = null;
                continue;
            }
            if (!renewSession)
            {
                // Session inspection neither touches idle expiry nor redeems credentials.
                // CAS still verifies that authentication did not race a logout/update.
                if (await store.CompareExchangeAsync(snapshot, snapshot.State, cancellationToken))
                    return snapshot.State.ReadTicket();
                snapshot = null;
                continue;
            }
            if (snapshot.State.RefreshOperation is not null)
            {
                if (
                    await TouchAsync(
                        snapshot,
                        activityAt,
                        snapshot.State.RefreshAt,
                        reused,
                        cancellationToken
                    )
                )
                    await WaitForRefreshAsync(snapshot, cancellationToken);
                snapshot = null;
                continue;
            }

            var ticket = snapshot.State.ReadTicket();
            if (
                !DateTimeOffset.TryParse(
                    ticket.Properties.GetTokenValue("expires_at"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var expiresAt
                )
            )
            {
                if (await store.CompareExchangeAsync(snapshot, null, cancellationToken))
                    return null;
                snapshot = null;
                continue;
            }
            var refreshToken = ticket.Properties.GetTokenValue("refresh_token");
            // Records without RefreshAt derive a stable threshold on their first activity.
            var refreshAt =
                snapshot.State.RefreshAt ?? StoredSession.CalculateRefreshAt(expiresAt, now);
            if (expiresAt > now && (string.IsNullOrEmpty(refreshToken) || now < refreshAt))
            {
                if (await TouchAsync(snapshot, activityAt, refreshAt, reused, cancellationToken))
                    return ticket;
            }
            else if (string.IsNullOrEmpty(refreshToken))
            {
                if (await store.CompareExchangeAsync(snapshot, null, cancellationToken))
                    return null;
            }
            else
                await WaitForRefreshAsync(snapshot, cancellationToken);
            snapshot = null;
        }
        throw new SessionUnavailableException(
            "Session contention exceeded the bounded retry limit."
        );
    }

    private DateTimeOffset ActivityExpiration(StoredSession state, DateTimeOffset activityAt)
    {
        var idleUntil = activityAt + client.Options.SessionIdleTimeout;
        if (idleUntil > state.AbsoluteExpiresAt)
            idleUntil = state.AbsoluteExpiresAt;
        return idleUntil > state.IdleExpiresAt ? idleUntil : state.IdleExpiresAt;
    }

    private Task<bool> TouchAsync(
        SessionSnapshot snapshot,
        DateTimeOffset activityAt,
        DateTimeOffset? refreshAt,
        bool verifyCurrent,
        CancellationToken ct
    )
    {
        var touched = snapshot.State with
        {
            // A later concurrent activity already covers this request's instant.
            IdleExpiresAt = ActivityExpiration(snapshot.State, activityAt),
            RefreshAt = refreshAt,
        };
        // An authentication snapshot may predate logout or a concurrent refresh.
        // Verify it even when another activity already covers the idle instant.
        return touched == snapshot.State && !verifyCurrent
            ? Task.FromResult(true)
            : store.CompareExchangeAsync(snapshot, touched, ct);
    }

    private async Task WaitForRefreshAsync(SessionSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Each TokenSession belongs to one client. Lazy prevents losing factories
        // from starting extra owners; caller cancellation only stops that waiter.
        var operation = _refreshes.GetOrAdd(
            snapshot.Id,
            _ => new Lazy<Task>(() => CoordinateRefreshAsync(snapshot))
        );
        var started = Stopwatch.GetTimestamp();
        try
        {
            await operation.Value.WaitAsync(ct);
        }
        finally
        {
            telemetry.RefreshWaited(client.Name, started);
        }
    }

    private async Task CoordinateRefreshAsync(SessionSnapshot initial)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        budget.CancelAfter(client.Options.RefreshTimeout);
        var ct = budget.Token;
        SessionSnapshot? snapshot = initial;
        var delay = InitialPollDelay;
        try
        {
            for (var attempt = 0; attempt < MaxRefreshPolls; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                snapshot ??= await store.ReadAsync(initial.Id, ct);
                if (snapshot is null)
                    return;
                var now = timeProvider.GetUtcNow();
                if (snapshot.State.RefreshOperation is not null)
                {
                    if (snapshot.State.RefreshDeadline <= now)
                    {
                        if (await store.CompareExchangeAsync(snapshot, null, ct))
                            return;
                    }
                    else
                    {
                        var milliseconds = Math.Min(
                            MaximumPollDelay.TotalMilliseconds,
                            delay.TotalMilliseconds * (0.8 + Random.Shared.NextDouble() * 0.4)
                        );
                        await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), timeProvider, ct);
                        delay = TimeSpan.FromMilliseconds(
                            Math.Min(
                                delay.TotalMilliseconds * 2,
                                MaximumPollDelay.TotalMilliseconds
                            )
                        );
                    }
                    snapshot = null;
                    continue;
                }

                var ticket = snapshot.State.ReadTicket();
                if (
                    !DateTimeOffset.TryParse(
                        ticket.Properties.GetTokenValue("expires_at"),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var expiresAt
                    )
                )
                {
                    if (await store.CompareExchangeAsync(snapshot, null, ct))
                        return;
                    snapshot = null;
                    continue;
                }
                var refreshToken = ticket.Properties.GetTokenValue("refresh_token");
                var refreshAt =
                    snapshot.State.RefreshAt ?? StoredSession.CalculateRefreshAt(expiresAt, now);
                if (string.IsNullOrEmpty(refreshToken) || now < refreshAt)
                    return;
                var operation = RandomNumberGenerator.GetHexString(32);
                var claimed = snapshot.State with
                {
                    RefreshOperation = operation,
                    RefreshDeadline = now + client.Options.RefreshTimeout,
                    IdleExpiresAt = ActivityExpiration(snapshot.State, now),
                };
                // The server budget already applies before sending this mutation.
                if (!await TryClaimAsync(snapshot, claimed, ct))
                {
                    snapshot = null;
                    continue;
                }

                using var activity = telemetry.StartRefresh(client.Name);
                var started = Stopwatch.GetTimestamp();
                AuthenticationTicket updated;
                try
                {
                    updated = await RefreshAsync(ticket, refreshToken, ct);
                }
                catch (Exception e)
                {
                    activity?.SetStatus(ActivityStatusCode.Error);
                    telemetry.RefreshCompleted(
                        client.Name,
                        e is ReauthenticationRequiredException
                            ? "reauthentication_required"
                            : "provider_failure",
                        started
                    );
                    await InvalidateAsync(initial.Id);
                    await RevokeAsync(ticket, CancellationToken.None);
                    logger.LogWarning(
                        "BFF refresh failed; session cleanup requested. Failure type: {FailureType}",
                        e.GetType().Name
                    );
                    if (e is ReauthenticationRequiredException)
                        return;
                    throw new SessionUnavailableException(
                        "The identity provider could not complete token refresh.",
                        e
                    );
                }
                try
                {
                    var committed = await CompleteRefreshAsync(initial.Id, operation, updated, ct);
                    telemetry.RefreshCompleted(
                        client.Name,
                        committed ? "success" : "session_removed",
                        started
                    );
                }
                catch
                {
                    activity?.SetStatus(ActivityStatusCode.Error);
                    telemetry.RefreshCompleted(client.Name, "persistence_failure", started);
                    throw;
                }
                return;
            }
            throw new SessionUnavailableException(
                "Session contention exceeded the bounded refresh limit."
            );
        }
        catch (OperationCanceledException e) when (budget.IsCancellationRequested)
        {
            throw new SessionUnavailableException(
                "Refresh could not complete within its server budget.",
                e
            );
        }
        finally
        {
            _refreshes.TryRemove(initial.Id, out _);
        }
    }

    private async Task<bool> CompleteRefreshAsync(
        string id,
        string operation,
        AuthenticationTicket updated,
        CancellationToken ct
    )
    {
        try
        {
            for (var attempt = 0; attempt < MaxCompletionAttempts; attempt++)
            {
                var latest = await store.ReadAsync(id, ct);
                if (
                    latest is null
                    || latest.State.RefreshOperation != operation
                    || latest.State.RefreshDeadline <= timeProvider.GetUtcNow()
                )
                {
                    await RevokeAsync(updated, CancellationToken.None);
                    return false;
                }
                var now = timeProvider.GetUtcNow();
                var completed = latest.State with
                {
                    Ticket = Convert.ToBase64String(TicketSerializer.Default.Serialize(updated)),
                    IdleExpiresAt = ActivityExpiration(latest.State, now),
                    RefreshOperation = null,
                    RefreshDeadline = null,
                    RefreshAt = StoredSession.CalculateRefreshAt(
                        DateTimeOffset.Parse(
                            updated.Properties.GetTokenValue("expires_at")!,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind
                        ),
                        now
                    ),
                };
                if (await store.CompareExchangeAsync(latest, completed, ct))
                    return true;
            }
            throw new SessionUnavailableException(
                "Session changed repeatedly while completing refresh."
            );
        }
        catch (Exception e)
        {
            await InvalidateAsync(id);
            await RevokeAsync(updated, CancellationToken.None);
            throw new SessionUnavailableException("Unable to persist the rotated tokens.", e);
        }
    }

    private async Task<bool> TryClaimAsync(
        SessionSnapshot snapshot,
        StoredSession claimed,
        CancellationToken ct
    )
    {
        try
        {
            return await store.CompareExchangeAsync(snapshot, claimed, ct);
        }
        catch (Exception e) when (e is RedisException or RedisTimeoutException)
        {
            // A transport timeout does not establish whether Lua committed. Read
            // our exact operation ID; never redeem based on an assumed write.
            var latest = await store.ReadAsync(snapshot.Id, ct);
            if (
                latest is not null
                && latest.State.RefreshOperation == claimed.RefreshOperation
                && latest.State.RefreshDeadline > timeProvider.GetUtcNow()
            )
                return true;
            throw new SessionUnavailableException(
                "Unable to confirm refresh ownership after a Redis failure.",
                e
            );
        }
    }

    private async Task<AuthenticationTicket> RefreshAsync(
        AuthenticationTicket previous,
        string refreshToken,
        CancellationToken ct
    )
    {
        var oidc = oidcOptions.Get(client.OpenIdConnectScheme);
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(ct);
        if (string.IsNullOrEmpty(configuration.TokenEndpoint))
            throw new SessionUnavailableException("The identity provider has no token endpoint.");
        var response = await oidc.Backchannel.RequestRefreshTokenAsync(
            new RefreshTokenRequest
            {
                Address = configuration.TokenEndpoint!,
                ClientId = oidc.ClientId!,
                ClientSecret = oidc.ClientSecret,
                ClientCredentialStyle = client.Options.ClientCredentialStyle,
                RefreshToken = refreshToken,
            },
            ct
        );
        if (response.IsError)
        {
            if (response.Error == "invalid_grant")
                throw new ReauthenticationRequiredException();
            throw new SessionUnavailableException("The identity provider rejected token refresh.");
        }
        try
        {
            if (
                string.IsNullOrWhiteSpace(response.AccessToken)
                || !string.Equals(response.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase)
                || response.ExpiresIn <= 0
            )
                throw new ReauthenticationRequiredException();

            var principal = previous.Principal;
            if (!string.IsNullOrEmpty(response.IdentityToken))
            {
                var parameters = oidc.TokenValidationParameters.Clone();
                parameters.ValidIssuer = configuration.Issuer;
                parameters.ValidAudience = oidc.ClientId;
                parameters.IssuerSigningKeys = configuration.SigningKeys;
                parameters.ValidateIssuer = true;
                parameters.ValidateAudience = true;
                parameters.ValidateLifetime = true;
                parameters.RequireExpirationTime = true;
                parameters.RequireSignedTokens = true;
                parameters.ConfigurationManager =
                    oidc.ConfigurationManager as BaseConfigurationManager;
                var handler = new JsonWebTokenHandler { MapInboundClaims = false };
                var validated = await handler.ValidateTokenAsync(
                    response.IdentityToken,
                    parameters
                );
                if (
                    !validated.IsValid
                    && validated.Exception is SecurityTokenSignatureKeyNotFoundException
                    && oidc.RefreshOnIssuerKeyNotFound
                    && !string.IsNullOrEmpty(oidc.MetadataAddress)
                )
                {
                    // ConfigurationManager refreshes in the background. A newly issued
                    // rotating token cannot wait for a later browser retry. Retrieve the
                    // configured HTTPS discovery/JWKS once and revalidate this same token;
                    // never repeat its refresh-token redemption.
                    var updatedConfiguration = await OpenIdConnectConfigurationRetriever.GetAsync(
                        oidc.MetadataAddress,
                        oidc.Backchannel,
                        ct
                    );
                    var updatedParameters = parameters.Clone();
                    updatedParameters.ConfigurationManager = null;
                    updatedParameters.IssuerSigningKeys = updatedConfiguration.SigningKeys;
                    validated = await handler.ValidateTokenAsync(
                        response.IdentityToken,
                        updatedParameters
                    );
                }
                if (!validated.IsValid)
                    throw new ReauthenticationRequiredException();
                var validatedJwt = (JsonWebToken)validated.SecurityToken;
                var refreshedIdToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                    (validatedJwt.InnerToken ?? validatedJwt).EncodedToken
                );
                var nonce = refreshedIdToken.Payload.Nonce;
                previous.Properties.Items.TryGetValue(
                    BffConstants.OriginalNonce,
                    out var originalNonce
                );
                previous.Properties.Items.TryGetValue(
                    BffConstants.OriginalAuthTime,
                    out var originalAuthTime
                );
                if (nonce is not null && nonce != originalNonce)
                    throw new ReauthenticationRequiredException();
                if (
                    refreshedIdToken.Payload.TryGetValue("auth_time", out var authTime)
                    && originalAuthTime != Convert.ToString(authTime, CultureInfo.InvariantCulture)
                )
                    throw new ReauthenticationRequiredException();
                try
                {
                    new OpenIdConnectProtocolValidator
                    {
                        RequireNonce = false,
                        RequireTimeStampInNonce = false,
                    }.ValidateTokenResponse(
                        new OpenIdConnectProtocolValidationContext
                        {
                            ClientId = oidc.ClientId,
                            Nonce = nonce,
                            ProtocolMessage = new OpenIdConnectMessage
                            {
                                IdToken = response.IdentityToken,
                                AccessToken = response.AccessToken,
                            },
                            ValidatedIdToken = refreshedIdToken,
                        }
                    );
                }
                catch (OpenIdConnectProtocolException)
                {
                    throw new ReauthenticationRequiredException();
                }
                var identity = validated.ClaimsIdentity;
                if (
                    identity.FindFirst("sub")?.Value != previous.Principal.FindFirst("sub")?.Value
                    || string.IsNullOrEmpty(identity.FindFirst("sub")?.Value)
                )
                    throw new ReauthenticationRequiredException();
                principal = new ClaimsPrincipal(identity);
            }

            var tokens = previous
                .Properties.GetTokens()
                .ToDictionary(t => t.Name, t => t.Value, StringComparer.Ordinal);
            tokens["access_token"] = response.AccessToken;
            tokens["expires_at"] = (
                timeProvider.GetUtcNow() + TimeSpan.FromSeconds(response.ExpiresIn)
            ).ToString("O", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(response.RefreshToken))
                tokens["refresh_token"] = response.RefreshToken;
            if (!string.IsNullOrEmpty(response.IdentityToken))
                tokens["id_token"] = response.IdentityToken;
            previous.Properties.StoreTokens(
                tokens.Select(t => new AuthenticationToken { Name = t.Key, Value = t.Value })
            );
            return new AuthenticationTicket(principal, previous.Properties, client.CookieScheme);
        }
        catch
        {
            // Successful redemption can issue a new refresh token even when subsequent
            // token validation fails. Revoke the known credential before discarding it.
            await RevokeAsync(response.RefreshToken ?? refreshToken, CancellationToken.None);
            throw;
        }
    }

    private async Task InvalidateAsync(string id)
    {
        using var timeout = new CancellationTokenSource(client.Options.SessionStoreTimeout);
        try
        {
            // Session IDs are never reused. Delete directly: a failed read must not
            // prevent cleanup, and an unacknowledged completion may have cleared the
            // pending marker already. In either case this logical session must end.
            await store.RemoveAsync(id, timeout.Token);
        }
        catch (Exception e)
        {
            // Redis did not confirm cleanup. Report the failure; the record
            // remains bounded by its stored lifetimes and any pending deadline.
            logger.LogError(
                "Unable to invalidate BFF session after refresh failure. Failure type: {FailureType}",
                e.GetType().Name
            );
        }
    }

    internal Task RevokeAsync(AuthenticationTicket ticket, CancellationToken cancellationToken) =>
        RevokeAsync(ticket.Properties.GetTokenValue("refresh_token"), cancellationToken);

    internal async Task RevokeAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
            return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(client.Options.TokenRevocationTimeout);
        try
        {
            var oidc = oidcOptions.Get(client.OpenIdConnectScheme);
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(
                timeout.Token
            );
            var endpoint = configuration.RevocationEndpoint;
            if (string.IsNullOrEmpty(endpoint))
                return;
            var response = await oidc.Backchannel.RevokeTokenAsync(
                new TokenRevocationRequest
                {
                    Address = endpoint,
                    ClientId = oidc.ClientId!,
                    ClientSecret = oidc.ClientSecret,
                    ClientCredentialStyle = client.Options.ClientCredentialStyle,
                    Token = token,
                    TokenTypeHint = "refresh_token",
                },
                timeout.Token
            );
            if (response.IsError)
            {
                telemetry.RevocationFailed(client.Name);
                logger.LogWarning("Identity provider rejected best-effort BFF token revocation.");
            }
        }
        catch (Exception e)
        {
            telemetry.RevocationFailed(client.Name);
            logger.LogWarning(
                "Best-effort BFF token revocation failed. Failure type: {FailureType}",
                e.GetType().Name
            );
        }
    }
}

internal sealed class SessionUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

internal sealed class ReauthenticationRequiredException : Exception;
