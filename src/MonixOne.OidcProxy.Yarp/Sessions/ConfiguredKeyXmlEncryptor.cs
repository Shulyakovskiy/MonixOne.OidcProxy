using System.Xml.Linq;
using Jose;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MonixOne.OidcProxy.Yarp.Sessions;

/// <summary>
/// Adapts jose-jwt to framework key storage for cookies, OIDC state and antiforgery.
/// Session data uses the same framework key ring through IDataProtector.
/// </summary>
internal sealed class ConfiguredKeyXmlEncryptor(IServiceProvider services)
    : IXmlEncryptor,
        IXmlDecryptor
{
    private readonly string _key = GetKey(services);

    private static string GetKey(IServiceProvider services)
    {
        var key = services.GetRequiredService<IOptions<ProxyOptions>>().Value.EncryptionKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new OptionsValidationException(
                nameof(ProxyOptions),
                typeof(ProxyOptions),
                [
                    "Bff:EncryptionKey must contain a strong random secret, or configure standard Data Protection key encryption such as ProtectKeysWithCertificate.",
                ]
            );
        return key;
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
        new(
            new XElement(
                "jwe",
                JWT.Encode(
                    plaintextElement.ToString(SaveOptions.DisableFormatting),
                    _key,
                    JweAlgorithm.PBES2_HS256_A128KW,
                    JweEncryption.A256GCM
                )
            ),
            typeof(ConfiguredKeyXmlEncryptor)
        );

    public XElement Decrypt(XElement encryptedElement) =>
        XElement.Parse(
            JWT.Decrypt(
                encryptedElement.Value,
                _key,
                JweAlgorithm.PBES2_HS256_A128KW,
                JweEncryption.A256GCM
            )
        );
}
