using System;

namespace Veldrid.Vulkan
{
    internal interface ISynchronizedResource : Vulkan.IResourceRefCountTarget
    {
        //ref SyncState SyncState { get; }
        Span<SyncState> AllSyncStates { get; }
        SyncSubresource SubresourceCounts { get; }
        ref SyncState SyncStateForSubresource(SyncSubresource subresource);
    }
}
