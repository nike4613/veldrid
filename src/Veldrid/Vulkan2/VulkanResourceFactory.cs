using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Veldrid.Vulkan2
{
    internal sealed class VulkanResourceFactory : ResourceFactory
    {
        private readonly VulkanGraphicsDevice _gd;

        public VulkanResourceFactory(VulkanGraphicsDevice gd) : base(gd.Features)
        {
            _gd = gd;
        }

        public override GraphicsBackend BackendType => throw new NotImplementedException();

        public override DeviceBuffer CreateBuffer(in BufferDescription description)
        {
            throw new NotImplementedException();
        }

        public override CommandList CreateCommandList(in CommandListDescription description)
        {
            throw new NotImplementedException();
        }

        public override Pipeline CreateComputePipeline(in ComputePipelineDescription description)
        {
            throw new NotImplementedException();
        }

        public override Fence CreateFence(bool signaled)
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

        public override Sampler CreateSampler(in SamplerDescription description)
        {
            throw new NotImplementedException();
        }

        public override Shader CreateShader(in ShaderDescription description)
        {
            throw new NotImplementedException();
        }

        public override Swapchain CreateSwapchain(in SwapchainDescription description)
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
