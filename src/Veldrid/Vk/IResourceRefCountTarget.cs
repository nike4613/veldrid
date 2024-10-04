namespace Veldrid.Vulkan
{
    internal interface IResourceRefCountTarget
    {
        ResourceRefCount RefCount { get; }
        void RefZeroed();
    }
}
