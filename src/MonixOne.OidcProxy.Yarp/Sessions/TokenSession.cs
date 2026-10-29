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

namespace MonixOne.OidcProxy.Yarp.Sessions;

internal sealed class TokenSession(SessionStore store, BffClient client,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions, TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime, ILogger<TokenSession> logger)
{
    internal async Task<AuthenticationTicket?> GetActiveTicketAsync(string id, CancellationToken cancellationToken)
    {
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
            if (expiresAt > now + client.Options.RefreshBeforeExpiration)
            {
                if (snapshot.State.IdleExpiresAt - now < client.Options.SessionIdleTimeout / 2)
                {
                    if (!await store.CompareExchangeAsync(snapshot,
                            snapshot.State with { IdleExpiresAt = now + client.Options.SessionIdleTimeout }, cancellationToken))
                        continue;
                }
                return ticket;
            }

            var refreshToken = ticket.Properties.GetTokenValue("refresh_token");
            if (string.IsNullOrEmpty(refreshToken))
            {
                // A token without offline access remains usable until its actual expiration.
                if (expiresAt > now) return ticket;
                if (await store.CompareExchangeAsync(snapshot, null, cancellationToken)) return null;
                continue;
            }

            var operation = RandomNumberGenerator.GetHexString(32);
            var claimed = snapshot.State with
            {
                RefreshOperation = operation,
                RefreshDeadline = now + client.Options.RefreshTimeout
            };
            if (!await store.CompareExchangeAsync(snapshot, claimed, cancellationToken)) continue;

            // Once claimed, caller disconnection must not interrupt persisting a rotated token.
            using var ownerTimeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            ownerTimeout.CancelAfter(client.Options.RefreshTimeout);
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
                    var completed = latest.State with
                    {
                        Ticket = Convert.ToBase64String(TicketSerializer.Default.Serialize(updated)),
                        IdleExpiresAt = timeProvider.GetUtcNow() + client.Options.SessionIdleTimeout,
                        RefreshOperation = null,
                        RefreshDeadline = null
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
