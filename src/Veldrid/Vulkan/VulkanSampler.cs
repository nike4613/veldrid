using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan
{
    internal unsafe sealed class VulkanSampler : Sampler, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkSampler _sampler;
        private string? _name;

        public VkSampler DeviceSampler => _sampler;

        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        internal VulkanSampler(VulkanGraphicsDevice gd, VkSampler sampler)
        {
            _gd = gd;
            _sampler = sampler;
            RefCount = new(this);
        }

        public override void Dispose() => RefCount?.DecrementDispose();

        void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroySampler(_gd.Device, _sampler, null);
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_SAMPLER_EXT, _sampler.Value, value);
            }
        }
    }
}
