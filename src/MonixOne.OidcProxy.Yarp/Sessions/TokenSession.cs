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

internal sealed class TokenSession(SessionStore store, BffClient client,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions, TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime, ILogger<TokenSession> logger)
{
    internal async Task<AuthenticationTicket?> GetActiveTicketAsync(string id, CancellationToken cancellationToken)
    {
        var activityAt = timeProvider.GetUtcNow();
        var waitStarted = timeProvider.GetTimestamp();
        for (var attempt = 0; attempt < 4096; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await store.ReadAsync(id, cancellationToken);
            if (snapshot is null) return null;
            var now = timeProvider.GetUtcNow();
            if (snapshot.State.RefreshOperation is not null)
            {
                if (snapshot.State.RefreshDeadline <= now)
                {
                    // A worker may have redeemed a rotating refresh token and lost the response.
                    // Never retry that token after an ambiguous failure or a crashed worker.
                    if (await store.CompareExchangeAsync(snapshot, null, cancellationToken)) return null;
                    continue;
                }
                if (timeProvider.GetElapsedTime(waitStarted) >= client.Options.RefreshTimeout)
                    throw new SessionUnavailableException("Another request is still refreshing this session.");
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeProvider, cancellationToken);
                continue;
            }

            var ticket = snapshot.State.ReadTicket();
            if (!DateTimeOffset.TryParse(ticket.Properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expiresAt))
            {
                if (await store.CompareExchangeAsync(snapshot, null, cancellationToken)) return null;
                continue;
            }
            var refreshToken = ticket.Properties.GetTokenValue("refresh_token");
            // Old v6 records have no RefreshAt. Derive it once from their remaining
            // token lifetime and persist it with the same CAS used for activity.
            var refreshAt = snapshot.State.RefreshAt ?? StoredSession.CalculateRefreshAt(expiresAt, now);
            if (expiresAt > now && (string.IsNullOrEmpty(refreshToken) || now < refreshAt))
            {
                var idleUntil = activityAt + client.Options.SessionIdleTimeout;
                if (idleUntil > snapshot.State.AbsoluteExpiresAt) idleUntil = snapshot.State.AbsoluteExpiresAt;
                var touched = snapshot.State with
                {
                    IdleExpiresAt = idleUntil > snapshot.State.IdleExpiresAt ? idleUntil : snapshot.State.IdleExpiresAt,
                    RefreshAt = refreshAt
                };
                if (touched != snapshot.State && !await store.CompareExchangeAsync(snapshot, touched, cancellationToken)) continue;
                return ticket;
            }

            if (string.IsNullOrEmpty(refreshToken))
            {
                if (await store.CompareExchangeAsync(snapshot, null, cancellationToken)) return null;
                continue;
            }

            var operation = RandomNumberGenerator.GetHexString(32);
            var claimed = snapshot.State with
            {
                RefreshOperation = operation,
                RefreshDeadline = now + client.Options.RefreshTimeout
            };
            // Redis may commit a claim even if waiting for its reply is cancelled.
            // Ownership and completion therefore share a server-owned budget from
            // the moment the mutation is sent, not from receipt of its response.
            cancellationToken.ThrowIfCancellationRequested();
            using var ownerTimeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            ownerTimeout.CancelAfter(client.Options.RefreshTimeout);
            try
            {
                if (!await TryClaimAsync(snapshot, claimed, ownerTimeout.Token)) continue;
            }
            catch (OperationCanceledException e) when (ownerTimeout.IsCancellationRequested)
            {
                throw new SessionUnavailableException("Refresh ownership could not be confirmed within its server budget.", e);
            }
            AuthenticationTicket updated;
            try
            {
                updated = await RefreshAsync(ticket, refreshToken, ownerTimeout.Token);
            }
            catch (Exception e)
            {
                await InvalidateAsync(id);
                await RevokeAsync(ticket, CancellationToken.None);
                logger.LogWarning("BFF refresh failed; session cleanup requested. Failure type: {FailureType}", e.GetType().Name);
                if (e is ReauthenticationRequiredException) return null;
                throw new SessionUnavailableException("The identity provider could not complete token refresh.", e);
            }

            try
            {
                for (var completionAttempt = 0; completionAttempt < 16; completionAttempt++)
                {
                    var latest = await store.ReadAsync(id, ownerTimeout.Token);
                    if (latest is null || latest.State.RefreshOperation != operation || latest.State.RefreshDeadline <= timeProvider.GetUtcNow())
                    {
                        await RevokeAsync(updated, CancellationToken.None);
                        return null;
                    }
                    var completedAt = timeProvider.GetUtcNow();
                    var idleUntil = completedAt + client.Options.SessionIdleTimeout;
                    var completed = latest.State with
                    {
                        Ticket = Convert.ToBase64String(TicketSerializer.Default.Serialize(updated)),
                        IdleExpiresAt = idleUntil < latest.State.AbsoluteExpiresAt ? idleUntil : latest.State.AbsoluteExpiresAt,
                        RefreshOperation = null,
                        RefreshDeadline = null,
                        RefreshAt = StoredSession.CalculateRefreshAt(DateTimeOffset.Parse(updated.Properties.GetTokenValue("expires_at")!,
                            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), completedAt)
                    };
                    if (await store.CompareExchangeAsync(latest, completed, ownerTimeout.Token)) return updated;
                }
                throw new SessionUnavailableException("Session changed repeatedly while completing refresh.");
            }
            catch (Exception e)
            {
                await InvalidateAsync(id);
                await RevokeAsync(updated, CancellationToken.None);
                throw new SessionUnavailableException("Unable to persist the rotated tokens.", e);
            }
        }
        throw new SessionUnavailableException("Session contention exceeded the bounded retry limit.");
    }

    private async Task<bool> TryClaimAsync(SessionSnapshot snapshot, StoredSession claimed, CancellationToken ct)
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
            if (latest is not null && latest.State.RefreshOperation == claimed.RefreshOperation &&
                latest.State.RefreshDeadline > timeProvider.GetUtcNow()) return true;
            throw new SessionUnavailableException("Unable to confirm refresh ownership after a Redis failure.", e);
        }
    }

    private async Task<AuthenticationTicket> RefreshAsync(AuthenticationTicket previous, string refreshToken, CancellationToken ct)
    {
        var oidc = oidcOptions.Get(client.OpenIdConnectScheme);
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(ct);
        if (string.IsNullOrEmpty(configuration.TokenEndpoint))
            throw new SessionUnavailableException("The identity provider has no token endpoint.");
        var response = await oidc.Backchannel.RequestRefreshTokenAsync(new RefreshTokenRequest
        {
            Address = configuration.TokenEndpoint!,
            ClientId = oidc.ClientId!,
            ClientSecret = oidc.ClientSecret,
            ClientCredentialStyle = ClientCredentialStyle.PostBody,
            RefreshToken = refreshToken
        }, ct);
        if (response.IsError)
        {
            if (response.Error == "invalid_grant") throw new ReauthenticationRequiredException();
            throw new SessionUnavailableException("The identity provider rejected token refresh.");
        }
        try
        {
            if (string.IsNullOrWhiteSpace(response.AccessToken) ||
                !string.Equals(response.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                response.ExpiresIn <= 0)
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
                parameters.ConfigurationManager = oidc.ConfigurationManager as BaseConfigurationManager;
                var handler = new JsonWebTokenHandler { MapInboundClaims = false };
                var validated = await handler.ValidateTokenAsync(response.IdentityToken, parameters);
                if (!validated.IsValid && validated.Exception is SecurityTokenSignatureKeyNotFoundException &&
                    oidc.RefreshOnIssuerKeyNotFound && !string.IsNullOrEmpty(oidc.MetadataAddress))
                {
                    // ConfigurationManager refreshes in the background. A newly issued
                    // rotating token cannot wait for a later browser retry. Retrieve the
                    // configured HTTPS discovery/JWKS once and revalidate this same token;
                    // never repeat its refresh-token redemption.
                    var updatedConfiguration = await OpenIdConnectConfigurationRetriever.GetAsync(
                        oidc.MetadataAddress, oidc.Backchannel, ct);
                    var updatedParameters = parameters.Clone();
                    updatedParameters.ConfigurationManager = null;
                    updatedParameters.IssuerSigningKeys = updatedConfiguration.SigningKeys;
                    validated = await handler.ValidateTokenAsync(response.IdentityToken, updatedParameters);
                }
                if (!validated.IsValid) throw new ReauthenticationRequiredException();
                var validatedJwt = (JsonWebToken)validated.SecurityToken;
                var refreshedIdToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                    (validatedJwt.InnerToken ?? validatedJwt).EncodedToken);
                var nonce = refreshedIdToken.Payload.Nonce;
                previous.Properties.Items.TryGetValue(BffConstants.OriginalNonce, out var originalNonce);
                previous.Properties.Items.TryGetValue(BffConstants.OriginalAuthTime, out var originalAuthTime);
                if (nonce is not null && nonce != originalNonce)
                    throw new ReauthenticationRequiredException();
                if (refreshedIdToken.Payload.TryGetValue("auth_time", out var authTime) &&
                    originalAuthTime != Convert.ToString(authTime, CultureInfo.InvariantCulture))
                    throw new ReauthenticationRequiredException();
                try
                {
                    new OpenIdConnectProtocolValidator { RequireNonce = false, RequireTimeStampInNonce = false }
                        .ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
                        {
                            ClientId = oidc.ClientId,
                            Nonce = nonce,
                            ProtocolMessage = new OpenIdConnectMessage { IdToken = response.IdentityToken, AccessToken = response.AccessToken },
                            ValidatedIdToken = refreshedIdToken
                        });
                }
                catch (OpenIdConnectProtocolException)
                {
                    throw new ReauthenticationRequiredException();
                }
                var identity = validated.ClaimsIdentity;
                if (identity.FindFirst("sub")?.Value != previous.Principal.FindFirst("sub")?.Value ||
                    string.IsNullOrEmpty(identity.FindFirst("sub")?.Value))
                    throw new ReauthenticationRequiredException();
                principal = new ClaimsPrincipal(identity);
            }

            var tokens = previous.Properties.GetTokens().ToDictionary(t => t.Name, t => t.Value, StringComparer.Ordinal);
            tokens["access_token"] = response.AccessToken;
            tokens["expires_at"] = (timeProvider.GetUtcNow() + TimeSpan.FromSeconds(response.ExpiresIn)).ToString("O", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(response.RefreshToken)) tokens["refresh_token"] = response.RefreshToken;
            if (!string.IsNullOrEmpty(response.IdentityToken)) tokens["id_token"] = response.IdentityToken;
            previous.Properties.StoreTokens(tokens.Select(t => new AuthenticationToken { Name = t.Key, Value = t.Value }));
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
            logger.LogError("Unable to invalidate BFF session after refresh failure. Failure type: {FailureType}", e.GetType().Name);
        }
    }

    internal Task RevokeAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
        => RevokeAsync(ticket.Properties.GetTokenValue("refresh_token"), cancellationToken);

    internal async Task RevokeAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var oidc = oidcOptions.Get(client.OpenIdConnectScheme);
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(timeout.Token);
            var endpoint = configuration.RevocationEndpoint;
            if (string.IsNullOrEmpty(endpoint)) return;
            var response = await oidc.Backchannel.RevokeTokenAsync(new TokenRevocationRequest
            {
                Address = endpoint,
                ClientId = oidc.ClientId!,
                ClientSecret = oidc.ClientSecret,
                ClientCredentialStyle = ClientCredentialStyle.PostBody,
                Token = token,
                TokenTypeHint = "refresh_token"
            }, timeout.Token);
            if (response.IsError) logger.LogWarning("Identity provider rejected best-effort BFF token revocation.");
        }
        catch (Exception e)
        {
            logger.LogWarning("Best-effort BFF token revocation failed. Failure type: {FailureType}", e.GetType().Name);
        }
    }
}

internal sealed class SessionUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
internal sealed class ReauthenticationRequiredException : Exception;
