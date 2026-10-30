using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MonixOne.OidcProxy.Yarp.Forwarding;

/// <summary>
/// Keeps browser credentials at the BFF boundary and limits forwarding to
/// explicitly configured HTTP/HTTPS backends. The host owns the trusted
/// internal network boundary; HTTPS certificate validation stays enabled.
/// </summary>
internal sealed class BffTransformProvider(
    BffClients clients,
    TimeProvider clock,
    ILogger<BffTransformProvider> logger
) : ITransformProvider, IProxyConfigFilter
{
    public void ValidateRoute(TransformRouteValidationContext context)
    {
        if (clients.Find(context.Route) is not { } binding)
            return;
        var client = binding.Client;
        if (
            binding.Options.CookiePolicy is { } policyName
            && (
                !client.Options.CookiePolicies.TryGetValue(policyName, out var policy)
                || policy.ClusterId != context.Route.ClusterId
            )
        )
            context.Errors.Add(
                new ArgumentException(
                    $"Cookie policy '{policyName}' must exist and bind route '{context.Route.RouteId}' to its configured cluster."
                )
            );
        // Exact host bindings prevent token routing across origins and wildcard hosts.
        if (
            context.Route.Match.Hosts is not { Count: > 0 } hosts
            || hosts.Any(h =>
                !string.Equals(
                    h,
                    client.Options.PublicOrigin!.Authority,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
            context.Errors.Add(
                new ArgumentException(
                    $"BFF route '{context.Route.RouteId}' must match only its client's exact PublicOrigin authority."
                )
            );
    }

    public void ValidateCluster(TransformClusterValidationContext context) { }

    public ValueTask<ClusterConfig> ConfigureClusterAsync(
        ClusterConfig cluster,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(cluster);

    public ValueTask<RouteConfig> ConfigureRouteAsync(
        RouteConfig route,
        ClusterConfig? cluster,
        CancellationToken cancellationToken
    )
    {
        if (clients.Find(route) is not { } binding)
            return ValueTask.FromResult(route);
        if (cluster is null)
            throw new ArgumentException("A BFF route requires a configured cluster.");
        if (cluster.HttpClient?.DangerousAcceptAnyServerCertificate == true)
            throw new ArgumentException("BFF backends require TLS certificate validation.");
        foreach (var destination in cluster.Destinations?.Values ?? [])
            if (
                !Uri.TryCreate(destination.Address, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http")
                || uri.UserInfo.Length != 0
                || uri.Query.Length != 0
                || uri.Fragment.Length != 0
            )
                throw new ArgumentException(
                    $"BFF route '{route.RouteId}' requires explicit HTTP/HTTPS destinations without credentials, query or fragment."
                );
        return ValueTask.FromResult(
            binding.Options.AllowAnonymous && string.IsNullOrEmpty(route.AuthorizationPolicy)
                ? route with
                {
                    AuthorizationPolicy = "anonymous",
                }
                : route
        );
    }

    public void Apply(TransformBuilderContext context)
    {
        if (clients.Find(context.Route) is not { } binding)
            return;
        var client = binding.Client;
        var policyName = binding.Options.CookiePolicy;
        var policy = policyName is null ? null : client.Options.CookiePolicies[policyName];
        // Only an explicitly public, exact callback with constrained methods may
        // receive application cookies from a foreign browser origin.
        var allowForeignOriginCookies =
            binding.Options.AllowAnonymous
            && context.Route.Match.Methods is { Count: > 0 }
            && context.Route.Match.Path is { } path
            && RoutePatternFactory.Parse(path).Parameters.Count == 0;
        context.AddRequestTransform(request =>
        {
            request.ProxyRequest.Headers.Remove("Cookie");
            request.ProxyRequest.Headers.Authorization = null;
            var cookiesValid = policy is not null
                ? ForwardedCookies.ApplyRequest(request, client, policyName!, policy)
                : !client.Options.ForwardCookies
                    || ForwardedCookies.ApplyApplicationRequest(
                        request,
                        client,
                        context.Route.ClusterId!,
                        allowForeignOriginCookies
                    );
            if (!cookiesValid)
            {
                // YARP short-circuits forwarding when a request transform sets a status.
                request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return ValueTask.CompletedTask;
            }
            var ticket = request.HttpContext.Items[BffConstants.TicketItem] as AuthenticationTicket;
            var token = ticket?.Properties.GetTokenValue("access_token");
            if (!string.IsNullOrEmpty(token))
                request.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    token
                );
            return ValueTask.CompletedTask;
        });
        context.AddResponseTransform(response =>
        {
            response.HttpContext.Response.Headers.Remove("Set-Cookie");
            var cookiesValid = policy is not null
                ? ForwardedCookies.ApplyResponse(
                    response,
                    client,
                    policyName!,
                    policy,
                    clock.GetUtcNow()
                )
                : !client.Options.ForwardCookies
                    || ForwardedCookies.ApplyApplicationResponse(
                        response,
                        client,
                        context.Route.ClusterId!,
                        clock.GetUtcNow()
                    );
            if (!cookiesValid)
            {
                logger.LogWarning(
                    "Invalid downstream cookie headers for BFF client {Client}, policy {Policy}.",
                    client.Name,
                    policyName ?? "application"
                );
                response.HttpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
                response.HttpContext.Response.ContentLength = null;
                response.SuppressResponseBody = true;
            }
            return ValueTask.CompletedTask;
        });
    }
}
