using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

// NOTE:
// Modern OpenSim removed revenue-sharing payee lists.
// Default policy is to pay the object owner.

internal static class OpenSimCompat
{
    public static IList<UUID> GetPayees(SceneObjectPart part)
    {
        if (part == null)
            return Array.Empty<UUID>();

#if OPENSIM_HAS_PAYEELIST
        // Older OpenSim supported revenue-sharing via payeeList
        if (part.payeeList != null && part.payeeList.Count > 0)
            return part.payeeList;
#endif
        // Modern OpenSim behavior: pay the object owner
        return new List<UUID>(1) { part.OwnerID };
    }
}

