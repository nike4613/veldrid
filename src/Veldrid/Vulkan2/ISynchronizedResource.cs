using System;

namespace Veldrid.Vulkan2
{
    internal interface ISynchronizedResource : Vulkan.IResourceRefCountTarget
    {
        //ref SyncState SyncState { get; }
        Span<SyncState> AllSyncStates { get; }
        ref SyncState SyncStateForSubresource(SyncSubresource subresource);
    }
}
