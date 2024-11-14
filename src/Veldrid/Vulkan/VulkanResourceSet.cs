using System.Collections.Generic;

using TerraFX.Interop.Vulkan;

namespace Veldrid.Vulkan
{
    internal sealed class VulkanResourceSet : ResourceSet, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly DescriptorResourceCounts _descriptorCounts;
        private readonly DescriptorAllocationToken _descriptorAllocationToken;
        private string? _name;

        public List<ResourceRefCount> RefCounts { get; } = new();
        public List<ResourceWithSyncRequest<VulkanBuffer>> Buffers { get; } = new();
        public List<ResourceWithSyncRequest<VulkanTextureView>> Textures { get; } = new();

        public readonly record struct ResourceWithSyncRequest<T>(T Resource, SyncRequest Request);

        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        public VkDescriptorSet DescriptorSet => _descriptorAllocationToken.Set;

        internal VulkanResourceSet(VulkanGraphicsDevice gd, in ResourceSetDescription description, DescriptorAllocationToken token)
            : base(description)
        {
            _gd = gd;
            var layout = Util.AssertSubtype<ResourceLayout, VulkanResourceLayout>(description.Layout);
            _descriptorCounts = layout.ResourceCounts;
            _descriptorAllocationToken = token;

            RefCount = new(this);
        }

        public override void Dispose() => RefCount?.DecrementDispose();
        void IResourceRefCountTarget.RefZeroed()
        {
            _gd.DescriptorPoolManager.Free(_descriptorAllocationToken, _descriptorCounts);
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_DESCRIPTOR_SET_EXT, DescriptorSet.Value, value);
            }
        }
    }
}
