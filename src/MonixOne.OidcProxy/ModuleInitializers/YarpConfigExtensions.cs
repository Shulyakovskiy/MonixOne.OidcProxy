using System.Collections.Immutable;
using Yarp.ReverseProxy.Configuration;

// ReSharper disable once CheckNamespace
namespace MonixOne.OidcProxy;

public static class YarpConfigExtensions
{
    public static IReadOnlyList<RouteConfig> ToRouteConfig(this Dictionary<string, RouteConfig> source)
        => source.Select(x => x.ToRouteConfig()).ToImmutableArray();
    
    public static IReadOnlyList<ClusterConfig> ToClusterConfig(this Dictionary<string, ClusterConfig> source)
        => source.Select(x => x.ToClusterConfig()).ToImmutableArray();
    
    private static RouteConfig ToRouteConfig(this KeyValuePair<string, RouteConfig> source)
    {
        return source.Value with { RouteId = source.Key };
    }
    
    public static ClusterConfig ToClusterConfig(this KeyValuePair<string, ClusterConfig> source)
    {
        return source.Value with { ClusterId = source.Key };
    }
}
