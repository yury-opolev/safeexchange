/// <summary>
/// AccessUpdateInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System.Collections.Generic;

    public class AccessUpdateInput
    {
        public List<SubjectPermissionsInput>? Add { get; set; }

        public List<SubjectPermissionsInput>? Remove { get; set; }
    }
}
