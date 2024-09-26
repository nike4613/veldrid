using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TerraFX.Interop.Vulkan;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal sealed class VulkanFence : Fence, Vulkan.IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _device;
        private string? _name;
        public VkFence DeviceFence { get; }
        public Vulkan.ResourceRefCount RefCount { get; }

        public VulkanFence(VulkanGraphicsDevice device, VkFence fence)
        {
            _device = device;
            DeviceFence = fence;
            RefCount = new(this);
        }

        public override void Dispose() => RefCount.DecrementDispose();
        unsafe void Vulkan.IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyFence(_device.Device, DeviceFence, null);
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _device.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_FENCE_EXT, DeviceFence.Value, value);
            }
        }

        public override bool Signaled => vkGetFenceStatus(_device.Device, DeviceFence) is VkResult.VK_SUCCESS;
        public override bool IsDisposed => RefCount.IsDisposed;

        public override unsafe void Reset()
        {
            var fence = DeviceFence;
            VulkanUtil.CheckResult(vkResetFences(_device.Device, 1, &fence));
        }
    }
}
