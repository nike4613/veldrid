using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan
{
    internal sealed class VulkanRenderPassFramebuffer : VulkanFramebuffer, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VulkanRenderPassHolder _rpHolder;
        private readonly VkFramebuffer _framebuffer;
        private readonly VulkanTextureView[]? _colorAttachments;
        private readonly VulkanTextureView? _depthAttachment;
        private string? _name;

        public override ResourceRefCount RefCount { get; }

        internal VulkanRenderPassFramebuffer(VulkanGraphicsDevice gd, FramebufferDescription description,
            VkFramebuffer framebuffer, VulkanRenderPassHolder holder,
            VulkanTextureView[]? attachments, VulkanTextureView? depthAttachment)
            : base(description.DepthTarget, description.ColorTargets)
        {
            _gd = gd;
            _framebuffer = framebuffer;
            _rpHolder = holder;
            _colorAttachments = attachments;
            _depthAttachment = depthAttachment;

            RefCount = new(this);
        }

        public override void Dispose() => RefCount?.DecrementDispose();

        unsafe void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyFramebuffer(_gd.Device, _framebuffer, null);
            _rpHolder.DecRef();

            foreach (var att in _colorAttachments.AsSpan())
            {
                att.Dispose();
            }
            _depthAttachment?.Dispose();
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_FRAMEBUFFER_EXT, _framebuffer.Value, value);
            }
        }

        public override unsafe void StartRenderPass(VulkanCommandList cl, VkCommandBuffer cb, bool firstBinding,
            VkClearValue? depthClear, ReadOnlySpan<VkClearValue> colorTargetClear, ReadOnlySpan<bool> setColorClears)
        {
            var haveAnyAttachments = false;
            var haveDepthAttachment = false;

            if (_depthAttachment is { } depthTarget)
            {
                haveAnyAttachments = true;
                haveDepthAttachment = true;

                var targetLayout =
                    (depthTarget.Target.Usage & TextureUsage.Sampled) != 0
                    ? VkImageLayout.VK_IMAGE_LAYOUT_GENERAL // TODO: it might be possible to do better
                    : VkImageLayout.VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;

                cl.SyncResource(depthTarget, new()
                {
                    Layout = targetLayout,
                    BarrierMasks = new()
                    {
                        AccessMask = (!depthClear.HasValue
                            ? VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT
                            : 0) | VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT,
                        StageMask =
                        VkPipelineStageFlags.VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT
                        | VkPipelineStageFlags.VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT
                        | VkPipelineStageFlags.VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT,
                    }
                });
            }

            // now the color targets
            var colorAttSpan = _colorAttachments.AsSpan();
            for (var i = 0; i < colorAttSpan.Length; i++)
            {
                haveAnyAttachments = true;
                var target = colorAttSpan[i];

                var targetLayout =
                    (target.Target.Usage & TextureUsage.Sampled) != 0
                    ? VkImageLayout.VK_IMAGE_LAYOUT_GENERAL // TODO: it should definitely be possible to do better
                    : VkImageLayout.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;

                cl.SyncResource(target, new()
                {
                    Layout = targetLayout,
                    BarrierMasks = new()
                    {
                        StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                        AccessMask = (!setColorClears[i]
                            ? VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_READ_BIT
                            : 0) | VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                    }
                });
            }

            // emit sunchro before actually starting the render pass
            // render passes will be SHORT, and pretty much only single dispatches/dispatch sets, so we can avoid the problem of emitting synchro inside the render pass
            cl.EmitQueuedSynchro();

            var haveAllClearValues = depthClear.HasValue;
            var haveAnyClearValues = depthClear.HasValue;
            foreach (var hasClear in setColorClears)
            {
                if (hasClear)
                {
                    haveAnyClearValues = true;
                }
                else
                {
                    haveAllClearValues = false;
                }
            }

            var beginInfo = new VkRenderPassBeginInfo()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO,
                framebuffer = _framebuffer,
                renderArea = new()
                {
                    offset = default,
                    extent = RenderableExtent,
                }
            };

            if (!haveAnyAttachments || !haveAllClearValues)
            {
                beginInfo.renderPass = firstBinding ? _rpHolder.LoadOpDontCare : _rpHolder.LoadOpLoad;
                vkCmdBeginRenderPass(cb, &beginInfo, VkSubpassContents.VK_SUBPASS_CONTENTS_INLINE);

                if (haveAnyClearValues)
                {
                    if (depthClear is { } depthClearValue)
                    {
                        var att = new VkClearAttachment()
                        {
                            aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT
                                | (FormatHelpers.IsStencilFormat(_depthAttachment!.Format) ? VkImageAspectFlags.VK_IMAGE_ASPECT_STENCIL_BIT : 0),
                            clearValue = depthClearValue,
                            colorAttachment = (uint)colorAttSpan.Length,
                        };

                        var rect = new VkClearRect()
                        {
                            baseArrayLayer = _depthAttachment!.BaseArrayLayer,
                            layerCount = _depthAttachment!.RealArrayLayers,
                            rect = new()
                            {
                                offset = default,
                                extent = RenderableExtent,
                            }
                        };
                        vkCmdClearAttachments(cb, 1, &att, 1, &rect);
                    }

                    for (var i = 0u; i < colorAttSpan.Length; i++)
                    {
                        if (setColorClears[(int)i])
                        {
                            var att = new VkClearAttachment()
                            {
                                aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                                clearValue = colorTargetClear[(int)i],
                                colorAttachment = i,
                            };

                            var rect = new VkClearRect()
                            {
                                baseArrayLayer = _depthAttachment!.BaseArrayLayer,
                                layerCount = _depthAttachment!.RealArrayLayers,
                                rect = new()
                                {
                                    offset = default,
                                    extent = RenderableExtent,
                                }
                            };
                            vkCmdClearAttachments(cb, 1, &att, 1, &rect);
                        }
                    }
                }

            }
            else
            {
                cl.EmitQueuedSynchro();

                // we have clear values for every attachment, use the clear LoadOp RenderPass
                beginInfo.renderPass = _rpHolder.LoadOpClear;
                if (haveDepthAttachment)
                {
                    // we have a depth attachment, we need more space than we have in colorTargetClear
                    var clearValues = ArrayPool<VkClearValue>.Shared.Rent(colorAttSpan.Length + 1);
                    beginInfo.clearValueCount = (uint)colorAttSpan.Length + 1;
                    colorTargetClear.CopyTo(clearValues);
                    clearValues[colorAttSpan.Length] = depthClear!.Value;

                    fixed (VkClearValue* pClearValues = clearValues)
                    {
                        beginInfo.pClearValues = pClearValues;
                        vkCmdBeginRenderPass(cb, &beginInfo, VkSubpassContents.VK_SUBPASS_CONTENTS_INLINE);
                    }

                    ArrayPool<VkClearValue>.Shared.Return(clearValues);
                }
                else
                {
                    // we don't have a depth attachment, we can just use the passed-in span
                    beginInfo.clearValueCount = (uint)colorAttSpan.Length;
                    fixed (VkClearValue* pClearValues = colorTargetClear)
                    {
                        beginInfo.pClearValues = pClearValues;
                        vkCmdBeginRenderPass(cb, &beginInfo, VkSubpassContents.VK_SUBPASS_CONTENTS_INLINE);
                    }
                }
            }
        }

        public override void EndRenderPass(VulkanCommandList cl, VkCommandBuffer cb)
        {
            vkCmdEndRenderPass(cb);
        }
    }
}
