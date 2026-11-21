/// <summary>
/// MainContentTextTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Services;

    [TestFixture]
    public class MainContentTextTests
    {
        [Test]
        public void ToHtml_WritesOneEncodedParagraphPerLine()
        {
            Assert.That(MainContentText.ToHtml("a<b\n\nc&d"), Is.EqualTo("<p>a&lt;b</p><p><br></p><p>c&amp;d</p>"));
            Assert.That(MainContentText.ToHtml("one\r\ntwo\rthree"), Is.EqualTo("<p>one</p><p>two</p><p>three</p>"));
        }

        [TestCase("")]
        [TestCase("a")]
        [TestCase("a\nb")]
        [TestCase("a\n\nb")]
        [TestCase("\nleading and trailing\n")]
        [TestCase("  spaced  out  ")]
        [TestCase("p&ss<w>\"'/\\")]
        [TestCase("<script>alert(1)</script>")]
        [TestCase("tab\tand \u00fcn\u00efc\u00f8d\u00e9 \U0001F511 \u00a0nbsp")]
        public void PlainText_SurvivesTheMainContentSanitiser(string text)
        {
            var stored = MainContentSanitizer.Sanitize(MainContentText.ToHtml(text));

            Assert.That(MainContentText.ToText(stored), Is.EqualTo(text));
        }

        [Test]
        public void ToText_ReadsRichTextBlocksAsLines()
        {
            var html = "<p>Hello <strong>world</strong></p><ul><li>one</li><li>two</li></ul><p>a<br>b</p><div>c</div>";

            Assert.That(MainContentText.ToText(html), Is.EqualTo("Hello world\none\ntwo\na\nb\nc"));
        }
    }
}
