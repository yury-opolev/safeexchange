/// <summary>
/// ContentMetadataCreationInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class ContentMetadataCreationInput
    {
        public string ContentType { get; set; }

        public string FileName { get; set; }

        /// <summary>
        /// True when this attachment is an image (content-type image/*). Image
        /// attachments are rendered with a thumbnail preview in the UI instead of
        /// being embedded inline in the secret's main note.
        /// </summary>
        public bool IsImage { get; set; }
    }
}
