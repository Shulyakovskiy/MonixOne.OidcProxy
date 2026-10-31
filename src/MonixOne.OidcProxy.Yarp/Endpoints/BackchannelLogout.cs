using System.Globalization;
using System.Net.Mime;
using System.Text.Json;
using Duende.IdentityModel;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MonixOne.OidcProxy.Yarp.Sessions;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Endpoints;

internal static class BackchannelLogout
{
    /// <summary>
    /// Event identifier mandated by OpenID Connect Back-Channel Logout 1.0.
    /// </summary>
    private const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    internal static async Task<IResult> ProcessAsync(HttpContext context, BffClient client)
    {
        var options = client.Options.BackchannelLogout;
        context.Response.Headers.CacheControl = "no-store";
        if (
            context.RequestServices.GetRequiredService<BffClients>().Find(context.Request)?.Name
            != client.Name
        )
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (
            !context.Request.HasFormContentType
            || !context
                .Request.GetTypedHeaders()
                .ContentType!.MediaType.Equals(
                    MediaTypeNames.Application.FormUrlEncoded,
                    StringComparison.OrdinalIgnoreCase
                )
        )
            return Results.BadRequest();
        if (context.Request.ContentLength > options.MaximumRequestBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = options.MaximumRequestBodyBytes;

        var logger = context
            .RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MonixOne.OidcProxy.Yarp.BackchannelLogout");
        try
        {
            var form = await context.Request.ReadFormAsync(
                new FormOptions
                {
                    BufferBody = true,
                    ValueLengthLimit = options.MaximumTokenLength,
                    BufferBodyLengthLimit = options.MaximumRequestBodyBytes,
                    MultipartBodyLengthLimit = options.MaximumRequestBodyBytes,
                },
                context.RequestAborted
            );
            var tokens = form[OidcConstants.BackChannelLogoutRequest.LogoutToken];
            if (
                tokens.Count != 1
                || string.IsNullOrWhiteSpace(tokens[0])
                || tokens[0]!.Length > options.MaximumTokenLength
            )
                return Results.BadRequest();
            var oidc = context
                .RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(client.OpenIdConnectScheme);
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(
                context.RequestAborted
            );
            // Logout has its own protocol boundary. Host ID-token validators must not
            // relax its exact provider/client binding or skip signature validation.
            var validation = new TokenValidationParameters
            {
                ValidIssuer = configuration.Issuer,
                ValidAudience = oidc.ClientId,
                IssuerSigningKeys = configuration.SigningKeys,
                ConfigurationManager = oidc.ConfigurationManager as BaseConfigurationManager,
                TokenDecryptionKey = oidc.TokenValidationParameters.TokenDecryptionKey,
                TokenDecryptionKeys = oidc.TokenValidationParameters.TokenDecryptionKeys,
                ValidAlgorithms = oidc.TokenValidationParameters.ValidAlgorithms,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                RequireSignedTokens = true,
                // exp is optional in the logout specification; iat freshness is checked below.
                RequireExpirationTime = false,
                ClockSkew = options.ClockSkew,
            };
            var handler = new JsonWebTokenHandler
            {
                MapInboundClaims = false,
                MaximumTokenSizeInBytes = options.MaximumTokenLength,
            };
            var validated = await handler.ValidateTokenAsync(tokens[0], validation);
            if (
                !validated.IsValid
                && validated.Exception is SecurityTokenSignatureKeyNotFoundException
                && oidc.RefreshOnIssuerKeyNotFound
            )
            {
                // Use the framework's throttled metadata refresh. Public untrusted JWTs
                // must not trigger an unbounded direct discovery/JWKS download per request.
                oidc.ConfigurationManager.RequestRefresh();
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            if (!validated.IsValid)
                return Results.BadRequest();
            var claims = validated.ClaimsIdentity;
            var subject = claims.FindFirst(JwtClaimTypes.Subject)?.Value;
            var sid = claims.FindFirst(JwtClaimTypes.SessionId)?.Value;
            var jwtId = claims.FindFirst(JwtClaimTypes.JwtId)?.Value;
            if (
                string.IsNullOrWhiteSpace(subject) && string.IsNullOrWhiteSpace(sid)
                || string.IsNullOrWhiteSpace(jwtId)
                || claims.HasClaim(c => c.Type == JwtClaimTypes.Nonce)
                || !long.TryParse(
                    claims.FindFirst(JwtClaimTypes.IssuedAt)?.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var issued
                )
            )
                return Results.BadRequest();
            var now = context
                .RequestServices.GetRequiredService<TimeProvider>()
                .GetUtcNow()
                .ToUnixTimeSeconds();
            if (
                issued < now - (long)options.MaximumTokenAge.TotalSeconds
                || issued > now + (long)options.ClockSkew.TotalSeconds
            )
                return Results.BadRequest();
            using var events = JsonDocument.Parse(
                claims.FindFirst(JwtClaimTypes.Events)?.Value ?? "null"
            );
            if (
                events.RootElement.ValueKind != JsonValueKind.Object
                || !events.RootElement.TryGetProperty(LogoutEvent, out var logout)
                || logout.ValueKind != JsonValueKind.Object
            )
                return Results.BadRequest();

            // Once validated, finish local invalidation even if the IdP disconnects.
            using var timeout = new CancellationTokenSource(options.ProcessingTimeout);
            var store = context.RequestServices.GetRequiredKeyedService<SessionStore>(client.Name);
            await store.RevokeSessionsAsync(
                configuration.Issuer,
                string.IsNullOrWhiteSpace(subject) ? null : subject,
                string.IsNullOrWhiteSpace(sid) ? null : sid,
                jwtId,
                timeout.Token
            );
            return Results.Ok();
        }
        catch (Exception e)
            when (e
                    is JsonException
                        or InvalidDataException
                        or BadHttpRequestException
                        or IOException
            )
        {
            return Results.BadRequest();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
            when (e
                    is RedisException
                        or RedisTimeoutException
                        or SessionUnavailableException
                        or OperationCanceledException
                        or HttpRequestException
                        or InvalidOperationException
            )
        {
            logger.LogWarning(
                "Back-channel logout could not complete. Failure type: {FailureType}",
                e.GetType().Name
            );
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
