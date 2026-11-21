/// <summary>
/// McpArguments
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using System;
    using System.Globalization;
    using System.Text.Json;

    /// <summary>
    /// Typed, validating access to a <c>tools/call</c> <c>arguments</c> object. Every accessor throws
    /// <see cref="McpArgumentException"/> with a message naming the argument when the value has the wrong type.
    /// </summary>
    public sealed class McpArguments
    {
        private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

        private readonly JsonElement arguments;

        public McpArguments(JsonElement arguments)
        {
            this.arguments = arguments.ValueKind == JsonValueKind.Object ? arguments : EmptyObject;
        }

        public static McpArguments Empty { get; } = new(default);

        public string RequiredString(string name)
        {
            var value = this.OptionalString(name);
            return string.IsNullOrEmpty(value)
                ? throw new McpArgumentException($"'{name}' is required.")
                : value;
        }

        public string? OptionalString(string name)
        {
            if (!this.TryGet(name, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new McpArgumentException($"'{name}' must be a string.");
        }

        public int RequiredNonNegativeInt(string name)
            => this.OptionalNonNegativeInt(name) ?? throw new McpArgumentException($"'{name}' is required.");

        public int? OptionalNonNegativeInt(string name)
        {
            if (!this.TryGet(name, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0
                ? number
                : throw new McpArgumentException($"'{name}' must be a non-negative integer.");
        }

        /// <summary>
        /// <c>true</c> only for a JSON <c>true</c>; a missing value is <c>false</c>.
        /// </summary>
        public bool Flag(string name)
        {
            if (!this.TryGet(name, out var value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new McpArgumentException($"'{name}' must be a boolean."),
            };
        }

        public DateTime? OptionalUtcDateTime(string name)
        {
            var value = this.OptionalString(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : throw new McpArgumentException($"'{name}' must be an ISO 8601 date-time.");
        }

        /// <summary>
        /// Binds the whole arguments object to a REST input DTO with the REST serializer settings, so field names
        /// and defaults are the REST ones. Unknown arguments are ignored.
        /// </summary>
        public T Bind<T>() where T : class
            => Deserialize<T>(this.arguments, "The arguments")
                ?? throw new McpArgumentException("The arguments must be an object.");

        /// <summary>
        /// Binds one argument with the REST serializer settings; <c>null</c> when it is missing or JSON null.
        /// </summary>
        public T? BindProperty<T>(string name) where T : class
            => this.TryGet(name, out var value) ? Deserialize<T>(value, $"'{name}'") : null;

        private bool TryGet(string name, out JsonElement value)
        {
            if (this.arguments.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            {
                return true;
            }

            value = default;
            return false;
        }

        private static T? Deserialize<T>(JsonElement value, string subject) where T : class
        {
            try
            {
                return DefaultJsonSerializer.Deserialize<T>(value.GetRawText());
            }
            catch (JsonException exception)
            {
                throw new McpArgumentException($"{subject} must match the input schema: {exception.Message}");
            }
        }
    }
}
