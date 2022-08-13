/// <summary>
/// BaseResponseObject
/// </summary>

namespace SafeExchange.Core
{
    using System;

    public class BaseResponseObject<T>
    {
        public string Status { get; set; } = string.Empty;

        public string SubStatus { get; set; } = string.Empty;

        public T? Result { get; set; }

        public string? Error { get; set; }

    }
}
