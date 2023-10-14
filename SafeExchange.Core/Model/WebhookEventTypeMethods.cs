
namespace SafeExchange.Core.Model
{
    using System;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;

    public static class WebhookEventTypeMethods
	{
        public static WebhookEventType ToModel(this WebhookEventTypeInput source)
        {
            switch (source)
            {
                case WebhookEventTypeInput.AccessRequestCreated:
                    return WebhookEventType.AccessRequestCreated;

                default:
                    throw new ArgumentException($"Cannot convert {source} to model.");
            }
        }

        public static WebhookEventTypeOutput ToDto(this WebhookEventType source)
        {
            switch (source)
            {
                case WebhookEventType.AccessRequestCreated:
                    return WebhookEventTypeOutput.AccessRequestCreated;

                default:
                    throw new ArgumentException($"Cannot convert {source} to output DTO.");
            }
        }
    }
}

