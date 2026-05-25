/// <summary>
/// ContentMetadataModelTests — verifies the IsImage marker (images-as-attachments
/// spike) flows from the creation input into the content entity.
/// </summary>

namespace SafeExchange.Tests.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;

    [TestFixture]
    public class ContentMetadataModelTests
    {
        [Test]
        public void IsImage_FlowsFromCreationInput()
        {
            var image = new ContentMetadata(new ContentMetadataCreationInput
            {
                ContentType = "image/png",
                FileName = "passport.png",
                IsImage = true
            });
            Assert.That(image.IsImage, Is.True);
            Assert.That(image.IsMain, Is.False);

            var file = new ContentMetadata(new ContentMetadataCreationInput
            {
                ContentType = "application/pdf",
                FileName = "doc.pdf",
                IsImage = false
            });
            Assert.That(file.IsImage, Is.False);
        }
    }
}
