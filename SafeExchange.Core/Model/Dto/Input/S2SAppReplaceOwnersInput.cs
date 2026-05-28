namespace SafeExchange.Core.Model.Dto.Input
{
    using System.Collections.Generic;

    /// <summary>
    /// Body of PUT /v2/s2sapps/{name}/owners. Replaces the entire owner set
    /// for an application in a single Cosmos transaction so a swap (remove
    /// one + add another) can't break the >=2-owners-with-a-user invariant
    /// mid-update.
    /// </summary>
    public class S2SAppReplaceOwnersInput
    {
        public List<S2SAppOwnerInput> Owners { get; set; } = new();
    }
}
