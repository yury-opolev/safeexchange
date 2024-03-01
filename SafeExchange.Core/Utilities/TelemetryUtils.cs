/// <summary>
/// TelemetryUtils
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using System;
    using System.Text;

    public static class TelemetryUtils
    {
        public static string GetDescription(Exception exception)
        {
            var stringBuilder = new StringBuilder($"{exception.GetType()}: {exception.Message}");
            var currentException = exception.InnerException;
            while (currentException != null)
            {
                stringBuilder.Append($" -> {currentException.GetType()}: {currentException.Message}");
                currentException = currentException.InnerException;
            }

            return stringBuilder.ToString();
        }
    }
}
