using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan
{
    internal readonly struct RenderPassCacheKey : IEquatable<RenderPassCacheKey>
    {
        public readonly ReadOnlyMemory<RenderPassAttachmentInfo> Attachments;
        public readonly bool HasDepthStencilAttachment;

        public bool IsDefault => Attachments.IsEmpty;

        public RenderPassCacheKey(ReadOnlyMemory<RenderPassAttachmentInfo> attachments, bool hasDepthStencil)
        {
            Attachments = attachments;
            HasDepthStencilAttachment = hasDepthStencil;
        }

        public RenderPassCacheKey ToOwned()
            => new(Attachments.ToArray(), HasDepthStencilAttachment);

        public override int GetHashCode()
        {
            var hc = new HashCode();

            foreach (var att in Attachments.Span)
            {
                hc.Add(att);
            }

            hc.Add(HasDepthStencilAttachment);

            return hc.ToHashCode();
        }

        public override bool Equals([NotNullWhen(true)] object? obj)
            => obj is RenderPassCacheKey other && Equals(other);
        public bool Equals(RenderPassCacheKey other)
            => Attachments.Span.SequenceEqual(other.Attachments.Span)
            && HasDepthStencilAttachment == other.HasDepthStencilAttachment;
    }

    internal readonly record struct RenderPassAttachmentInfo(VkFormat Format, VkSampleCountFlags SampleCount, bool IsShaderRead, bool HasStencil);

    internal sealed class VulkanRenderPassHolder : IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly RenderPassCacheKey _cacheKey;
        internal readonly VkRenderPass LoadOpLoad;
        internal readonly VkRenderPass LoadOpDontCare;
        internal readonly VkRenderPass LoadOpClear;

        public ResourceRefCount RefCount { get; }

        private VulkanRenderPassHolder(VulkanGraphicsDevice gd, RenderPassCacheKey key,
            VkRenderPass loadOpLoad, VkRenderPass loadOpDontCare, VkRenderPass loadOpClear)
        {
            _gd = gd;
            _cacheKey = key;
            LoadOpLoad = loadOpLoad;
            LoadOpDontCare = loadOpDontCare;
            LoadOpClear = loadOpClear;

            RefCount = new(this);
        }

        unsafe void IResourceRefCountTarget.RefZeroed()
        {
            _ = _gd._renderPasses.TryRemove(new(_cacheKey, this));

            vkDestroyRenderPass(_gd.Device, LoadOpLoad, null);
            vkDestroyRenderPass(_gd.Device, LoadOpDontCare, null);
            vkDestroyRenderPass(_gd.Device, LoadOpClear, null);
        }

        public void DecRef() => RefCount.Decrement();

        // note: returns with RefCount = 1, when caller is done, they must DecRef()
        internal static VulkanRenderPassHolder GetRenderPassHolder(VulkanGraphicsDevice gd, RenderPassCacheKey cacheKey)
        {
            RenderPassCacheKey owned = default;
            VulkanRenderPassHolder? holder = null;
            bool createdHolder;
            do
            {
                createdHolder = false;
                if (gd._renderPasses.TryGetValue(cacheKey, out holder))
                {
                    // got a holder, fall out and make sure it's not closed
                }
                else
                {
                    if (owned.IsDefault)
                    {
                        owned = cacheKey.ToOwned();
                    }

                    createdHolder = true;
                    var newHolder = CreateRenderPasses(owned, gd);
                    holder = gd._renderPasses.GetOrAdd(owned, newHolder);
                    if (holder != newHolder)
                    {
                        // this means someone else beat us, make sure to clean ourselves up
                        newHolder.DecRef();
                        createdHolder = false;
                    }
                }
            }
            while (holder is null || holder.RefCount.IsClosed);

            // once we've selected a holder, increment the refcount (if we weren't the one to create it
            if (!createdHolder)
            {
                holder.RefCount.Increment();
            }
            return holder;
        }


        private static unsafe VulkanRenderPassHolder CreateRenderPasses(RenderPassCacheKey key, VulkanGraphicsDevice gd)
        {
            VkRenderPass rpLoad = default;
            VkRenderPass rpDontCare = default;
            VkRenderPass rpClear = default;

            try
            {
                var attachmentSpan = key.Attachments.Span;
                var totalAtts = attachmentSpan.Length;
                var colorAtts = key.HasDepthStencilAttachment ? totalAtts - 1 : totalAtts;
                var vkAtts = ArrayPool<VkAttachmentDescription>.Shared.Rent(totalAtts);
                var vkAttRefs = ArrayPool<VkAttachmentReference>.Shared.Rent(totalAtts);

                for (var i = 0; i < totalAtts; i++)
                {
                    var isDepthStencil = i >= colorAtts;
                    var desc = attachmentSpan[i];

                    var layout = desc.IsShaderRead
                        ? VkImageLayout.VK_IMAGE_LAYOUT_GENERAL
                        : isDepthStencil
                        ? VkImageLayout.VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL
                        : VkImageLayout.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;

                    vkAtts[i] = new()
                    {
                        format = desc.Format,
                        samples = desc.SampleCount,

                        // first, we'll create the LOAD_OP_LOAD render passes
                        loadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD,
                        storeOp = VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_STORE,
                        stencilLoadOp = desc.HasStencil ? VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD : VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_DONT_CARE,
                        stencilStoreOp = desc.HasStencil ? VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_STORE : VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_DONT_CARE,

                        // layouts shouldn't change due to render passes
                        initialLayout = layout,
                        finalLayout = layout,
                    };
                    vkAttRefs[i] = new()
                    {
                        attachment = (uint)i,
                        layout = layout,
                    };
                }

                ReadOnlySpan<VkSubpassDependency> subpassDeps = [
                    new VkSubpassDependency()
                    {
                        srcSubpass = VK_SUBPASS_EXTERNAL,
                        srcStageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                        dstStageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                        dstAccessMask = VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                    },
                    new VkSubpassDependency()
                    {
                        srcSubpass = 0,
                        dstSubpass = 0,
                        srcStageMask =
                            VkPipelineStageFlags.VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT |
                            VkPipelineStageFlags.VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT |
                            VkPipelineStageFlags.VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT |
                            VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                        dstStageMask =
                            VkPipelineStageFlags.VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT |
                            VkPipelineStageFlags.VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT |
                            VkPipelineStageFlags.VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT |
                            VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                        srcAccessMask =
                            VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_READ_BIT |
                            VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT |
                            VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT |
                            VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT |
                            VkAccessFlags.VK_ACCESS_SHADER_READ_BIT |
                            VkAccessFlags.VK_ACCESS_SHADER_WRITE_BIT,
                        dstAccessMask =
                            VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_READ_BIT |
                            VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT |
                            VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT |
                            VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT |
                            VkAccessFlags.VK_ACCESS_SHADER_READ_BIT |
                            VkAccessFlags.VK_ACCESS_SHADER_WRITE_BIT,

                        dependencyFlags = VkDependencyFlags.VK_DEPENDENCY_BY_REGION_BIT, // REQUIRED by Vulkan
                    },
                ];

                fixed (VkAttachmentDescription* pVkAtts = vkAtts)
                fixed (VkAttachmentReference* pVkAttRefs = vkAttRefs)
                fixed (VkSubpassDependency* pSubpassDeps = subpassDeps)
                {
                    var subpassDesc = new VkSubpassDescription()
                    {
                        pipelineBindPoint = VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_GRAPHICS,
                        colorAttachmentCount = (uint)colorAtts,
                        pColorAttachments = pVkAttRefs,
                        pDepthStencilAttachment = key.HasDepthStencilAttachment ? pVkAttRefs + colorAtts : null,
                    };


                    var renderPassCreateInfo = new VkRenderPassCreateInfo()
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO,
                        attachmentCount = (uint)totalAtts,
                        pAttachments = pVkAtts,
                        subpassCount = 1,
                        pSubpasses = &subpassDesc,
                        dependencyCount = (uint)subpassDeps.Length,
                        pDependencies = pSubpassDeps,
                    };

                    // our create info is all set up, now create our first variant, the Load variant
                    VulkanUtil.CheckResult(vkCreateRenderPass(gd.Device, &renderPassCreateInfo, null, &rpLoad));

                    // next, create the DONT_CARE variants
                    for (var i = 0; i < totalAtts; i++)
                    {
                        ref var desc = ref vkAtts[i];
                        desc.loadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_DONT_CARE;
                        desc.stencilLoadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_DONT_CARE;
                    }
                    VulkanUtil.CheckResult(vkCreateRenderPass(gd.Device, &renderPassCreateInfo, null, &rpDontCare));

                    // finally, the CLEAR variants
                    for (var i = 0; i < totalAtts; i++)
                    {
                        ref var desc = ref vkAtts[i];
                        desc.loadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_CLEAR;
                        if (attachmentSpan[i].HasStencil)
                        {
                            desc.stencilLoadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_CLEAR;
                        }
                    }
                    VulkanUtil.CheckResult(vkCreateRenderPass(gd.Device, &renderPassCreateInfo, null, &rpClear));

                }

                var result = new VulkanRenderPassHolder(gd, key, rpLoad, rpDontCare, rpClear);
                rpLoad = default;
                rpDontCare = default;
                rpClear = default;

                ArrayPool<VkAttachmentDescription>.Shared.Return(vkAtts);
                ArrayPool<VkAttachmentReference>.Shared.Return(vkAttRefs);

                return result;
            }
            finally
            {
                if (rpLoad != VkRenderPass.NULL)
                {
                    vkDestroyRenderPass(gd.Device, rpLoad, null);
                }
                if (rpDontCare != VkRenderPass.NULL)
                {
                    vkDestroyRenderPass(gd.Device, rpDontCare, null);
                }
                if (rpClear != VkRenderPass.NULL)
                {
                    vkDestroyRenderPass(gd.Device, rpClear, null);
                }
            }
        }


    }
}
