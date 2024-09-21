using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using static TerraFX.Interop.Vulkan.VkStructureType;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal unsafe partial class VulkanGraphicsDevice
    {
        public static VulkanGraphicsDevice CreateDevice(GraphicsDeviceOptions gdOpts, VulkanDeviceOptions vkOpts, SwapchainDescription? swapchainDesc)
        {
            var instance = VkInstance.NULL;
            var surface = VkSurfaceKHR.NULL;

            try
            {
                instance = CreateInstance(gdOpts.Debug, vkOpts.InstanceExtensions,
                    out var apiVersion,
                    out var surfaceExtensionList,
                    out var vkGetPhysicalDeviceProperties2,
                    out var debugCallbackHandle,
                    out var hasDebugReportExt,
                    out var hasStdValidation,
                    out var hasKhrValidation);

                if (swapchainDesc is { } swdesc)
                {
                    surface = CreateSurface(instance, surfaceExtensionList, swdesc.Source);
                }

                var physicalDevice = SelectPhysicalDevice(instance, out var physicalDeviceProps);
            }
            finally
            {
                // if we reach here with non-NULL locals, then an error occurred and we should be good API users and clean up

                if (surface != VkSurfaceKHR.NULL)
                {
                    vkDestroySurfaceKHR(instance, surface, null);
                }

                if (instance != VkInstance.NULL)
                {
                    vkDestroyInstance(instance, null);
                }
            }
        }

        private static ReadOnlySpan<byte> PhysicalDeviceTypePreference => [
                (byte)VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU,
                (byte)VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU,
                (byte)VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_OTHER,
                (byte)VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU,
                (byte)VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_CPU,
            ];

        // TODO: maybe somehow expose device selection?
        private static VkPhysicalDevice SelectPhysicalDevice(VkInstance instance, out VkPhysicalDeviceProperties deviceProps)
        {
            uint deviceCount;
            VulkanUtil.CheckResult(vkEnumeratePhysicalDevices(instance, &deviceCount, null));
            if (deviceCount is 0)
            {
                throw new InvalidOperationException("No physical devices exist.");
            }

            var devices = new VkPhysicalDevice[deviceCount];
            fixed (VkPhysicalDevice* ptr = devices)
            {
                VulkanUtil.CheckResult(vkEnumeratePhysicalDevices(instance, &deviceCount, ptr));
            }

            VkPhysicalDevice selectedDevice = default;
            VkPhysicalDeviceProperties selectedDeviceProps = default;
            var selectedPreferenceIndex = uint.MaxValue;

            for (var i = 0; i < deviceCount; i++)
            {
                var device = devices[i];

                VkPhysicalDeviceProperties props;
                vkGetPhysicalDeviceProperties(device, &props);

                // we want to prefer items earlier in our list, breaking ties by the physical device order
                var preferenceIndex = (uint)PhysicalDeviceTypePreference.IndexOf((byte)props.deviceType);
                if (preferenceIndex < selectedPreferenceIndex)
                {
                    selectedDevice = device;
                    selectedDeviceProps = props;
                    selectedPreferenceIndex = preferenceIndex;
                }
            }

            if (selectedDevice == VkPhysicalDevice.NULL)
            {
                throw new InvalidOperationException($"Physical device selection failed to select any of {deviceCount} devices");
            }

            // now that we've identified, we can return
            deviceProps = selectedDeviceProps;
            return selectedDevice;
        }
    }
}
