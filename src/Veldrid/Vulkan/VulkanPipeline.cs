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

namespace Veldrid.Vulkan
{
    internal sealed class VulkanPipeline : Pipeline, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkPipeline _devicePipeline;
        private readonly VkPipelineLayout _pipelineLayout;
        private string? _name;

        public VkPipeline DevicePipeline => _devicePipeline;
        public VkPipelineLayout PipelineLayout => _pipelineLayout;
        public uint ResourceSetCount { get; }
        public int DynamicOffsetsCount { get; }
        public uint VertexLayoutCount { get; }
        public override bool IsComputePipeline { get; }

        public ResourceRefCount RefCount { get; }
        public sealed override bool IsDisposed => RefCount.IsDisposed;

        public VulkanPipeline(VulkanGraphicsDevice device, in GraphicsPipelineDescription description, ref VkPipeline pipeline, ref VkPipelineLayout layout) : base(description)
        {
            _gd = device;
            _devicePipeline = pipeline;
            _pipelineLayout = layout;

            pipeline = default;
            layout = default;

            RefCount = new(this);

            IsComputePipeline = false;
            ResourceSetCount = (uint)description.ResourceLayouts.Length;
            DynamicOffsetsCount = 0;
            foreach (var resLayout in description.ResourceLayouts)
            {
                DynamicOffsetsCount += Util.AssertSubtype<ResourceLayout, VulkanResourceLayout>(resLayout).DynamicBufferCount;
            }
            VertexLayoutCount = (uint)description.ShaderSet.VertexLayouts.AsSpan().Length;
        }

        public VulkanPipeline(VulkanGraphicsDevice device, in ComputePipelineDescription description, ref VkPipeline pipeline, ref VkPipelineLayout layout) : base(description)
        {
            _gd = device;
            _devicePipeline = pipeline;
            _pipelineLayout = layout;

            pipeline = default;
            layout = default;

            RefCount = new(this);

            IsComputePipeline = true;
            ResourceSetCount = (uint)description.ResourceLayouts.Length;
            DynamicOffsetsCount = 0;
            foreach (var resLayout in description.ResourceLayouts)
            {
                DynamicOffsetsCount += Util.AssertSubtype<ResourceLayout, VulkanResourceLayout>(resLayout).DynamicBufferCount;
            }
        }

        public sealed override void Dispose() => RefCount?.DecrementDispose();

        unsafe void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyPipeline(_gd.Device, _devicePipeline, null);
            vkDestroyPipelineLayout(_gd.Device, _pipelineLayout, null);
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_PIPELINE_EXT, _devicePipeline.Value, value);
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_PIPELINE_LAYOUT_EXT, _pipelineLayout.Value, value + " (Pipeline Layout)");
            }
        }
    }
}
