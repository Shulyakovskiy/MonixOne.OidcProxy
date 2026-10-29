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
using MonixOne.OidcProxy.ModuleInitializers;

namespace MonixOne.OidcProxy.Sessions;

internal sealed class TokenSession(SessionStore store, ProxyOptions options,
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
                if (timeProvider.GetElapsedTime(waitStarted) >= options.RefreshTimeout)
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
            if (expiresAt > now + options.RefreshBeforeExpiration)
            {
                if (snapshot.State.IdleExpiresAt - now < options.SessionIdleTimeout / 2)
                {
                    if (!await store.CompareExchangeAsync(snapshot,
                            snapshot.State with { IdleExpiresAt = now + options.SessionIdleTimeout }, cancellationToken))
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
                RefreshDeadline = now + options.RefreshTimeout
            };
            if (!await store.CompareExchangeAsync(snapshot, claimed, cancellationToken)) continue;

            // Once claimed, caller disconnection must not interrupt persisting a rotated token.
            using var ownerTimeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            ownerTimeout.CancelAfter(options.RefreshTimeout);
            AuthenticationTicket updated;
            try
            {
                updated = await RefreshAsync(ticket, refreshToken, ownerTimeout.Token);
            }
            catch (Exception e)
            {
                await InvalidateOperationAsync(id, operation);
                logger.LogWarning("BFF refresh failed; server session invalidated. Failure type: {FailureType}", e.GetType().Name);
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
                        IdleExpiresAt = timeProvider.GetUtcNow() + options.SessionIdleTimeout,
                        RefreshOperation = null,
                        RefreshDeadline = null
                    };
                    if (await store.CompareExchangeAsync(latest, completed, ownerTimeout.Token)) return updated;
                }
                throw new SessionUnavailableException("Session changed repeatedly while completing refresh.");
            }
            catch (Exception e)
            {
                await InvalidateOperationAsync(id, operation);
                await RevokeAsync(updated, CancellationToken.None);
                throw new SessionUnavailableException("Unable to persist the rotated tokens.", e);
            }
        }
        throw new SessionUnavailableException("Session contention exceeded the bounded retry limit.");
    }

    private async Task<AuthenticationTicket> RefreshAsync(AuthenticationTicket previous, string refreshToken, CancellationToken ct)
    {
        var oidc = oidcOptions.Get(BffConstants.OidcScheme);
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
                var validated = await new JsonWebTokenHandler { MapInboundClaims = false }
                    .ValidateTokenAsync(response.IdentityToken, parameters);
                if (!validated.IsValid) throw new ReauthenticationRequiredException();
                var refreshedIdToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(response.IdentityToken);
                var previousIdToken = previous.Properties.GetTokenValue("id_token") is { Length: > 0 } oldIdToken
                    ? new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(oldIdToken) : null;
                var nonce = refreshedIdToken.Payload.Nonce;
                if (nonce is not null && nonce != previousIdToken?.Payload.Nonce)
                    throw new ReauthenticationRequiredException();
                if (refreshedIdToken.Payload.TryGetValue("auth_time", out var authTime) &&
                    previousIdToken?.Payload.GetValueOrDefault("auth_time")?.ToString() != authTime?.ToString())
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
            return new AuthenticationTicket(principal, previous.Properties, BffConstants.CookieScheme);
        }
        catch
        {
            // Successful redemption can issue a new refresh token even when subsequent
            // token validation fails. Revoke the known credential before discarding it.
            var rejected = new AuthenticationProperties();
            rejected.StoreTokens([new AuthenticationToken
            {
                Name = "refresh_token", Value = response.RefreshToken ?? refreshToken
            }]);
            await RevokeAsync(new AuthenticationTicket(previous.Principal, rejected, BffConstants.CookieScheme), CancellationToken.None);
            throw;
        }
    }

    private async Task InvalidateOperationAsync(string id, string operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var latest = await store.ReadAsync(id, timeout.Token);
            if (latest?.State.RefreshOperation == operation)
                await store.CompareExchangeAsync(latest, null, timeout.Token);
        }
        catch (Exception e)
        {
            // The persisted pending marker still prevents re-use of the old refresh token.
            logger.LogError("Unable to invalidate pending BFF refresh. Failure type: {FailureType}", e.GetType().Name);
        }
    }

    internal async Task RevokeAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var token = ticket.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrEmpty(token)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var oidc = oidcOptions.Get(BffConstants.OidcScheme);
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
