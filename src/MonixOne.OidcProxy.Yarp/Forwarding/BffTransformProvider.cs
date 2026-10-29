using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MonixOne.OidcProxy.Yarp.Forwarding;

/// <summary>
/// Keeps browser credentials at the BFF boundary and limits forwarding to
/// explicitly configured HTTPS backends with certificate validation enabled.
/// </summary>
internal sealed class BffTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context) { }

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
        context.AddRequestTransform(request =>
        {
            request.ProxyRequest.Headers.Remove("Cookie");
            request.ProxyRequest.Headers.Authorization = null;
            var ticket = request.HttpContext.Items[BffConstants.TicketItem] as AuthenticationTicket;
            var token = ticket?.Properties.GetTokenValue("access_token");
            if (!string.IsNullOrEmpty(token))
                request.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return ValueTask.CompletedTask;
        });
        context.AddResponseTransform(response =>
        {
            response.HttpContext.Response.Headers.Remove("Set-Cookie");
            return ValueTask.CompletedTask;
        });
    }
}
