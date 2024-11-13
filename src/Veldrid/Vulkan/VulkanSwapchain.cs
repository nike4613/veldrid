using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
    internal sealed class VulkanSwapchain : Swapchain, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkSurfaceKHR _surface;
        private VkSwapchainKHR _deviceSwapchain;
        private readonly VulkanSwapchainFramebuffer _framebuffer;

        private readonly int _presentQueueIndex;
        private readonly VkQueue _presentQueue;

        private VkSemaphore[] _semaphores = [];
        private VkFence[] _fences = [];
        private uint _fenceIndex;
        private uint _currentImageIndex;
        private uint _imageCount;

        private readonly SwapchainSource _swapchainSource;
        private readonly bool _colorSrgb;
        private bool _syncToVBlank;
        private bool? _newSyncToVBlank;

        private string? _name;
        public ResourceRefCount RefCount { get; }
        public object PresentLock { get; } = new();

        public override VulkanSwapchainFramebuffer Framebuffer => _framebuffer;
        public override bool IsDisposed => RefCount.IsDisposed;
        public VkSwapchainKHR DeviceSwapchain => _deviceSwapchain;
        public uint ImageIndex => _currentImageIndex;
        public VkSurfaceKHR Surface => _surface;
        public VkQueue PresentQueue => _presentQueue;
        public int PresentQueueIndex => _presentQueueIndex;

        internal unsafe VulkanSwapchain(VulkanGraphicsDevice gd, in SwapchainDescription description, ref VkSurfaceKHR surface, int presentQueueIndex)
        {
            _gd = gd;
            _surface = surface;

            _swapchainSource = description.Source;
            _syncToVBlank = description.SyncToVerticalBlank;
            _colorSrgb = description.ColorSrgb;

            Debug.Assert(presentQueueIndex != -1);
            _presentQueueIndex = presentQueueIndex;
            _presentQueue = gd._deviceCreateState.MainQueue; // right now, we only ever create one queue

            RefCount = new(this);
            surface = default; // we take ownership of the surface

            try
            {
                _framebuffer = new(gd, this, description);

                CreateSwapchain(description.Width, description.Height);

                // make sure we pre-emptively acquire the first image for the swapchain
                _ = AcquireNextImage(_gd.Device);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public override void Dispose() => RefCount?.DecrementDispose();
        unsafe void IResourceRefCountTarget.RefZeroed()
        {
            _framebuffer?.RefZeroed();

            foreach (var fence in _fences)
            {
                if (fence != VkFence.NULL)
                {
                    vkDestroyFence(_gd.Device, fence, null);
                }
            }
            foreach (var semaphore in _semaphores)
            {
                if (semaphore != VkSemaphore.NULL)
                {
                    vkDestroySemaphore(_gd.Device, semaphore, null);
                }
            }

            if (_deviceSwapchain != VkSwapchainKHR.NULL)
            {
                vkDestroySwapchainKHR(_gd.Device, _deviceSwapchain, null);
            }

            if (_surface != VkSurfaceKHR.NULL)
            {
                vkDestroySurfaceKHR(_gd._deviceCreateState.Instance, _surface, null);
            }
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_SWAPCHAIN_KHR_EXT, _deviceSwapchain.Value, value);
            }
        }

        public override bool SyncToVerticalBlank
        {
            get => _newSyncToVBlank ?? _syncToVBlank;
            set
            {
                if (_syncToVBlank != value)
                {
                    _newSyncToVBlank = value;
                }
            }
        }

        private unsafe bool CreateSwapchain(uint width, uint height)
        {
            var physicalDevice = _gd._deviceCreateState.PhysicalDevice;

            // Obtain the surface capabilities first -- this will indicate whether the surface has been lost.
            VkSurfaceCapabilitiesKHR surfaceCapabilities;
            VkResult result = vkGetPhysicalDeviceSurfaceCapabilitiesKHR(physicalDevice, _surface, &surfaceCapabilities);
            if (result == VkResult.VK_ERROR_SURFACE_LOST_KHR)
            {
                throw new VeldridException($"The Swapchain's underlying surface has been lost.");
            }

            if (surfaceCapabilities.minImageExtent.width == 0 && surfaceCapabilities.minImageExtent.height == 0
                && surfaceCapabilities.maxImageExtent.width == 0 && surfaceCapabilities.maxImageExtent.height == 0)
            {
                return false;
            }

            if (_deviceSwapchain != VkSwapchainKHR.NULL)
            {
                _gd.WaitForIdle();
            }

            _currentImageIndex = 0;
            var surfaceFormatCount = 0u;
            VulkanUtil.CheckResult(vkGetPhysicalDeviceSurfaceFormatsKHR(physicalDevice, _surface, &surfaceFormatCount, null));
            VkSurfaceFormatKHR[] formats = new VkSurfaceFormatKHR[surfaceFormatCount];
            fixed (VkSurfaceFormatKHR* formatsPtr = formats)
            {
                VulkanUtil.CheckResult(vkGetPhysicalDeviceSurfaceFormatsKHR(_gd._deviceCreateState.PhysicalDevice, _surface, &surfaceFormatCount, formatsPtr));
            }

            VkFormat desiredFormat = _colorSrgb
                ? VkFormat.VK_FORMAT_B8G8R8A8_SRGB
                : VkFormat.VK_FORMAT_B8G8R8A8_UNORM;

            VkSurfaceFormatKHR surfaceFormat = new();
            if (formats.Length == 1 && formats[0].format == VkFormat.VK_FORMAT_UNDEFINED)
            {
                surfaceFormat.format = desiredFormat;
                surfaceFormat.colorSpace = VkColorSpaceKHR.VK_COLORSPACE_SRGB_NONLINEAR_KHR;
            }
            else
            {
                foreach (VkSurfaceFormatKHR format in formats)
                {
                    if (format.colorSpace == VkColorSpaceKHR.VK_COLORSPACE_SRGB_NONLINEAR_KHR && format.format == desiredFormat)
                    {
                        surfaceFormat = format;
                        break;
                    }
                }
                if (surfaceFormat.format == VkFormat.VK_FORMAT_UNDEFINED)
                {
                    if (_colorSrgb && surfaceFormat.format != VkFormat.VK_FORMAT_R8G8B8A8_SRGB)
                    {
                        throw new VeldridException($"Unable to create an sRGB Swapchain for this surface.");
                    }

                    surfaceFormat = formats[0];
                }
            }

            uint presentModeCount = 0;
            VulkanUtil.CheckResult(vkGetPhysicalDeviceSurfacePresentModesKHR(physicalDevice, _surface, &presentModeCount, null));
            VkPresentModeKHR[] presentModes = new VkPresentModeKHR[presentModeCount];
            fixed (VkPresentModeKHR* presentModesPtr = presentModes)
            {
                VulkanUtil.CheckResult(vkGetPhysicalDeviceSurfacePresentModesKHR(physicalDevice, _surface, &presentModeCount, presentModesPtr));
            }

            VkPresentModeKHR presentMode = VkPresentModeKHR.VK_PRESENT_MODE_FIFO_KHR;

            if (_syncToVBlank)
            {
                if (presentModes.Contains(VkPresentModeKHR.VK_PRESENT_MODE_FIFO_RELAXED_KHR))
                {
                    presentMode = VkPresentModeKHR.VK_PRESENT_MODE_FIFO_RELAXED_KHR;
                }
            }
            else
            {
                if (presentModes.Contains(VkPresentModeKHR.VK_PRESENT_MODE_MAILBOX_KHR))
                {
                    presentMode = VkPresentModeKHR.VK_PRESENT_MODE_MAILBOX_KHR;
                }
                else if (presentModes.Contains(VkPresentModeKHR.VK_PRESENT_MODE_IMMEDIATE_KHR))
                {
                    presentMode = VkPresentModeKHR.VK_PRESENT_MODE_IMMEDIATE_KHR;
                }
            }

            uint maxImageCount = surfaceCapabilities.maxImageCount == 0 ? uint.MaxValue : surfaceCapabilities.maxImageCount;
            // TODO: it would maybe be a good idea to enable this to be configurable?
            uint imageCount = Math.Min(maxImageCount, surfaceCapabilities.minImageCount + 1);

            uint clampedWidth = Util.Clamp(width, surfaceCapabilities.minImageExtent.width, surfaceCapabilities.maxImageExtent.width);
            uint clampedHeight = Util.Clamp(height, surfaceCapabilities.minImageExtent.height, surfaceCapabilities.maxImageExtent.height);
            VkSwapchainCreateInfoKHR swapchainCI = new()
            {
                sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
                surface = _surface,
                presentMode = presentMode,
                imageFormat = surfaceFormat.format,
                imageColorSpace = surfaceFormat.colorSpace,
                imageExtent = new VkExtent2D() { width = clampedWidth, height = clampedHeight },
                minImageCount = imageCount,
                imageArrayLayers = 1,
                imageUsage = VkImageUsageFlags.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_DST_BIT
            };

            uint* queueFamilyIndices = stackalloc uint[]
            {
                (uint)_gd._deviceCreateState.QueueFamilyInfo.MainGraphicsFamilyIdx,
                (uint)_presentQueueIndex,
            };

            if (queueFamilyIndices[0] != queueFamilyIndices[1])
            {
                swapchainCI.imageSharingMode = VkSharingMode.VK_SHARING_MODE_CONCURRENT;
                swapchainCI.queueFamilyIndexCount = 2;
                swapchainCI.pQueueFamilyIndices = queueFamilyIndices;
            }
            else
            {
                swapchainCI.imageSharingMode = VkSharingMode.VK_SHARING_MODE_EXCLUSIVE;
                swapchainCI.queueFamilyIndexCount = 0;
            }

            swapchainCI.preTransform = VkSurfaceTransformFlagsKHR.VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
            swapchainCI.compositeAlpha = VkCompositeAlphaFlagsKHR.VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
            swapchainCI.clipped = (VkBool32)true;

            VkSwapchainKHR oldSwapchain = _deviceSwapchain;
            swapchainCI.oldSwapchain = oldSwapchain;

            VkSwapchainKHR deviceSwapchain;
            VulkanUtil.CheckResult(vkCreateSwapchainKHR(_gd.Device, &swapchainCI, null, &deviceSwapchain));
            _deviceSwapchain = deviceSwapchain;

            if (Name is { } name)
            {
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_SWAPCHAIN_KHR_EXT, deviceSwapchain.Value, name);
            }

            if (oldSwapchain != VkSwapchainKHR.NULL)
            {
                vkDestroySwapchainKHR(_gd.Device, oldSwapchain, null);
            }

            VulkanUtil.CheckResult(vkGetSwapchainImagesKHR(_gd.Device, deviceSwapchain, &imageCount, null));
            _imageCount = imageCount;

            // as a last step, we need to set up our fences and semaphores
            Util.EnsureArrayMinimumSize(ref _fences, imageCount);
            Util.EnsureArrayMinimumSize(ref _semaphores, imageCount + 1);
            _fenceIndex = 0;

            for (var i = 0; i < _fences.Length; i++)
            {
                if (_fences[i] != VkFence.NULL)
                {
                    // we always want to recreate any fences we have, because we want them to be default-signalled
                    vkDestroyFence(_gd.Device, _fences[i], null);
                    _fences[i] = VkFence.NULL;
                }

                if (i < imageCount)
                {
                    var fenceCi = new VkFenceCreateInfo()
                    {
                        sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO,
                        flags = VkFenceCreateFlags.VK_FENCE_CREATE_SIGNALED_BIT,
                    };
                    VkFence fence;
                    VulkanUtil.CheckResult(vkCreateFence(_gd.Device, &fenceCi, null, &fence));
                    _fences[i] = fence;
                }
            }

            for (var i = 0; i < _semaphores.Length; i++)
            {
                if (_semaphores[i] != VkSemaphore.NULL)
                {
                    // we always want to recreate any semaphores we have, to make sure they aren't signalled when we do our initial acquire
                    vkDestroySemaphore(_gd.Device, _semaphores[i], null);
                    _semaphores[i] = VkSemaphore.NULL;
                }

                if (i < imageCount + 1)
                {
                    var semaphoreCi = new VkSemaphoreCreateInfo() { sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
                    VkSemaphore semaphore;
                    VulkanUtil.CheckResult(vkCreateSemaphore(_gd.Device, &semaphoreCi, null, &semaphore));
                    _semaphores[i] = semaphore;
                }
            }

            _framebuffer.SetNewSwapchain(_deviceSwapchain, width, height, surfaceFormat, swapchainCI.imageExtent);
            return true;
        }

        public unsafe bool AcquireNextImage(VkDevice device)
        {
            if (_newSyncToVBlank != null)
            {
                _syncToVBlank = _newSyncToVBlank.Value;
                _newSyncToVBlank = null;
                RecreateAndReacquire(_framebuffer.Width, _framebuffer.Height);
                return false;
            }

            // first, wait for the i - N'th fence (which mod N is just the current fence, and the one we will be passing to acquire)
            var waitFence = _fences[_fenceIndex];
            _ = vkWaitForFences(_gd.Device, 1, &waitFence, 1, ulong.MaxValue);
            _ = vkResetFences(_gd.Device, 1, &waitFence);

            // then, pick up the semaphore we're going to use
            // we always grab the "extra" one, and we'll swap it into place in the array once we know the image we've acquired
            // The semaphore we pass in to vkAcquireNextImage MUST be unsignaled (so waited-upon), which we guarantee at the callsites
            // of AcquireNextImage. (Either because we *just* recreated the swapchain, or because we are doing a presentation, and thus
            // have a command list that we can force to wait on it.)
            var semaphore = _semaphores[_imageCount];

            uint imageIndex = _currentImageIndex;
            VkResult result = vkAcquireNextImageKHR(
                device,
                _deviceSwapchain,
                ulong.MaxValue,
                semaphore,
                waitFence,
                &imageIndex);
            _framebuffer.SetImageIndex(imageIndex, semaphore);
            _currentImageIndex = imageIndex;
            // swap this semaphore into position
            _semaphores[_imageCount] = _semaphores[imageIndex];
            _semaphores[imageIndex] = semaphore;
            // and move our fence index forward
            _fenceIndex = (_fenceIndex + 1) % _imageCount;

            if (result is VkResult.VK_ERROR_OUT_OF_DATE_KHR or VkResult.VK_SUBOPTIMAL_KHR)
            {
                CreateSwapchain(_framebuffer.Width, _framebuffer.Height);
                return false;
            }
            else if (result != VkResult.VK_SUCCESS)
            {
                throw new VeldridException("Could not acquire next image from the Vulkan swapchain.");
            }

            return true;
        }

        private unsafe void RecreateAndReacquire(uint width, uint height)
        {
            if (CreateSwapchain(width, height))
            {
                _ = AcquireNextImage(_gd.Device);
            }
        }

        public override void Resize(uint width, uint height) => RecreateAndReacquire(width, height);
    }
}
