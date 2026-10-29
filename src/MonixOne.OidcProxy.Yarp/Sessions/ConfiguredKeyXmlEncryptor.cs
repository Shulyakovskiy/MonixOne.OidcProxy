using System.Xml.Linq;
using Jose;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MonixOne.OidcProxy.Yarp.Sessions;

/// <summary>
/// Adapts jose-jwt to framework key storage for cookies, OIDC state and antiforgery.
/// Session data is encrypted directly by SessionStore.
/// </summary>
internal sealed class ConfiguredKeyXmlEncryptor(IServiceProvider services) : IXmlEncryptor, IXmlDecryptor
{
    private readonly string _key = services.GetRequiredService<IOptions<ProxyOptions>>().Value.EncryptionKey;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
        => new(new XElement("jwe", JWT.Encode(plaintextElement.ToString(SaveOptions.DisableFormatting),
            _key, JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM)), typeof(ConfiguredKeyXmlEncryptor));

    public XElement Decrypt(XElement encryptedElement)
        => XElement.Parse(JWT.Decrypt(encryptedElement.Value, _key,
            JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM));
}
