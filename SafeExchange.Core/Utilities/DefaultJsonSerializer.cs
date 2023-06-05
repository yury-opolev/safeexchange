/// <summary>
/// DefaultJsonSerializer
/// </summary>

namespace SafeExchange.Core
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public static class DefaultJsonSerializer
    {
        private static readonly Lazy<JsonSerializerOptions> JsonOptions = new Lazy<JsonSerializerOptions>(
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

        public static T? Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, options: DefaultJsonSerializer.JsonOptions.Value);
        }

        public static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, options: DefaultJsonSerializer.JsonOptions.Value);
        }
    }
}
