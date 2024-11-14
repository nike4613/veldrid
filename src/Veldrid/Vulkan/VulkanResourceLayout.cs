using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan
{
    internal sealed class VulkanResourceLayout : ResourceLayout, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkDescriptorSetLayout _dsl;
        private readonly VkDescriptorType[] _descriptorTypes;
        private readonly VkShaderStageFlags[] _shaderStages;
        private readonly VkAccessFlags[] _accessFlags;
        private string? _name;

        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        public VkDescriptorSetLayout DescriptorSetLayout => _dsl;
        public VkDescriptorType[] DescriptorTypes => _descriptorTypes;
        public VkShaderStageFlags[] ShaderStages => _shaderStages;
        public VkAccessFlags[] AccessFlags => _accessFlags;
        public DescriptorResourceCounts ResourceCounts { get; }
        public new int DynamicBufferCount { get; }

        internal VulkanResourceLayout(VulkanGraphicsDevice gd, in ResourceLayoutDescription description,
            VkDescriptorSetLayout dsl,
            VkDescriptorType[] descriptorTypes, VkShaderStageFlags[] shaderStages, VkAccessFlags[] access,
            in DescriptorResourceCounts resourceCounts, int dynamicBufferCount)
            : base(description)
        {
            _gd = gd;
            _dsl = dsl;
            _descriptorTypes = descriptorTypes;
            _shaderStages = shaderStages;
            _accessFlags = access;
            ResourceCounts = resourceCounts;
            DynamicBufferCount = dynamicBufferCount;

            RefCount = new(this);
        }

        public override void Dispose() => RefCount?.DecrementDispose();
        unsafe void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyDescriptorSetLayout(_gd.Device, _dsl, null);
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_DESCRIPTOR_SET_LAYOUT_EXT, _dsl.Value, value);
            }
        }
    }
}
