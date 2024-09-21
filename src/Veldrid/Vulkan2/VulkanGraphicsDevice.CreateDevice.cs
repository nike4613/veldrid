using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using StackListNI = Veldrid.Vulkan.StackList<System.IntPtr>;
using CommonStrings = Veldrid.Vulkan.CommonStrings;
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
            var debugCallbackHandle = VkDebugReportCallbackEXT.NULL;
            var surface = VkSurfaceKHR.NULL;
            var logicalDevice = VkDevice.NULL;

            try
            {
                instance = CreateInstance(gdOpts.Debug, vkOpts.InstanceExtensions,
                    out var apiVersion,
                    out var surfaceExtensionList,
                    out var vkGetPhysicalDeviceProperties2,
                    out debugCallbackHandle,
                    out var hasDebugReportExt,
                    out var hasStdValidation,
                    out var hasKhrValidation);

                if (swapchainDesc is { } swdesc)
                {
                    surface = CreateSurface(instance, surfaceExtensionList, swdesc.Source);
                }

                var physicalDevice = SelectPhysicalDevice(instance, out var physicalDeviceProps);

                VkPhysicalDeviceFeatures physicalDeviceFeatures;
                vkGetPhysicalDeviceFeatures(physicalDevice, &physicalDeviceFeatures);

                var queueFamilyInfo = IdentifyQueueFamilies(physicalDevice, surface);

                logicalDevice = CreateLogicalDevice(instance, physicalDevice, surface,
                    physicalDeviceFeatures, queueFamilyInfo, vkOpts,
                    hasStdValidation, hasKhrValidation,
                    out var mainQueue,
                    out var hasDebugMarker,
                    out var hasMemReqs2,
                    out var hasDedicatedAllocation,
                    out var hasDriverProperties);

                throw new NotImplementedException();
            }
            finally
            {
                // if we reach here with non-NULL locals, then an error occurred and we should be good API users and clean up

                if (logicalDevice != VkDevice.NULL)
                {
                    vkDestroyDevice(logicalDevice, null);
                }

                if (surface != VkSurfaceKHR.NULL)
                {
                    vkDestroySurfaceKHR(instance, surface, null);
                }

                if (debugCallbackHandle != VkDebugReportCallbackEXT.NULL)
                {
                    var vkDestroyDebugReportCallbackEXT =
                        (delegate* unmanaged<VkInstance, VkDebugReportCallbackEXT, VkAllocationCallbacks*, void>)
                        GetInstanceProcAddr(instance, "vkDestroyDebugReportCallbackEXT"u8);
                    vkDestroyDebugReportCallbackEXT(instance, debugCallbackHandle, null);
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

        private struct QueueFamilyProperties()
        {
            public int MainGraphicsFamilyIdx = -1;
            public int PresentFamilyIdx = -1;
            public int MainComputeFamilyIdx = -1;
            //public int AsyncComputeFamilyIdx = -1;
        }

        private static QueueFamilyProperties IdentifyQueueFamilies(VkPhysicalDevice device, VkSurfaceKHR optSurface)
        {
            var result = new QueueFamilyProperties();

            uint familyCount = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
            var families = new VkQueueFamilyProperties[familyCount];
            fixed (VkQueueFamilyProperties* pFamilies = families)
            {
                vkGetPhysicalDeviceQueueFamilyProperties(device, &familyCount, pFamilies);
            }

            for (var i = 0; i < familyCount; i++)
            {
                ref var props = ref families[i];
                if (props.queueCount <= 0) continue;

                var used = false;

                if (result.MainGraphicsFamilyIdx < 0 && (props.queueFlags & VkQueueFlags.VK_QUEUE_GRAPHICS_BIT) != 0)
                {
                    used = true;
                    result.MainGraphicsFamilyIdx = i;
                }

                if (result.MainComputeFamilyIdx < 0 && (props.queueFlags & VkQueueFlags.VK_QUEUE_COMPUTE_BIT) != 0)
                {
                    used = true;
                    result.MainComputeFamilyIdx = i;
                }

                // we only care about present queues which are ALSO main queues
                if (used && result.PresentFamilyIdx < 0 && optSurface != VkSurfaceKHR.NULL)
                {
                    uint presentSupported;
                    var vkr = vkGetPhysicalDeviceSurfaceSupportKHR(device, (uint)i, optSurface, &presentSupported);
                    if (vkr is VkResult.VK_SUCCESS && presentSupported != 0)
                    {
                        result.PresentFamilyIdx = i;
                    }
                }

                if (used)
                {
                    // mark this queue as having been used once
                    props.queueCount--;
                }


                // TODO: identify an async compute family

                // check for early exit (all relevant family indicies have been found
                if (result.MainGraphicsFamilyIdx >= 0 && result.MainComputeFamilyIdx >= 0)
                {
                    if (optSurface == VkSurfaceKHR.NULL || result.PresentFamilyIdx >= 0)
                    {
                        // we have everything we need
                        break;
                    }
                }
            }

            // note: at the moment we only actually support outputting to one queue
            Debug.Assert(result.MainGraphicsFamilyIdx >= 0);
            Debug.Assert(result.PresentFamilyIdx is -1 || result.MainGraphicsFamilyIdx == result.PresentFamilyIdx);
            Debug.Assert(result.MainComputeFamilyIdx is -1 || result.MainGraphicsFamilyIdx == result.MainComputeFamilyIdx);

            return result;
        }

        private static VkDevice CreateLogicalDevice(
            VkInstance instance, VkPhysicalDevice physicalDevice, VkSurfaceKHR optSurface,
            VkPhysicalDeviceFeatures deviceFeatures, QueueFamilyProperties queueFamilies,
            VulkanDeviceOptions vkOpts,
            bool hasStdValidation, bool hasKhrValidation,
            out VkQueue queue,
            out bool hasDebugMarker,
            out bool hasMemReqs2,
            out bool hasDedicatedAllocation,
            out bool hasDriverProperties)
        {
            // note: at the moment we only actually support outputting to one queue
            Debug.Assert(queueFamilies.MainGraphicsFamilyIdx >= 0);
            Debug.Assert(queueFamilies.PresentFamilyIdx is -1 || queueFamilies.MainGraphicsFamilyIdx == queueFamilies.PresentFamilyIdx);
            Debug.Assert(queueFamilies.MainComputeFamilyIdx is -1 || queueFamilies.MainGraphicsFamilyIdx == queueFamilies.MainComputeFamilyIdx);
            // IF ANY OF THE ABOVE CONDITIONS CHANGE, AND WE BEGIN TO CREATE MULTIPLE QUEUES, vkGetDeviceQueue BELOW MUST ALSO CHANGE

            var queuePriority = 1f;
            var queueCreateInfo = new VkDeviceQueueCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                queueFamilyIndex = (uint)queueFamilies.MainGraphicsFamilyIdx,
                queueCount = 1,
                pQueuePriorities = &queuePriority,
            };

            var requiredDeviceExtensions = new HashSet<string>(vkOpts.DeviceExtensions ?? []);

            uint numDeviceExtensions = 0;
            VulkanUtil.CheckResult(vkEnumerateDeviceExtensionProperties(physicalDevice, null, &numDeviceExtensions, null));
            var extensionProps = new VkExtensionProperties[numDeviceExtensions];
            var activeExtensions = new nint[numDeviceExtensions];
            uint activeExtensionCount = 0;

            VkDevice device;
            fixed (VkExtensionProperties* pExtensionProps = extensionProps)
            fixed (nint* pActiveExtensions = activeExtensions)
            {
                VulkanUtil.CheckResult(vkEnumerateDeviceExtensionProperties(physicalDevice, null, &numDeviceExtensions, pExtensionProps));

                hasMemReqs2 = false;
                hasDedicatedAllocation = false;
                hasDriverProperties = false;

                hasDebugMarker = false;

                for (var i = 0; i < numDeviceExtensions; i++)
                {
                    var name = Util.GetString(pExtensionProps[i].extensionName);
                    switch (name)
                    {
                        case "VK_EXT_debug_marker":
                        case "VK_EXT_debug_utils":
                            hasDebugMarker = true;
                            goto EnableExtension;

                        case "VK_KHR_swapchain":
                        case "VK_KHR_maintenance1":
                        case "VK_KHR_portability_subset":
                            goto EnableExtension;

                        case "VK_KHR_get_memory_requirements2":
                            hasMemReqs2 = true;
                            goto EnableExtension;
                        case "VK_KHR_dedicated_allocation":
                            hasDedicatedAllocation = true;
                            goto EnableExtension;
                        case "VK_KHR_driver_properties":
                            hasDriverProperties = true;
                            goto EnableExtension;

                        default:
                            if (requiredDeviceExtensions.Remove(name))
                            {
                                goto EnableExtension;
                            }
                            else
                            {
                                continue;
                            }

                        EnableExtension:
                            _ = requiredDeviceExtensions.Remove(name);
                            pActiveExtensions[activeExtensionCount++] = (nint)(&pExtensionProps[i].extensionName);
                            break;
                    }
                }

                if (requiredDeviceExtensions.Count != 0)
                {
                    var missingList = string.Join(", ", requiredDeviceExtensions);
                    throw new VeldridException(
                        $"The following Vulkan device extensions were not available: {missingList}");
                }

                StackListNI layerNames = new();
                if (hasStdValidation)
                {
                    layerNames.Add(CommonStrings.StandardValidationLayerName);
                }
                if (hasKhrValidation)
                {
                    layerNames.Add(CommonStrings.KhronosValidationLayerName);
                }

                var deviceCreateInfo = new VkDeviceCreateInfo()
                {
                    sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                    queueCreateInfoCount = 1,
                    pQueueCreateInfos = &queueCreateInfo,

                    pEnabledFeatures = &deviceFeatures,

                    enabledLayerCount = layerNames.Count,
                    ppEnabledLayerNames = (sbyte**)layerNames.Data,

                    enabledExtensionCount = activeExtensionCount,
                    ppEnabledExtensionNames = (sbyte**)pActiveExtensions,
                };

                VulkanUtil.CheckResult(vkCreateDevice(physicalDevice, &deviceCreateInfo, null, &device));

                VkQueue localQueue;
                vkGetDeviceQueue(device, (uint)queueFamilies.MainGraphicsFamilyIdx, 0, &localQueue);
                queue = localQueue;

                return device;
            }
        }
    }
}
