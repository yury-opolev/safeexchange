﻿/// <summary>
/// SubjectPermissionsOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class SubjectPermissionsOutput
    {
        public string ObjectName { get; set; }

        public SubjectTypeOutput SubjectType { get; set; }

        public string SubjectName { get; set; }

        public string SubjectId { get; set; }

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }

        public bool CanGrantAccess { get; set; }

        public bool CanRevokeAccess { get; set; }
    }
}
