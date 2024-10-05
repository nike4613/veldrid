using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using VkFormats = Veldrid.Vulkan.VkFormats;
using VkMemoryBlock = Veldrid.Vulkan.VkMemoryBlock;
using VkDeviceMemoryManager = Veldrid.Vulkan.VkDeviceMemoryManager;
using ResourceRefCount = Veldrid.Vulkan.ResourceRefCount;
using IResourceRefCountTarget = Veldrid.Vulkan.IResourceRefCountTarget;
using static TerraFX.Interop.Vulkan.VkStructureType;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal sealed class VulkanShader : Shader, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkShaderModule _shaderModule;
        private string? _name;

        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        public VkShaderModule ShaderModule => _shaderModule;

        public VulkanShader(VulkanGraphicsDevice gd, in ShaderDescription description, VkShaderModule module)
            : base(description.Stage, description.EntryPoint)
        {
            _gd = gd;
            _shaderModule = module;

            RefCount = new(this);
        }

        public override void Dispose() => RefCount?.DecrementDispose();
        unsafe void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyShaderModule(_gd.Device, _shaderModule, null);
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_SHADER_MODULE_EXT, _shaderModule.Value, value);
            }
        }
    }
}
