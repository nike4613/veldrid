using System;

using TerraFX.Interop.Vulkan;

namespace Veldrid.Vulkan
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
