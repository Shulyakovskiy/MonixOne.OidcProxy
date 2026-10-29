using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Builders;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace MonixOne.OidcProxy.Yarp.Tests;

[CollectionDefinition("Infrastructure", DisableParallelization = true)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>;

/// <summary>
/// Shares real Redis/PostgreSQL containers and a test-only TLS certificate.
/// Each test uses a unique Redis namespace and OIDC client for isolation.
/// </summary>
public sealed class InfrastructureFixture : IAsyncLifetime
{
    public RedisContainer Redis { get; } = new RedisBuilder("redis:8-alpine")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379)).Build();
    public PostgreSqlContainer PostgreSql { get; } = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public ConnectionMultiplexer Connection { get; private set; } = null!;
    public X509Certificate2 Certificate { get; }
    public string EncryptionKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public InfrastructureFixture()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(Redis.StartAsync(), PostgreSql.StartAsync());
        Connection = await ConnectRedisAsync();
    }

    public Task<ConnectionMultiplexer> ConnectRedisAsync()
    {
        var options = ConfigurationOptions.Parse(Redis.GetConnectionString());
        options.AsyncTimeout = 1000;
        return ConnectionMultiplexer.ConnectAsync(options);
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await Redis.DisposeAsync();
        await PostgreSql.DisposeAsync();
        Certificate.Dispose();
    }
}
