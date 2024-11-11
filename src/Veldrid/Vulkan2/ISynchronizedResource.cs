using System;

namespace Veldrid.Vulkan2
{
    internal interface ISynchronizedResource : Vulkan.IResourceRefCountTarget
    {
        //ref SyncState SyncState { get; }
        Span<SyncState> AllSyncStates { get; }
        SyncSubresource SubresourceCounts { get; }
        ref SyncState SyncStateForSubresource(SyncSubresource subresource);
    }
}
