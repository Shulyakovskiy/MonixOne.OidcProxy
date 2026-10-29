using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Sessions;

internal static class RedisSessionRegistration
{
    internal static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.AddOptions<DataProtectionOptions>().Configure<Microsoft.Extensions.Options.IOptions<ProxyOptions>>((protection, host) =>
            protection.ApplicationDiscriminator =
                $"MonixOne.OidcProxy.Yarp.v6:{host.Value.RedisKeyPrefix}");
        services.AddOptions<KeyManagementOptions>()
            .Configure<ConnectionMultiplexer, Microsoft.Extensions.Options.IOptions<ProxyOptions>>((keys, connection, host) =>
                keys.XmlRepository = new RedisXmlRepository(() => connection.GetDatabase(),
                    host.Value.RedisKeyPrefix + "data-protection"));
    }
}