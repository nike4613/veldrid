using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Buffers;

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
    // VulkanDynamicFramebuffer uses Vulkan's new dynamic_rendering APIs, which doesn't require the construction of explicit render passes
    // or framebuffer objects. Using this enables framebuffers to be much cheaper to construct, when available.

    internal sealed class VulkanDynamicFramebuffer : VulkanFramebuffer, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VulkanTextureView? _depthTargetView;
        private readonly VulkanTextureView[] _colorTargetViews;

        // we don't actually have a backing object for the name
        public override string? Name { get; set; }

        public override HashSet<ISynchronizedResource> SynchroResources { get; }

        public override ResourceRefCount RefCount { get; }

        internal unsafe VulkanDynamicFramebuffer(VulkanGraphicsDevice gd, in FramebufferDescription description,
            VulkanTextureView? depthTargetView, VulkanTextureView[] colorTextureViews)
            : base(description.DepthTarget, description.ColorTargets)
        {
            _gd = gd;
            _depthTargetView = depthTargetView;
            _colorTargetViews = colorTextureViews;

            SynchroResources = new();
            if (depthTargetView is not null)
            {
                _ = SynchroResources.Add(depthTargetView.Target);
            }
            foreach (var view in colorTextureViews)
            {
                _ = SynchroResources.Add(view.Target);
            }

            Debug.Assert(gd._deviceCreateState.HasDynamicRendering);
            Debug.Assert(gd.vkCmdBeginRendering is not null);
            Debug.Assert(gd.vkCmdEndRendering is not null);

            RefCount = new(this);
        }

        void IResourceRefCountTarget.RefZeroed()
        {
            // we are the unique owners of these image views
            _depthTargetView?.Dispose();
            if (_colorTargetViews is not null)
            {
                foreach (var target in _colorTargetViews)
                {
                    target?.Dispose();
                }
            }
        }

        public override unsafe void StartRenderPass(VulkanCommandList cl, VkCommandBuffer cb, bool firstBinding,
            VkClearValue? depthClear, ReadOnlySpan<VkClearValue> colorTargetClear, ReadOnlySpan<bool> setColorClears)
        {
            // we'll also put the depth and stencil in here, for convenience
            var attachments = ArrayPool<VkRenderingAttachmentInfo>.Shared.Rent(_colorTargetViews.Length + 2);

            var hasDepthTarget = false;
            var hasStencil = false;
            if (_depthTargetView is { } depthTarget)
            {
                hasDepthTarget = true;
                hasStencil = FormatHelpers.IsStencilFormat(depthTarget.Format);

                var targetLayout =
                    (depthTarget.Target.Usage & TextureUsage.Sampled) != 0
                    ? VkImageLayout.VK_IMAGE_LAYOUT_GENERAL // TODO: it might be possible to do better
                    : VkImageLayout.VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;

                var loadOp = depthClear is not null
                    ? VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_CLEAR
                    : firstBinding
                    ? VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_DONT_CARE
                    : VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD;

                cl.SyncResource(depthTarget, new()
                {
                    Layout = targetLayout,
                    BarrierMasks = new()
                    {
                        AccessMask = (loadOp == VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD
                            ? VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT
                            : 0) | VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT,
                        StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT
                        | VkPipelineStageFlags.VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT
                        | VkPipelineStageFlags.VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT, // TODO: what stage mask should this be?
                    }
                });

                // [0] is depth
                attachments[0] = new()
                {
                    sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
                    imageView = depthTarget.ImageView,
                    imageLayout = targetLayout,
                    resolveMode = VkResolveModeFlags.VK_RESOLVE_MODE_NONE, // do not resolve
                    loadOp = loadOp is VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_DONT_CARE ? VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD : loadOp,
                    storeOp = VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_STORE,
                    clearValue = depthClear.GetValueOrDefault(),
                };

                if (hasStencil)
                {
                    // [1] is stencil
                    attachments[1] = new()
                    {
                        sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
                        imageView = depthTarget.ImageView,
                        imageLayout = targetLayout,
                        resolveMode = VkResolveModeFlags.VK_RESOLVE_MODE_NONE, // do not resolve
                        loadOp = loadOp,
                        storeOp = VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_STORE,
                        clearValue = depthClear.GetValueOrDefault(),
                    };
                }
            }

            // now the color targets
            for (var i = 0; i < _colorTargetViews.Length; i++)
            {
                var target = _colorTargetViews[i];

                var targetLayout =
                    (target.Target.Usage & TextureUsage.Sampled) != 0
                    ? VkImageLayout.VK_IMAGE_LAYOUT_GENERAL // TODO: it should definitely be possible to do better
                    : VkImageLayout.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;

                var loadOp = setColorClears[i] ? VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_CLEAR : VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD;

                cl.SyncResource(target, new()
                {
                    Layout = targetLayout,
                    BarrierMasks = new()
                    {
                        AccessMask = (loadOp == VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD
                            ? VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_READ_BIT
                            : 0) | VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                        StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, // TODO: what stage mask should this be?
                    }
                });

                attachments[2 + i] = new()
                {
                    sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
                    imageView = target.ImageView,
                    imageLayout = targetLayout,
                    resolveMode = VkResolveModeFlags.VK_RESOLVE_MODE_NONE,
                    loadOp = loadOp,
                    storeOp = VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_STORE,
                    clearValue = colorTargetClear[i],
                };
            }

            // emit sunchro before actually starting the render pass
            // render passes will be SHORT, and pretty much only single dispatches/dispatch sets, so we can avoid the problem of emitting synchro inside the render pass
            cl.EmitQueuedSynchro();

            fixed (VkRenderingAttachmentInfo* pAttachments = attachments)
            {
                var renderingInfo = new VkRenderingInfo()
                {
                    sType = VK_STRUCTURE_TYPE_RENDERING_INFO,
                    flags = 0,
                    renderArea = new()
                    {
                        offset = default,
                        extent = RenderableExtent
                    },
                    layerCount = 1,
                    viewMask = 0,
                    colorAttachmentCount = (uint)_colorTargetViews.Length,
                    pColorAttachments = pAttachments + 2, // [2] is the first color attachment
                    pDepthAttachment = hasDepthTarget ? pAttachments + 0 : null, // [0] is depth
                    pStencilAttachment = hasStencil ? pAttachments + 1 : null, // [1] is stencil
                };

                _gd.vkCmdBeginRendering(cb, &renderingInfo);
            }

            ArrayPool<VkRenderingAttachmentInfo>.Shared.Return(attachments);
        }

        public override unsafe void EndRenderPass(VulkanCommandList cl, VkCommandBuffer cb)
        {
            _gd.vkCmdEndRendering(cb);
        }
    }
}
