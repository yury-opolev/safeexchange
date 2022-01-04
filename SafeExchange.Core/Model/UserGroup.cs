

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using System;

    [Owned]
    public class UserGroup
    {
        public string AadGroupId { get; set; }
    }
}
