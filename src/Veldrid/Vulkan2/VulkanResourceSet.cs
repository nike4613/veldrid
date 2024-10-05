using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TerraFX.Interop.Vulkan;
using IResourceRefCountTarget = Veldrid.Vulkan.IResourceRefCountTarget;
using ResourceRefCount = Veldrid.Vulkan.ResourceRefCount;
using VkMemoryBlock = Veldrid.Vulkan.VkMemoryBlock;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using VkFormats = Veldrid.Vulkan.VkFormats;
using DescriptorResourceCounts = Veldrid.Vulkan.DescriptorResourceCounts;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal sealed class VulkanResourceSet : ResourceSet, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly DescriptorResourceCounts _descriptorCounts;
        private readonly DescriptorAllocationToken _descriptorAllocationToken;
        private string? _name;

        public List<ResourceRefCount> RefCounts { get; } = new();
        public List<VulkanTexture> SampledTextures { get; } = new();
        public List<VulkanTexture> StorageTextures { get; } = new();

        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        public VkDescriptorSet DescriptorSet => _descriptorAllocationToken.Set;

        internal VulkanResourceSet(VulkanGraphicsDevice gd, in ResourceSetDescription description, DescriptorAllocationToken token)
            : base(description)
        {
            _gd = gd;
            _descriptorCounts = Util.AssertSubtype<ResourceLayout, VulkanResourceLayout>(description.Layout).ResourceCounts;
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
