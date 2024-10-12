/// <summary>
/// SubjectTypeMethods
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;

    public static class SubjectTypeMethods
	{
        public static SubjectType ToModel(this SubjectTypeInput source)
        {
            switch (source)
            {
                case SubjectTypeInput.User:
                    return SubjectType.User;

                case SubjectTypeInput.Group:
                    return SubjectType.Group;

                case SubjectTypeInput.Application:
                    return SubjectType.Application;

                default:
                    throw new ArgumentException($"Cannot convert {source} to model.");
            }
        }

        public static SubjectTypeOutput ToDto(this SubjectType source)
        {
            switch (source)
            {
                case SubjectType.User:
                    return SubjectTypeOutput.User;

                case SubjectType.Group:
                    return SubjectTypeOutput.Group;

                case SubjectType.Application:
                    return SubjectTypeOutput.Application;

                default:
                    throw new ArgumentException($"Cannot convert {source} to output DTO.");
            }
        }
    }
}

