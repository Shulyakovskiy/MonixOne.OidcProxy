using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MonixOne.OidcProxy.Yarp.Forwarding;

/// <summary>
/// Keeps browser credentials at the BFF boundary and limits forwarding to
/// explicitly configured HTTPS backends with certificate validation enabled.
/// </summary>
internal sealed class BffTransformProvider(BffClients clients, TimeProvider clock, ILogger<BffTransformProvider> logger) : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context)
    {
        if (context.Route.Metadata?.TryGetValue(OidcProxyDefaults.ClientMetadataKey, out var name) != true)
        {
            if (context.Route.Metadata?.ContainsKey(OidcProxyDefaults.CookiePolicyMetadataKey) == true)
                context.Errors.Add(new ArgumentException("A cookie policy requires a managed BFF client route."));
            return;
        }
        if (name is null || !clients.ByName.TryGetValue(name, out var client))
        {
            context.Errors.Add(new ArgumentException($"Unknown BFF client '{name}' on route '{context.Route.RouteId}'."));
            return;
        }
        if (context.Route.Metadata.TryGetValue(OidcProxyDefaults.CookiePolicyMetadataKey, out var policyName) &&
            (!client.Options.CookiePolicies.TryGetValue(policyName, out var policy) || policy.ClusterId != context.Route.ClusterId))
            context.Errors.Add(new ArgumentException($"Cookie policy '{policyName}' must exist and bind route '{context.Route.RouteId}' to its configured cluster."));
        // Exact host bindings prevent token routing across origins and wildcard hosts.
        if (context.Route.Match.Hosts is not { Count: > 0 } hosts || hosts.Any(h =>
            !string.Equals(h, client.Options.PublicOrigin!.Authority, StringComparison.OrdinalIgnoreCase)))
            context.Errors.Add(new ArgumentException($"BFF route '{context.Route.RouteId}' must match only its client's exact PublicOrigin authority."));
    }

    public void ValidateCluster(TransformClusterValidationContext context)
    {
        if (context.Cluster.HttpClient?.DangerousAcceptAnyServerCertificate == true)
            context.Errors.Add(new ArgumentException("BFF backends require TLS certificate validation."));
        foreach (var destination in context.Cluster.Destinations?.Values ?? [])
            if (!Uri.TryCreate(destination.Address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                context.Errors.Add(new ArgumentException("BFF destinations must be explicit HTTPS addresses without credentials, query or fragment."));
    }

    public void Apply(TransformBuilderContext context)
    {
        if (context.Route.Metadata?.TryGetValue(OidcProxyDefaults.ClientMetadataKey, out var clientName) != true) return;
        var client = clients.ByName[clientName!];
        context.Route.Metadata.TryGetValue(OidcProxyDefaults.CookiePolicyMetadataKey, out var policyName);
        var policy = policyName is null ? null : client.Options.CookiePolicies[policyName];
        context.AddRequestTransform(request =>
        {
            request.ProxyRequest.Headers.Remove("Cookie");
            request.ProxyRequest.Headers.Authorization = null;
            if (policy is not null && !ForwardedCookies.ApplyRequest(request, client, policyName!, policy))
            {
                // YARP short-circuits forwarding when a request transform sets a status.
                request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return ValueTask.CompletedTask;
            }
            var ticket = request.HttpContext.Items[BffConstants.TicketItem] as AuthenticationTicket;
            var token = ticket?.Properties.GetTokenValue("access_token");
            if (!string.IsNullOrEmpty(token))
                request.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return ValueTask.CompletedTask;
        });
        context.AddResponseTransform(response =>
        {
            response.HttpContext.Response.Headers.Remove("Set-Cookie");
            if (policy is not null && !ForwardedCookies.ApplyResponse(response, client, policyName!, policy, clock.GetUtcNow()))
            {
                logger.LogWarning("Invalid downstream cookie headers for BFF client {Client}, policy {Policy}.", client.Name, policyName);
                response.HttpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
                response.HttpContext.Response.ContentLength = null;
                response.SuppressResponseBody = true;
            }
            return ValueTask.CompletedTask;
        });
    }
}
