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
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal sealed class VulkanResourceFactory : ResourceFactory
    {
        private readonly VulkanGraphicsDevice _gd;

        public VulkanResourceFactory(VulkanGraphicsDevice gd) : base(gd.Features)
        {
            _gd = gd;
        }

        public override GraphicsBackend BackendType => GraphicsBackend.Vulkan;

        public unsafe override Fence CreateFence(bool signaled)
        {
            VkFence fence = default;
            try
            {
                var createInfo = new VkFenceCreateInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_FENCE_CREATE_INFO,
                    flags = signaled ? VkFenceCreateFlags.VK_FENCE_CREATE_SIGNALED_BIT : 0,
                };

                VulkanUtil.CheckResult(vkCreateFence(_gd.Device, &createInfo, null, &fence));

                var result = new VulkanFence(_gd, fence);
                fence = default; // transfer ownership
                return result;
            }
            finally
            {
                if (fence != VkFence.NULL)
                {
                    vkDestroyFence(_gd.Device, fence, null);
                }
            }
        }

        public unsafe override CommandList CreateCommandList(in CommandListDescription description)
        {
            VkCommandPool pool = default;
            try
            {
                pool = _gd.CreateCommandPool(description.Transient);

                var result = new VulkanCommandList(_gd, pool, description);
                pool = default; // transfer ownership
                return result;
            }
            finally
            {
                if (pool != VkCommandPool.NULL)
                {
                    vkDestroyCommandPool(_gd.Device, pool, null);
                }
            }
        }

        public unsafe override Sampler CreateSampler(in SamplerDescription description)
        {
            ValidateSampler(description);

            VkFormats.GetFilterParams(description.Filter, out var minFilter, out var magFilter, out var mipmapMode);

            var createInfo = new VkSamplerCreateInfo()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
                addressModeU = VkFormats.VdToVkSamplerAddressMode(description.AddressModeU),
                addressModeV = VkFormats.VdToVkSamplerAddressMode(description.AddressModeV),
                addressModeW = VkFormats.VdToVkSamplerAddressMode(description.AddressModeW),
                minFilter = minFilter,
                magFilter = magFilter,
                mipmapMode = mipmapMode,
                compareEnable = (VkBool32)(description.ComparisonKind is not null),
                compareOp = description.ComparisonKind is { } compareKind
                    ? VkFormats.VdToVkCompareOp(compareKind)
                    : VkCompareOp.VK_COMPARE_OP_NEVER,
                anisotropyEnable = (VkBool32)(description.Filter is SamplerFilter.Anisotropic),
                maxAnisotropy = description.MaximumAnisotropy,
                minLod = description.MinimumLod,
                maxLod = description.MaximumLod,
                mipLodBias = description.LodBias,
                borderColor = VkFormats.VdToVkSamplerBorderColor(description.BorderColor),
            };

            VkSampler sampler;
            VulkanUtil.CheckResult(vkCreateSampler(_gd.Device, &createInfo, null, &sampler));

            return new VulkanSampler(_gd, sampler);
        }

        private unsafe VkMemoryRequirements GetBufferMemoryRequirements(VkBuffer buffer, out VkBool32 useDedicatedAllocation)
        {
            VkMemoryRequirements memoryRequirements;
            if (_gd.vkGetBufferMemoryRequirements2 is not null)
            {
                var memReqInfo2 = new VkBufferMemoryRequirementsInfo2()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_BUFFER_MEMORY_REQUIREMENTS_INFO_2,
                    buffer = buffer,
                };
                var dediReqs = new VkMemoryDedicatedRequirements()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_DEDICATED_REQUIREMENTS,
                };
                var memReqs2 = new VkMemoryRequirements2()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_REQUIREMENTS_2,
                    pNext = &dediReqs,
                };
                _gd.vkGetBufferMemoryRequirements2(_gd.Device, &memReqInfo2, &memReqs2);
                memoryRequirements = memReqs2.memoryRequirements;
                useDedicatedAllocation = dediReqs.prefersDedicatedAllocation | dediReqs.requiresDedicatedAllocation;
            }
            else
            {
                vkGetBufferMemoryRequirements(_gd.Device, buffer, &memoryRequirements);
                useDedicatedAllocation = false;
            }

            return memoryRequirements;
        }

        private unsafe VkMemoryRequirements GetImageMemoryRequirements(VkImage image, out VkBool32 useDedicatedAllocation)
        {
            VkMemoryRequirements memoryRequirements;
            if (_gd.vkGetImageMemoryRequirements2 is not null)
            {
                var memReqInfo2 = new VkImageMemoryRequirementsInfo2()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_REQUIREMENTS_INFO_2,
                    image = image,
                };
                var dediReqs = new VkMemoryDedicatedRequirements()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_DEDICATED_REQUIREMENTS,
                };
                var memReqs2 = new VkMemoryRequirements2()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_REQUIREMENTS_2,
                    pNext = &dediReqs,
                };
                _gd.vkGetImageMemoryRequirements2(_gd.Device, &memReqInfo2, &memReqs2);
                memoryRequirements = memReqs2.memoryRequirements;
                useDedicatedAllocation = dediReqs.prefersDedicatedAllocation | dediReqs.requiresDedicatedAllocation;
            }
            else
            {
                vkGetImageMemoryRequirements(_gd.Device, image, &memoryRequirements);
                useDedicatedAllocation = false;
            }

            return memoryRequirements;
        }

        public unsafe override DeviceBuffer CreateBuffer(in BufferDescription description)
        {
            ValidateBuffer(description);

            var vkUsage = VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_DST_BIT;
            if ((description.Usage & BufferUsage.VertexBuffer) != 0)
            {
                vkUsage |= VkBufferUsageFlags.VK_BUFFER_USAGE_VERTEX_BUFFER_BIT;
            }
            if ((description.Usage & BufferUsage.IndexBuffer) != 0)
            {
                vkUsage |= VkBufferUsageFlags.VK_BUFFER_USAGE_INDEX_BUFFER_BIT;
            }
            if ((description.Usage & BufferUsage.UniformBuffer) != 0)
            {
                vkUsage |= VkBufferUsageFlags.VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT;
            }
            if ((description.Usage & (BufferUsage.StructuredBufferReadOnly | BufferUsage.StructuredBufferReadWrite)) != 0)
            {
                vkUsage |= VkBufferUsageFlags.VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
            }
            if ((description.Usage & BufferUsage.IndirectBuffer) != 0)
            {
                vkUsage |= VkBufferUsageFlags.VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT;
            }

            VkBuffer buffer = default;
            VkMemoryBlock memoryBlock = default;
            VulkanBuffer result;

            try
            {
                var bufferCreateInfo = new VkBufferCreateInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
                    size = description.SizeInBytes,
                    usage = vkUsage,
                };
                VulkanUtil.CheckResult(vkCreateBuffer(_gd.Device, &bufferCreateInfo, null, &buffer));

                var memoryRequirements = GetBufferMemoryRequirements(buffer, out var useDedicatedAllocation);

                var isStaging = (description.Usage & BufferUsage.StagingReadWrite) != 0;
                var isDynamic = (description.Usage & BufferUsage.DynamicReadWrite) != 0;
                var hostVisible = isStaging || isDynamic;

                var memPropFlags = hostVisible
                    ? VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT
                    : VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;

                if ((description.Usage & BufferUsage.StagingRead) != 0)
                {
                    // Use "host cached" memory for staging when available, for better performance of GPU -> CPU transfers
                    var hostCachedAvailable = VulkanUtil.TryFindMemoryType(
                        _gd._deviceCreateState.PhysicalDeviceMemoryProperties,
                        memoryRequirements.memoryTypeBits,
                        memPropFlags | VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_CACHED_BIT,
                        out _);

                    if (hostCachedAvailable)
                    {
                        memPropFlags |= VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_CACHED_BIT;
                    }
                }

                memoryBlock = _gd.MemoryManager.Allocate(
                    _gd._deviceCreateState.PhysicalDeviceMemoryProperties,
                    memoryRequirements.memoryTypeBits,
                    memPropFlags,
                    hostVisible,
                    memoryRequirements.size,
                    memoryRequirements.alignment,
                    useDedicatedAllocation,
                    dedicatedImage: default,
                    buffer);

                VulkanUtil.CheckResult(vkBindBufferMemory(_gd.Device, buffer, memoryBlock.DeviceMemory, memoryBlock.Offset));

                result = new VulkanBuffer(_gd, description, buffer, memoryBlock);
                buffer = default; // ownership is transferred
                memoryBlock = default;
            }
            finally
            {
                if (buffer != VkBuffer.NULL)
                {
                    vkDestroyBuffer(_gd.Device, buffer, null);
                }

                if (memoryBlock.DeviceMemory != VkDeviceMemory.NULL)
                {
                    _gd.MemoryManager.Free(memoryBlock);
                }
            }

            // once we've created the buffer, populate it if initial data was specified
            if (description.InitialData != 0)
            {
                _gd.UpdateBuffer(result, 0, description.InitialData, description.SizeInBytes);
            }

            return result;
        }

        public unsafe override Texture CreateTexture(in TextureDescription description)
        {
            var isStaging = (description.Usage & TextureUsage.Staging) != 0;

            VkBuffer buffer = default;
            VkImage image = default;
            VkMemoryBlock memoryBlock = default;

            try
            {
                if (!isStaging)
                {
                    // regular texture, using an actual VkImage
                    var imageCreateInfo = new VkImageCreateInfo()
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
                        mipLevels = description.MipLevels,
                        arrayLayers = description.ArrayLayers,
                        imageType = VkFormats.VdToVkTextureType(description.Type),
                        extent = new()
                        {
                            width = description.Width,
                            height = description.Height,
                            depth = description.Depth,
                        },
                        initialLayout = VkImageLayout.VK_IMAGE_LAYOUT_PREINITIALIZED,
                        usage = VkFormats.VdToVkTextureUsage(description.Usage),
                        tiling = VkImageTiling.VK_IMAGE_TILING_OPTIMAL,
                        format = VkFormats.VdToVkPixelFormat(description.Format, description.Usage),
                        flags = VkImageCreateFlags.VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT,
                        samples = VkFormats.VdToVkSampleCount(description.SampleCount),
                    };

                    var actualArrayLayers = description.ArrayLayers;
                    if ((description.Usage & TextureUsage.Cubemap) != 0)
                    {
                        imageCreateInfo.flags |= VkImageCreateFlags.VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT;
                        actualArrayLayers = 6 * description.ArrayLayers;
                    }

                    var subresourceCount = description.MipLevels * description.Depth * actualArrayLayers;

                    VulkanUtil.CheckResult(vkCreateImage(_gd.Device, &imageCreateInfo, null, &image));

                    var memoryRequirements = GetImageMemoryRequirements(image, out var useDedicatedAllocation);

                    memoryBlock = _gd.MemoryManager.Allocate(
                        _gd._deviceCreateState.PhysicalDeviceMemoryProperties,
                        memoryRequirements.memoryTypeBits,
                        VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT,
                        persistentMapped: false,
                        memoryRequirements.size,
                        memoryRequirements.alignment,
                        useDedicatedAllocation,
                        image,
                        dedicatedBuffer: default);

                    VulkanUtil.CheckResult(vkBindImageMemory(_gd.Device, image, memoryBlock.DeviceMemory, memoryBlock.Offset));
                }
                else
                {
                    // staging buffer, isn't backed by an actual VkImage
                    var depthPitch = FormatHelpers.GetDepthPitch(
                        FormatHelpers.GetRowPitch(description.Width, description.Format),
                        description.Height,
                        description.Format);
                    var stagingSize = depthPitch * description.Depth;

                    for (var level = 1u; level < description.MipLevels; level++)
                    {
                        var mipWidth = Util.GetDimension(description.Width, level);
                        var mipHeight = Util.GetDimension(description.Height, level);
                        var mipDepth = Util.GetDimension(description.Depth, level);

                        depthPitch = FormatHelpers.GetDepthPitch(
                            FormatHelpers.GetRowPitch(mipWidth, description.Format),
                            mipHeight,
                            description.Format);

                        stagingSize += depthPitch * mipDepth;
                    }

                    var bufferCreateInfo = new VkBufferCreateInfo()
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
                        usage = VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_DST_BIT,
                        size = stagingSize,
                    };
                    VulkanUtil.CheckResult(vkCreateBuffer(_gd.Device, &bufferCreateInfo, null, &buffer));

                    var memoryRequirements = GetBufferMemoryRequirements(buffer, out var useDedicatedAllocation);

                    // Use "host cached" memory when available, for better performance of GPU -> CPU transfers
                    var propertyFlags =
                        VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                        VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT |
                        VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_CACHED_BIT;

                    if (!VulkanUtil.TryFindMemoryType(
                        _gd._deviceCreateState.PhysicalDeviceMemoryProperties,
                        memoryRequirements.memoryTypeBits, propertyFlags, out _))
                    {
                        propertyFlags ^= VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_CACHED_BIT;
                    }

                    memoryBlock = _gd.MemoryManager.Allocate(
                        _gd._deviceCreateState.PhysicalDeviceMemoryProperties,
                        memoryRequirements.memoryTypeBits,
                        propertyFlags,
                        persistentMapped: true,
                        memoryRequirements.size,
                        memoryRequirements.alignment,
                        useDedicatedAllocation,
                        dedicatedImage: default,
                        buffer);

                    VulkanUtil.CheckResult(vkBindBufferMemory(_gd.Device, buffer, memoryBlock.DeviceMemory, memoryBlock.Offset));
                }

                var result = new VulkanTexture(
                    _gd, in description,
                    image, memoryBlock, buffer,
                    isSwapchainTexture: false,
                    leaveOpen: false);
                image = default; // ownership transfer into the new object
                buffer = default;
                memoryBlock = default;

                // now make sure the current image layout is set
                // the texture is either:
                // a) a buffer, so the image layout doesn't matter, or
                // b) an image, created with an initial layout of PREINITIALIZED
                result.SyncState.CurrentImageLayout = VkImageLayout.VK_IMAGE_LAYOUT_PREINITIALIZED;

                return result;
            }
            finally
            {
                if (buffer != VkBuffer.NULL)
                {
                    vkDestroyBuffer(_gd.Device, buffer, null);
                }

                if (image != VkImage.NULL)
                {
                    vkDestroyImage(_gd.Device, image, null);
                }

                if (memoryBlock.DeviceMemory != VkDeviceMemory.NULL)
                {
                    _gd.MemoryManager.Free(memoryBlock);
                }
            }
        }

        public override Texture CreateTexture(ulong nativeTexture, in TextureDescription description)
        {
            var image = new VkImage(nativeTexture);

            return new VulkanTexture(_gd, description,
                image, default, default,
                isSwapchainTexture: false,
                leaveOpen: true);
        }

        public unsafe override TextureView CreateTextureView(in TextureViewDescription description)
        {
            var tex = Util.AssertSubtype<Texture, VulkanTexture>(description.Target);

            VkImageView imageView = default;

            try
            {
                var aspectFlags =
                    (description.Target.Usage & TextureUsage.DepthStencil) != 0
                    ? VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT
                    : VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT;

                var imageViewCreateInfo = new VkImageViewCreateInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
                    image = tex.DeviceImage,
                    format = VkFormats.VdToVkPixelFormat(description.Format ?? tex.Format, tex.Usage),
                    subresourceRange = new()
                    {
                        aspectMask = aspectFlags,
                        baseMipLevel = description.BaseMipLevel,
                        levelCount = description.MipLevels,
                        baseArrayLayer = description.BaseArrayLayer,
                        layerCount = description.ArrayLayers,
                    }
                };

                if ((tex.Usage & TextureUsage.Cubemap) != 0)
                {
                    imageViewCreateInfo.viewType = description.ArrayLayers == 1
                        ? VkImageViewType.VK_IMAGE_VIEW_TYPE_CUBE
                        : VkImageViewType.VK_IMAGE_VIEW_TYPE_CUBE_ARRAY;
                    imageViewCreateInfo.subresourceRange.layerCount *= 6;
                }
                else
                {
                    switch (tex.Type)
                    {
                        case TextureType.Texture1D:
                            imageViewCreateInfo.viewType = description.ArrayLayers == 1
                                ? VkImageViewType.VK_IMAGE_VIEW_TYPE_1D
                                : VkImageViewType.VK_IMAGE_VIEW_TYPE_1D_ARRAY;
                            break;
                        case TextureType.Texture2D:
                            imageViewCreateInfo.viewType = description.ArrayLayers == 1
                                ? VkImageViewType.VK_IMAGE_VIEW_TYPE_2D
                                : VkImageViewType.VK_IMAGE_VIEW_TYPE_2D_ARRAY;
                            break;
                        case TextureType.Texture3D:
                            imageViewCreateInfo.viewType = VkImageViewType.VK_IMAGE_VIEW_TYPE_3D;
                            break;
                    }
                }

                VulkanUtil.CheckResult(vkCreateImageView(_gd.Device, &imageViewCreateInfo, null, &imageView));

                var result = new VulkanTextureView(_gd, description, imageView);
                imageView = default; // ownership transfer
                return result;
            }
            finally
            {
                if (imageView != VkImageView.NULL)
                {
                    vkDestroyImageView(_gd.Device, imageView, null);
                }
            }
        }

        public override Swapchain CreateSwapchain(in SwapchainDescription description)
        {
            throw new NotImplementedException();
        }

        public override Pipeline CreateComputePipeline(in ComputePipelineDescription description)
        {
            throw new NotImplementedException();
        }

        public override Framebuffer CreateFramebuffer(in FramebufferDescription description)
        {
            throw new NotImplementedException();
        }

        public override Pipeline CreateGraphicsPipeline(in GraphicsPipelineDescription description)
        {
            throw new NotImplementedException();
        }

        public override ResourceLayout CreateResourceLayout(in ResourceLayoutDescription description)
        {
            throw new NotImplementedException();
        }

        public override ResourceSet CreateResourceSet(in ResourceSetDescription description)
        {
            throw new NotImplementedException();
        }

        public override Shader CreateShader(in ShaderDescription description)
        {
            throw new NotImplementedException();
        }
    }
}
