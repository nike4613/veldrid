using TerraFX.Interop.Vulkan;

namespace Veldrid.Vulkan.Interop;

internal unsafe struct VkPhysicalDevicePresentModeFifoLatestReadyFeaturesEXT
{
    public const VkStructureType SType = (VkStructureType)1000361000;

    public VkStructureType sType;
    public void* pNext;
    public VkBool32 presentModeFifoLatestReady;
}
