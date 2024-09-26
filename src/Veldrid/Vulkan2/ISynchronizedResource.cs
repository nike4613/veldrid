namespace Veldrid.Vulkan2
{
    internal interface ISynchronizedResource : Vulkan.IResourceRefCountTarget
    {
        ref SyncState SyncState { get; }
    }
}
