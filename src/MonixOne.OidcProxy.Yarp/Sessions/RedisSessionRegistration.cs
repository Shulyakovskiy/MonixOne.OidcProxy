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
        services.AddSingleton<SessionStore>();
        services.AddSingleton<TokenSession>();
        services.AddDataProtection();
        services.AddOptions<DataProtectionOptions>().Configure<ProxyOptions>((protection, options) =>
            protection.ApplicationDiscriminator =
                $"MonixOne.OidcProxy.Yarp.v6:{options.CookieName}:{options.RedisKeyPrefix}");
        services.AddOptions<KeyManagementOptions>()
            .Configure<ConnectionMultiplexer, ProxyOptions>((keys, connection, options) =>
                keys.XmlRepository = new RedisXmlRepository(() => connection.GetDatabase(),
                    options.RedisKeyPrefix + "data-protection"));
    }
}