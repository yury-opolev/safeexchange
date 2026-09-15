/// <summary>
/// DevModeCosmosTlsTests — regression guard for the SAEX_DEV_MODE Cosmos wiring.
///
/// The development path used to hand the Cosmos gateway an HttpClient built on
/// <c>HttpClientHandler.DangerousAcceptAnyServerCertificateValidator</c> so the
/// emulator's self-signed certificate would be accepted. That callback is
/// unconditional: it also accepts an untrusted certificate or a hostname
/// mismatch from any endpoint the configuration happens to point at, so the
/// connection had no server authentication at all.
///
/// These tests exercise the configuration produced by the real startup code —
/// not a standalone handler — and assert that the emulator connection now
/// authenticates its server like any other TLS connection.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Cosmos.Infrastructure.Internal;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core;
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Net;
    using System.Net.Http;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;

    [TestFixture]
    [NonParallelizable]
    public class DevModeCosmosTlsTests
    {
        private const string DevModeVariable = "SAEX_DEV_MODE";

        /// <summary>Not a credential — the dev path only requires a non-blank value, and no Cosmos client is ever created here.</summary>
        private const string PlaceholderPrimaryKey = "placeholder-not-a-real-key";

        private const string LoopbackEndpoint = "https://localhost:8081";

        private string? originalDevMode;

        [SetUp]
        public void Setup()
        {
            this.originalDevMode = Environment.GetEnvironmentVariable(DevModeVariable);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(DevModeVariable, this.originalDevMode);
        }

        [Test]
        public void DevMode_InstallsNoCustomHttpHandler()
        {
            var extension = CosmosExtension(devMode: true, endpoint: LoopbackEndpoint, primaryKey: PlaceholderPrimaryKey);

            // No factory at all means no place for a validation bypass to live, and
            // no insecure handler to fall back to: the Cosmos SDK's default client
            // applies ordinary platform certificate validation.
            Assert.That((object?)extension.HttpClientFactory, Is.Null);
        }

        [Test]
        public void DevMode_RejectsUntrustedServerCertificate()
        {
            using var certificate = CreateSelfSignedCertificate(
                subject: "127.0.0.1",
                configureSubjectAlternativeNames: san => san.AddIpAddress(IPAddress.Loopback));

            using var server = new LoopbackTlsServer(certificate);
            using var client = CreateDevCosmosHttpClient();

            var error = Assert.ThrowsAsync<HttpRequestException>(
                async () => await client.GetAsync($"https://127.0.0.1:{server.Port}/"));

            Assert.That(InnerAuthenticationException(error), Is.Not.Null, "The connection must fail TLS validation, not merely fail.");
        }

        [Test]
        public void DevMode_RejectsCertificateThatDoesNotMatchTheHost()
        {
            // Issued for 'localhost' but served on 127.0.0.1. The certificate is
            // self-signed as well, so this asserts that a name-mismatching
            // certificate is rejected without needing a machine-wide trust change.
            using var certificate = CreateSelfSignedCertificate(
                subject: "localhost",
                configureSubjectAlternativeNames: san => san.AddDnsName("localhost"));

            using var server = new LoopbackTlsServer(certificate);
            using var client = CreateDevCosmosHttpClient();

            var error = Assert.ThrowsAsync<HttpRequestException>(
                async () => await client.GetAsync($"https://127.0.0.1:{server.Port}/"));

            Assert.That(InnerAuthenticationException(error), Is.Not.Null, "The connection must fail TLS validation, not merely fail.");
        }

        [Test]
        public void DevMode_RequiresLoopbackCosmosEndpoint()
        {
            var error = Assert.Throws<ConfigurationErrorsException>(
                () => CosmosExtension(devMode: true, endpoint: "https://safeexchange-test.documents.azure.com:443/", primaryKey: PlaceholderPrimaryKey));

            Assert.That(error!.Message, Does.Contain("CosmosDb:CosmosDbEndpoint"));
        }

        [Test]
        public void DevMode_RequiresPrimaryKey()
        {
            var error = Assert.Throws<ConfigurationErrorsException>(
                () => CosmosExtension(devMode: true, endpoint: LoopbackEndpoint, primaryKey: null));

            Assert.That(error!.Message, Does.Contain("CosmosDb:PrimaryKey"));
        }

        [Test]
        public void WithoutDevMode_UsesCredentialPathAndNeedsNoPrimaryKey()
        {
            var extension = CosmosExtension(devMode: false, endpoint: "https://safeexchange-test.documents.azure.com:443/", primaryKey: null);

            Assert.Multiple(() =>
            {
                Assert.That(extension.TokenCredential, Is.Not.Null);
                Assert.That(extension.AccountKey, Is.Null);
                Assert.That((object?)extension.HttpClientFactory, Is.Null);
            });
        }

        /// <summary>
        /// The HttpClient the development configuration yields. When that configuration
        /// installs a factory the client comes from it; when it installs none — as it
        /// must, so that no bypass can live there — the Cosmos SDK builds a default
        /// HttpClient, which is what is mirrored here.
        /// </summary>
        private static HttpClient CreateDevCosmosHttpClient()
        {
            var extension = CosmosExtension(devMode: true, endpoint: LoopbackEndpoint, primaryKey: PlaceholderPrimaryKey);
            var client = extension.HttpClientFactory?.Invoke() ?? new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        /// <summary>
        /// Runs the real <see cref="SafeExchangeStartup.ConfigureServices"/> and returns
        /// the Cosmos options it registered, so the assertions above are made against
        /// production wiring rather than a reconstruction of it.
        /// </summary>
        private static CosmosOptionsExtension CosmosExtension(bool devMode, string endpoint, string? primaryKey)
        {
            Environment.SetEnvironmentVariable(DevModeVariable, devMode ? "true" : null);

            var settings = new Dictionary<string, string?>
            {
                ["CosmosDb:CosmosDbEndpoint"] = endpoint,
                ["CosmosDb:DatabaseName"] = "SafeExchangeDevModeTests",
            };

            if (primaryKey is not null)
            {
                settings["CosmosDb:PrimaryKey"] = primaryKey;
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            SafeExchangeStartup.ConfigureServices(configuration, services);

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<DbContextOptions<SafeExchangeDbContext>>();
            return options.FindExtension<CosmosOptionsExtension>()
                ?? throw new InvalidOperationException("No Cosmos options were registered by startup.");
        }

        private static AuthenticationException? InnerAuthenticationException(Exception? error)
        {
            for (var current = error; current is not null; current = current.InnerException)
            {
                if (current is AuthenticationException authenticationException)
                {
                    return authenticationException;
                }
            }

            return null;
        }

        private static X509Certificate2 CreateSelfSignedCertificate(string subject, Action<SubjectAlternativeNameBuilder> configureSubjectAlternativeNames)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            configureSubjectAlternativeNames(subjectAlternativeNames);
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));

            using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // SChannel refuses an ephemeral key when acting as a TLS server, so the
            // certificate is round-tripped through PKCS#12 to get a usable key handle.
            const string passphrase = "loopback-tls-test";
            return X509CertificateLoader.LoadPkcs12(
                ephemeral.Export(X509ContentType.Pfx, passphrase), passphrase, X509KeyStorageFlags.Exportable);
        }

        /// <summary>
        /// A TLS endpoint on loopback that presents the given certificate and answers
        /// 200 OK to any client that accepts it. A client that validates the server
        /// certificate never gets that far.
        /// </summary>
        private sealed class LoopbackTlsServer : IDisposable
        {
            private static readonly byte[] Response =
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();

            private readonly TcpListener listener;
            private readonly CancellationTokenSource cancellation = new();
            private readonly Task acceptLoop;

            public LoopbackTlsServer(X509Certificate2 certificate)
            {
                this.listener = new TcpListener(IPAddress.Loopback, 0);
                this.listener.Start();
                this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
                this.acceptLoop = Task.Run(() => this.AcceptAsync(certificate, this.cancellation.Token));
            }

            public int Port { get; }

            public void Dispose()
            {
                this.cancellation.Cancel();
                this.listener.Stop();

                try
                {
                    this.acceptLoop.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // The loop unwinds through the cancelled accept; nothing to report.
                }

                this.cancellation.Dispose();
            }

            private async Task AcceptAsync(X509Certificate2 certificate, CancellationToken token)
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient accepted;
                    try
                    {
                        accepted = await this.listener.AcceptTcpClientAsync(token);
                    }
                    catch (Exception) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        return;
                    }

                    using (accepted)
                    using (var secured = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false))
                    {
                        try
                        {
                            await secured.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false, checkCertificateRevocation: false);

                            var request = new byte[4096];
                            var read = await secured.ReadAsync(request, token);
                            if (read > 0)
                            {
                                await secured.WriteAsync(Response, token);
                                await secured.FlushAsync(token);
                            }
                        }
                        catch (Exception)
                        {
                            // Expected: a client that validates the certificate aborts the handshake.
                        }
                    }
                }
            }
        }
    }
}
