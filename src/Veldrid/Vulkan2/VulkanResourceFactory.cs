using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TerraFX.Interop.Vulkan;
using IResourceRefCountTarget = Veldrid.Vulkan.IResourceRefCountTarget;
using ResourceRefCount = Veldrid.Vulkan.ResourceRefCount;
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

        public override DeviceBuffer CreateBuffer(in BufferDescription description)
        {
            throw new NotImplementedException();
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

        public override Texture CreateTexture(in TextureDescription description)
        {
            throw new NotImplementedException();
        }

        public override Texture CreateTexture(ulong nativeTexture, in TextureDescription description)
        {
            throw new NotImplementedException();
        }

        public override TextureView CreateTextureView(in TextureViewDescription description)
        {
            throw new NotImplementedException();
        }
    }
}
