/// <summary>
/// ContentStatus
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public enum ContentStatus
    {
        /// <summary>
        /// Content has been created as metadata, but actual data is not uploaded yet.
        /// </summary>
        Blank = 0,

        /// <summary>
        /// Content data is being updated.
        /// </summary>
        Updating = 100,

        /// <summary>
        /// Content data is ready to use.
        /// </summary>
        Ready = 200
    }
}
