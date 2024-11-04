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

namespace Veldrid.Vulkan2
{
    internal sealed class VulkanSwapchain : Swapchain, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkSurfaceKHR _surface;
        private VkSwapchainKHR _deviceSwapchain;
        private readonly VulkanSwapchainFramebuffer _framebuffer;
        private VkFence _imageAvailableFence;
        private readonly int _presentQueueIndex;
        private readonly VkQueue _presentQueue;
        private uint _currentImageIndex;
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
        public VkFence ImageAvailableFence => _imageAvailableFence;
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
                VkFenceCreateInfo fenceCI = new()
                {
                    sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO
                };

                VkFence imageAvailableFence;
                vkCreateFence(_gd.Device, &fenceCI, null, &imageAvailableFence);
                _imageAvailableFence = imageAvailableFence;

                _framebuffer = new(gd, this, description);

                CreateSwapchain(description.Width, description.Height);
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

            if (_imageAvailableFence != VkFence.NULL)
            {
                vkDestroyFence(_gd.Device, _imageAvailableFence, null);
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

            _framebuffer.SetNewSwapchain(_deviceSwapchain, width, height, surfaceFormat, swapchainCI.imageExtent);
            return true;
        }

        public unsafe bool AcquireNextImage(VkDevice device, VkSemaphore semaphore, VkFence fence)
        {
            if (_newSyncToVBlank != null)
            {
                _syncToVBlank = _newSyncToVBlank.Value;
                _newSyncToVBlank = null;
                RecreateAndReacquire(_framebuffer.Width, _framebuffer.Height);
                return false;
            }

            uint imageIndex = _currentImageIndex;
            VkResult result = vkAcquireNextImageKHR(
                device,
                _deviceSwapchain,
                ulong.MaxValue,
                semaphore,
                fence,
                &imageIndex);
            _framebuffer.SetImageIndex(imageIndex);
            _currentImageIndex = imageIndex;

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
                var imageAvailableFence = _imageAvailableFence;
                if (AcquireNextImage(_gd.Device, default, imageAvailableFence))
                {
                    vkWaitForFences(_gd.Device, 1, &imageAvailableFence, (VkBool32)true, ulong.MaxValue);
                    vkResetFences(_gd.Device, 1, &imageAvailableFence);
                }
            }
        }

        public override void Resize(uint width, uint height) => RecreateAndReacquire(width, height);
    }
}
