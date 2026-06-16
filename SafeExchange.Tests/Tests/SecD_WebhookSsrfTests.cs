namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Utilities;

    /// <summary>
    /// Cluster D — CWE-918 SSRF. Verifies that attacker-supplied webhook URLs that
    /// point at loopback, link-local (incl. cloud metadata 169.254.169.254), or
    /// RFC1918 private hosts, or that are not HTTPS, are rejected by the URL safety
    /// validator, while normal public HTTPS URLs are allowed.
    /// </summary>
    [TestFixture]
    public class SecD_WebhookSsrfTests
    {
        [TestCase("http://169.254.169.254/latest/meta-data/")] // cloud metadata (link-local) + non-TLS
        [TestCase("http://127.0.0.1")]                          // loopback
        [TestCase("http://10.0.0.5")]                           // RFC1918 10/8
        [TestCase("http://192.168.1.1")]                        // RFC1918 192.168/16
        [TestCase("http://localhost")]                          // loopback hostname
        [TestCase("http://example.com/hook")]                   // non-TLS public host
        public void TryValidate_RejectsDisallowedUrls(string url)
        {
            var ok = WebhookUrlValidator.TryValidate(url, out var reason);
            Assert.That(ok, Is.False, $"Expected '{url}' to be rejected.");
            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("https://example.com/hook")]
        [TestCase("https://example.org/v1/notify")]
        public void TryValidate_AllowsPublicHttpsUrls(string url)
        {
            var ok = WebhookUrlValidator.TryValidate(url, out var reason);
            Assert.That(ok, Is.True, $"Expected '{url}' to be accepted. Reason: {reason}");
            Assert.That(reason, Is.Null);
        }

        [TestCase("https://169.254.169.254/x")]   // https but link-local metadata
        [TestCase("https://127.0.0.1/x")]         // https but loopback
        [TestCase("https://10.1.2.3/x")]          // https but private
        [TestCase("https://172.16.5.4/x")]        // https but private 172.16/12
        [TestCase("https://[::1]/x")]             // https but IPv6 loopback
        [TestCase("https://[fc00::1]/x")]         // https but IPv6 ULA
        public void TryValidate_RejectsPrivateHostsEvenWithHttps(string url)
        {
            var ok = WebhookUrlValidator.TryValidate(url, out var reason);
            Assert.That(ok, Is.False, $"Expected '{url}' to be rejected.");
            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not a url")]
        [TestCase("ftp://example.com/x")]
        public void TryValidate_RejectsMalformedOrNonHttp(string? url)
        {
            var ok = WebhookUrlValidator.TryValidate(url, out var reason);
            Assert.That(ok, Is.False, $"Expected '{url}' to be rejected.");
            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }
    }
}
