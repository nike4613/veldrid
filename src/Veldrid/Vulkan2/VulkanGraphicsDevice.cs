using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Buffers;
using System.Runtime.CompilerServices;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using static TerraFX.Interop.Vulkan.VkStructureType;
using static TerraFX.Interop.Vulkan.Vulkan;
using System.Net;

namespace Veldrid.Vulkan2
{
    internal sealed partial class VulkanGraphicsDevice : GraphicsDevice
    {
        private readonly DeviceCreateState _deviceCreateState;

        // optional functions

        // debug marker ext
        private readonly unsafe delegate* unmanaged<VkDevice, VkDebugMarkerObjectNameInfoEXT*, VkResult> vkDebugMarkerSetObjectNameEXT;
        private readonly unsafe delegate* unmanaged<VkCommandBuffer, VkDebugMarkerMarkerInfoEXT*, void> vkCmdDebugMarkerBeginEXT;
        private readonly unsafe delegate* unmanaged<VkCommandBuffer, void> vkCmdDebugMarkerEndEXT;
        private readonly unsafe delegate* unmanaged<VkCommandBuffer, VkDebugMarkerMarkerInfoEXT*, void> vkCmdDebugMarkerInsertEXT;
        // dedicated allocation and memreq2
        private readonly unsafe delegate* unmanaged<VkDevice, VkBufferMemoryRequirementsInfo2*, VkMemoryRequirements2*, void> vkGetBufferMemoryRequirements2;
        private readonly unsafe delegate* unmanaged<VkDevice, VkImageMemoryRequirementsInfo2*, VkMemoryRequirements2*, void> vkGetImageMemoryRequirements2;

        public string? DriverName { get; }
        public string? DriverInfo { get; }

        private unsafe VulkanGraphicsDevice(ref DeviceCreateState deviceCreateState)
        {
            // once we adopt the DCS, default-out the source because the caller will try to free the handles (which we now own)
            _deviceCreateState = deviceCreateState;
            deviceCreateState = default;

            // Populate knowns based on the fact that this is a Vulkan implementation
            BackendType = GraphicsBackend.Vulkan;
            IsUvOriginTopLeft = true;
            IsDepthRangeZeroToOne = true;
            IsClipSpaceYInverted = true;
            ApiVersion = new(
                (int)_deviceCreateState.ApiVersion.Major,
                (int)_deviceCreateState.ApiVersion.Minor,
                (int)_deviceCreateState.ApiVersion.Patch, 0);

            // Then stuff out of the physical device properties
            UniformBufferMinOffsetAlignment = (uint)_deviceCreateState.PhysicalDeviceProperties.limits.minUniformBufferOffsetAlignment;
            StructuredBufferMinOffsetAlignment = (uint)_deviceCreateState.PhysicalDeviceProperties.limits.minStorageBufferOffsetAlignment;
            DeviceName = Util.GetString(_deviceCreateState.PhysicalDeviceProperties.deviceName);

            // Then driver properties (if available)
            if (_deviceCreateState.HasDriverPropertiesExt)
            {
                var vkGetPhysicalDeviceProperties2 =
                    (delegate* unmanaged<VkPhysicalDevice, void*, void>)
                    GetInstanceProcAddr("vkGetPhysicalDeviceProperties2"u8, "vkGetPhysicalDeviceProperties2KHR"u8);

                if (vkGetPhysicalDeviceProperties2 is not null)
                {
                    var driverProps = new VkPhysicalDeviceDriverProperties()
                    {
                        sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES,
                    };
                    var deviceProps = new VkPhysicalDeviceProperties2()
                    {
                        sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,
                        pNext = &driverProps,
                    };
                    vkGetPhysicalDeviceProperties2(_deviceCreateState.PhysicalDevice, &deviceProps);

                    DriverName = Util.GetString(driverProps.driverName);
                    DriverInfo = Util.GetString(driverProps.driverInfo);

                    ApiVersion = new(
                        driverProps.conformanceVersion.major,
                        driverProps.conformanceVersion.minor,
                        driverProps.conformanceVersion.subminor,
                        driverProps.conformanceVersion.patch);
                }
            }

            // Then several optional extension functions
            if (_deviceCreateState.HasDebugMarkerExt)
            {
                vkDebugMarkerSetObjectNameEXT =
                    (delegate* unmanaged<VkDevice, VkDebugMarkerObjectNameInfoEXT*, VkResult>)GetInstanceProcAddr("vkDebugMarkerSetObjectNameEXT"u8);
                vkCmdDebugMarkerBeginEXT =
                    (delegate* unmanaged<VkCommandBuffer, VkDebugMarkerMarkerInfoEXT*, void>)GetInstanceProcAddr("vkCmdDebugMarkerBeginEXT"u8);
                vkCmdDebugMarkerEndEXT =
                    (delegate* unmanaged<VkCommandBuffer, void>)GetInstanceProcAddr("vkCmdDebugMarkerEndEXT"u8);
                vkCmdDebugMarkerInsertEXT =
                    (delegate* unmanaged<VkCommandBuffer, VkDebugMarkerMarkerInfoEXT*, void>)GetInstanceProcAddr("vkCmdDebugMarkerInsertEXT"u8);
            }

            if (_deviceCreateState.HasDedicatedAllocationExt && _deviceCreateState.HasMemReqs2Ext)
            {
                vkGetBufferMemoryRequirements2 =
                    (delegate* unmanaged<VkDevice, VkBufferMemoryRequirementsInfo2*, VkMemoryRequirements2*, void>)
                    GetDeviceProcAddr("vkGetBufferMemoryRequirements2"u8, "vkGetBufferMemoryRequirements2KHR"u8);
                vkGetImageMemoryRequirements2 =
                    (delegate* unmanaged<VkDevice, VkImageMemoryRequirementsInfo2*, VkMemoryRequirements2*, void>)
                    GetDeviceProcAddr("vkGetImageMemoryRequirements2"u8, "vkGetImageMemoryRequirements2KHR"u8);
            }

            throw new NotImplementedException();

        }

        private bool _disposed;

        protected override unsafe void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (_disposed) return;
            _disposed = true;

            // TODO: destroy all other associated information

            var dcs = _deviceCreateState;

            if (dcs.Device != VkDevice.NULL)
            {
                vkDestroyDevice(dcs.Device, null);
            }

            if (dcs.Surface != VkSurfaceKHR.NULL)
            {
                vkDestroySurfaceKHR(dcs.Instance, dcs.Surface, null);
            }

            if (dcs.DebugCallbackHandle != VkDebugReportCallbackEXT.NULL)
            {
                var vkDestroyDebugReportCallbackEXT =
                    (delegate* unmanaged<VkInstance, VkDebugReportCallbackEXT, VkAllocationCallbacks*, void>)
                    GetInstanceProcAddr(dcs.Instance, "vkDestroyDebugReportCallbackEXT"u8);
                vkDestroyDebugReportCallbackEXT(dcs.Instance, dcs.DebugCallbackHandle, null);
            }

            if (dcs.Instance != VkInstance.NULL)
            {
                vkDestroyInstance(dcs.Instance, null);
            }
        }

        public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
        {
            throw new NotImplementedException();
        }

        public override void ResetFence(Fence fence)
        {
            throw new NotImplementedException();
        }

        public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
        {
            throw new NotImplementedException();
        }

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            throw new NotImplementedException();
        }

        private protected override bool GetPixelFormatSupportCore(PixelFormat format, TextureType type, TextureUsage usage, out PixelFormatProperties properties)
        {
            throw new NotImplementedException();
        }

        private protected override MappedResource MapCore(MappableResource resource, uint bufferOffsetInBytes, uint sizeInBytes, MapMode mode, uint subresource)
        {
            throw new NotImplementedException();
        }

        private protected override void SubmitCommandsCore(CommandList commandList, Fence? fence)
        {
            throw new NotImplementedException();
        }

        private protected override void SwapBuffersCore(Swapchain swapchain)
        {
            throw new NotImplementedException();
        }

        private protected override void UnmapCore(MappableResource resource, uint subresource)
        {
            throw new NotImplementedException();
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, nint source, uint sizeInBytes)
        {
            throw new NotImplementedException();
        }

        private protected override void UpdateTextureCore(Texture texture, nint source, uint sizeInBytes, uint x, uint y, uint z, uint width, uint height, uint depth, uint mipLevel, uint arrayLayer)
        {
            throw new NotImplementedException();
        }

        private protected override void WaitForIdleCore()
        {
            throw new NotImplementedException();
        }
    }
}
