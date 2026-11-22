/// <summary>
/// ConsentPageTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System.Collections.Generic;

    [TestFixture]
    public class ConsentPageTests
    {
        private const string Action = "https://safeexchange.test/api/oauth/authorize";

        [Test]
        public void Render_IsAFormPostingEveryFieldBackWithAllowAndDenyButtons()
        {
            var html = ConsentPage.Render(Action, "http://127.0.0.1:53682", new KeyValuePair<string, string>[]
            {
                new("client_id", "00000000-0000-0000-0000-0000000000c9"),
                new("state", "abc"),
            });

            Assert.That(html, Does.StartWith("<!DOCTYPE html>"));
            Assert.That(html, Does.Contain("<title>Sign in an MCP client to SafeExchange</title>"));
            Assert.That(html, Does.Contain("<h1>Sign in an MCP client to SafeExchange</h1>"));
            Assert.That(html, Does.Contain("An application on this computer is asking for access to SafeExchange as you. It will receive the result at <b>http://127.0.0.1:53682</b>."));
            Assert.That(html, Does.Contain("Access requested: read and manage your secrets; stay signed in."));
            Assert.That(html, Does.Contain("Only continue if you just started a sign-in in an MCP client such as Copilot CLI."));
            Assert.That(html, Does.Contain($"<form method=\"post\" action=\"{Action}\">"));
            Assert.That(html, Does.Contain("<input type=\"hidden\" name=\"client_id\" value=\"00000000-0000-0000-0000-0000000000c9\">"
                + "<input type=\"hidden\" name=\"state\" value=\"abc\">"));
            Assert.That(html, Does.Contain("<button type=\"submit\" name=\"decision\" value=\"allow\">"));
            Assert.That(html, Does.Contain("<button type=\"submit\" name=\"decision\" value=\"deny\">"));
            Assert.That(html, Does.Not.Contain("<script"));
        }

        [Test]
        public void Render_HtmlEncodesEveryValue()
        {
            const string Hostile = "\"><script>alert('x')</script>&";

            var html = ConsentPage.Render(Action + "?" + Hostile, "http://127.0.0.1:1" + Hostile, new KeyValuePair<string, string>[] { new("state" + Hostile, Hostile) });

            Assert.That(html, Does.Not.Contain("<script>"));
            Assert.That(html, Does.Not.Contain("'x'"));
            Assert.That(html, Does.Contain("&quot;&gt;&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;&amp;"));
        }

        [Test]
        public void Message_IsAPlainPageWithTheEncodedText()
        {
            var html = ConsentPage.Message("Wait <a minute>.");

            Assert.That(html, Does.StartWith("<!DOCTYPE html>"));
            Assert.That(html, Does.Contain("<p>Wait &lt;a minute&gt;.</p>"));
            Assert.That(html, Does.Not.Contain("<form"));
        }
    }
}
