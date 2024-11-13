using System;
using System.Diagnostics;

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

namespace Veldrid.Vulkan
{
    internal sealed class VulkanSwapchainFramebuffer : VulkanFramebufferBase
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VulkanSwapchain _swapchain;
        private readonly PixelFormat? _depthFormat;
        private uint _currentImageIndex;

        private VulkanFramebuffer[] _framebuffers = [];
        private VkImage[] _images = [];
        private VkFormat _imageFormat;
        private VkExtent2D _extent;
        private VkSemaphore _presentSemaphore;

        private uint _imageCount;

        // this doesn't directly represent a resource
        public override string? Name { get; set; }

        public override ResourceRefCount RefCount { get; }
        public override VulkanFramebuffer CurrentFramebuffer => _framebuffers[_currentImageIndex];

        public uint ImageIndex => _currentImageIndex;
        public VulkanSwapchain Swapchain => _swapchain;

        internal VulkanSwapchainFramebuffer(
            VulkanGraphicsDevice gd,
            VulkanSwapchain swapchain,
            in SwapchainDescription description)
        {
            _gd = gd;
            _swapchain = swapchain;
            _depthFormat = description.DepthFormat;

            AttachmentCount = _depthFormat.HasValue ? 2u : 1u; // 1 color + 1 depth

            // SwapchainFramebuffer and Swapchain SHARE their refcount, otherwise they always keep each other alive
            RefCount = swapchain.RefCount;
        }

        // Dispose() here is a NOP because a SwapchainFramebuffer is tightly tied to a Swapchain for lifetime, so that's the managed one
        public override void Dispose() { }

        internal unsafe void RefZeroed()
        {
            DestroySwapchainFramebuffers();
        }

        internal void SetImageIndex(uint index, VkSemaphore presentSemaphore)
        {
            _currentImageIndex = index;
            _colorTargets = _framebuffers[index].ColorTargetsArray;
            _presentSemaphore = presentSemaphore;
        }

        // this will be called when about to submit a command buffer, and we MUST use this semaphore EXACTLY ONCE.
        public VkSemaphore UseFramebufferSemaphore()
        {
            lock (this)
            {
                var sem = _presentSemaphore;
                _presentSemaphore = VkSemaphore.NULL;
                return sem;
            }
        }

        internal unsafe void SetNewSwapchain(
            VkSwapchainKHR deviceSwapchain,
            uint width,
            uint height,
            VkSurfaceFormatKHR surfaceFormat,
            VkExtent2D swapchainExtent)
        {
            Width = width;
            Height = height;

            // Get the images
            uint imageCount = 0;
            VulkanUtil.CheckResult(vkGetSwapchainImagesKHR(_gd.Device, deviceSwapchain, &imageCount, null));
            if (_images.Length < imageCount)
            {
                _images = new VkImage[(int)imageCount];
            }
            fixed (VkImage* pImages = _images)
            {
                VulkanUtil.CheckResult(vkGetSwapchainImagesKHR(_gd.Device, deviceSwapchain, &imageCount, pImages));
            }

            _imageCount = imageCount;
            _imageFormat = surfaceFormat.format;
            _extent = swapchainExtent;

            CreateFramebuffers();

            OutputDescription = OutputDescription.CreateFromFramebuffer(this);
        }
        
        private unsafe void CreateFramebuffers()
        {
            DestroySwapchainFramebuffers();

            CreateDepthTexture();

            Util.EnsureArrayMinimumSize(ref _framebuffers, _imageCount);

            var semaphoreCreateInfo = new VkSemaphoreCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
                flags = 0,
            };

            var fenceCreateInfo = new VkFenceCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO,
                flags = 0,
            };

            for (uint i = 0; i < _images.Length; i++)
            {
                // create the VulkanTexture and framebuffer
                var tex = new VulkanTexture(_gd,
                    TextureDescription.Texture2D(
                        uint.Max(1, _extent.width), uint.Max(1, _extent.height), mipLevels: 1, arrayLayers: 1,
                        VkFormats.VkToVdPixelFormat(_imageFormat), TextureUsage.RenderTarget),
                    _images[i], default, default, parentFramebuffer: this, leaveOpen: true);
                // textures start out in the undefined format, which corresponds to the default value of the layout field.
                tex.AllSyncStates.Fill(default);

                var desc = new FramebufferDescription(_depthTarget?.Target, tex);
                _framebuffers[i] = _gd.ResourceFactory.CreateFramebuffer(desc);
            }

            SetImageIndex(0, default);
        }

        private void CreateDepthTexture()
        {
            if (_depthFormat is { } depthFormat)
            {
                Debug.Assert(!_depthTarget.HasValue);

                var depthTexture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                    Math.Max(1, _extent.width),
                    Math.Max(1, _extent.height),
                    1,
                    1,
                    depthFormat,
                    TextureUsage.DepthStencil));
                _depthTarget = new FramebufferAttachment(depthTexture, 0);
            }
        }

        private unsafe void DestroySwapchainFramebuffers()
        {
            _depthTarget?.Target.Dispose();
            _depthTarget = default;

            foreach (ref var fb in _framebuffers.AsSpan())
            {
                if (fb is not null)
                {
                    fb.Dispose();
                    fb = null!;
                }
            }
        }
    }
}
