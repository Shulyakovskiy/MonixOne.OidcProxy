using System.Diagnostics;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MonixOne.OidcProxy.Yarp.Forwarding;

internal sealed class GatewayTraceTransformProvider : ITransformProvider
{
    internal const string CorrelationHeader = "x-correlation-id";

    public void ValidateRoute(TransformRouteValidationContext context) { }

    public void ValidateCluster(TransformClusterValidationContext context) { }

    public void Apply(TransformBuilderContext context)
    {
        context.AddRequestTransform(request =>
        {
            // Never forward the client's parent span as the gateway's outbound context.
            var headers = request.ProxyRequest.Headers;
            headers.Remove(CorrelationHeader);
            headers.TryAddWithoutValidation(
                CorrelationHeader,
                (Activity.Current?.TraceId.ToString() ?? request.HttpContext.TraceIdentifier)
            );
            headers.Remove("traceparent");
            headers.Remove("tracestate");
            headers.Remove("baggage");

            DistributedContextPropagator.Current.Inject(
                Activity.Current,
                headers,
                static (carrier, name, value) =>
                    ((System.Net.Http.Headers.HttpRequestHeaders)carrier!).TryAddWithoutValidation(
                        name,
                        value
                    )
            );

            return ValueTask.CompletedTask;
        });
        context.AddResponseTransform(response =>
        {
            // A downstream response must not replace the gateway's trace identifier.
            response.HttpContext.Response.Headers[CorrelationHeader] = (
                Activity.Current?.TraceId.ToString() ?? response.HttpContext.TraceIdentifier
            );
            return ValueTask.CompletedTask;
        });
    }
}
