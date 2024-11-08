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
    internal abstract class VulkanFramebuffer : VulkanFramebufferBase
    {
        internal VulkanFramebuffer()
        {
        }

        internal VulkanFramebuffer(FramebufferAttachmentDescription? depthTargetDesc, ReadOnlySpan<FramebufferAttachmentDescription> colorTargetDescs)
            : base(depthTargetDesc, colorTargetDescs)
        {
        }

        // A "Framebuffer" framebuffer is always itself.
        public sealed override VulkanFramebuffer CurrentFramebuffer => this;


        public VkExtent2D RenderableExtent => new() { width = Width, height = Height };

        public abstract void StartRenderPass(VulkanCommandList cl, VkCommandBuffer cb, bool firstBinding,
            VkClearValue? depthClear, ReadOnlySpan<VkClearValue> colorTargetClear, ReadOnlySpan<bool> setColorClears);
        public abstract void EndRenderPass(VulkanCommandList cl, VkCommandBuffer cb);
    }
}
