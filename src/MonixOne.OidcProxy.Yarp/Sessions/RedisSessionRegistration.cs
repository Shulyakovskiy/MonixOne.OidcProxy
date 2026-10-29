using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Sessions;

internal static class RedisSessionRegistration
{
    internal static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ConfiguredKeyXmlEncryptor>();
        services.AddDataProtection();
        services.AddOptions<DataProtectionOptions>().Configure<IOptions<ProxyOptions>>((protection, host) =>
            protection.ApplicationDiscriminator = $"MonixOne.OidcProxy.Yarp.v6:{host.Value.RedisKeyPrefix}");
        services.AddOptions<KeyManagementOptions>()
            .Configure<ConnectionMultiplexer, IOptions<ProxyOptions>, ConfiguredKeyXmlEncryptor>((keys, connection, host, encryptor) =>
            {
                keys.XmlRepository = new RedisXmlRepository(() => connection.GetDatabase(),
                    host.Value.RedisKeyPrefix + "data-protection");
                keys.XmlEncryptor = encryptor;
            })
            .Validate(keys => keys.XmlRepository!.GetAllElements().Where(element => element.Name == "key")
                    .All(key => key.Descendants("jwe").Any() && !key.Descendants("masterKey").Any()),
                "The framework Data Protection key ring must be encrypted. Use a new Bff:KeyNamespace when upgrading from unprotected keys.")
            .ValidateOnStart();
    }
}
