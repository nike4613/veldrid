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
using System.Numerics;

namespace Veldrid.Vulkan
{
    internal unsafe partial class VulkanGraphicsDevice
    {
        /// <summary>
        /// DeviceCreateState holds mutable intermediate state that will be (ultimately) used to construct the final VkGraphicsDevice, without
        /// having to pass around a huge mess of parameters.
        /// </summary>
        internal struct DeviceCreateState()
        {
            // Inputs
            public GraphicsDeviceOptions GdOptions;
            public VulkanDeviceOptions VkOptions;

            // Managed Handles
            public VkInstance Instance;
            public VkDebugReportCallbackEXT DebugCallbackHandle;
            public VkSurfaceKHR Surface;
            public VkDevice Device;

            // VkInstance extra information
            public VkVersion ApiVersion;
            public bool HasDeviceProperties2Ext;
            public bool HasDebugReportExt;
            public bool HasStdValidationLayer;
            public bool HasKhrValidationLayer;

            // Physical device information
            public VkPhysicalDevice PhysicalDevice;
            public VkPhysicalDeviceProperties PhysicalDeviceProperties;
            public VkPhysicalDeviceFeatures PhysicalDeviceFeatures;
            public VkPhysicalDeviceMemoryProperties PhysicalDeviceMemoryProperties;
            public QueueFamilyProperties QueueFamilyInfo = new();

            // VkDevice auxiliary information
            public VkQueue MainQueue;
            public bool HasDebugMarkerExt;
            public bool HasMaintenance1Ext;
            public bool HasMemReqs2Ext;
            public bool HasDedicatedAllocationExt;
            public bool HasDriverPropertiesExt;
            public bool HasDynamicRendering;
            public bool HasSync2Ext;
        }

        public static VulkanGraphicsDevice CreateDevice(GraphicsDeviceOptions gdOpts, VulkanDeviceOptions vkOpts, SwapchainDescription? swapchainDesc)
        {
            var dcs = new DeviceCreateState()
            {
                GdOptions = gdOpts,
                VkOptions = vkOpts,
            };

            try
            {
                dcs.Instance = CreateInstance(ref dcs, out var surfaceExtensionList);

                if (swapchainDesc is { } swdesc)
                {
                    dcs.Surface = CreateSurface(dcs.Instance, surfaceExtensionList, swdesc.Source);
                }

                dcs.PhysicalDevice = SelectPhysicalDevice(dcs.Instance, out dcs.PhysicalDeviceProperties);
                vkGetPhysicalDeviceFeatures(dcs.PhysicalDevice, &dcs.PhysicalDeviceFeatures);
                vkGetPhysicalDeviceMemoryProperties(dcs.PhysicalDevice, &dcs.PhysicalDeviceMemoryProperties);
                // TODO: vkGetPhysicalDeviceFeatures2 to properly identify which features are available

                dcs.QueueFamilyInfo = IdentifyQueueFamilies(dcs.PhysicalDevice, dcs.Surface);

                dcs.Device = CreateLogicalDevice(ref dcs);

                return new VulkanGraphicsDevice(ref dcs, swapchainDesc, surfaceExtensionList);
            }
            finally
            {
                // if we reach here with non-NULL locals, then an error occurred and we should be good API users and clean up

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

        internal struct QueueFamilyProperties()
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

                // TODO: how can we identify a valid presentation queue when we're started with no target surface?

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

        private static VkDevice CreateLogicalDevice(ref DeviceCreateState dcs)
        {
            // note: at the moment we only actually support outputting to one queue
            Debug.Assert(dcs.QueueFamilyInfo.MainGraphicsFamilyIdx >= 0);
            Debug.Assert(dcs.QueueFamilyInfo.PresentFamilyIdx is -1 || dcs.QueueFamilyInfo.MainGraphicsFamilyIdx == dcs.QueueFamilyInfo.PresentFamilyIdx);
            Debug.Assert(dcs.QueueFamilyInfo.MainComputeFamilyIdx is -1 || dcs.QueueFamilyInfo.MainGraphicsFamilyIdx == dcs.QueueFamilyInfo.MainComputeFamilyIdx);
            // IF ANY OF THE ABOVE CONDITIONS CHANGE, AND WE BEGIN TO CREATE MULTIPLE QUEUES, vkGetDeviceQueue BELOW MUST ALSO CHANGE
            // THERE ARE OTHER PLACES AROUND THE CODEBASE WHICH MUST ALSO CHANGE, INCLUDING VulkanSwapchain AND THE SYNCHRONIZATION CODE

            var queuePriority = 1f;
            var queueCreateInfo = new VkDeviceQueueCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                queueFamilyIndex = (uint)dcs.QueueFamilyInfo.MainGraphicsFamilyIdx,
                queueCount = 1,
                pQueuePriorities = &queuePriority,
            };

            var requiredDeviceExtensions = new HashSet<string>(dcs.VkOptions.DeviceExtensions ?? []);

            uint numDeviceExtensions = 0;
            VulkanUtil.CheckResult(vkEnumerateDeviceExtensionProperties(dcs.PhysicalDevice, null, &numDeviceExtensions, null));
            var extensionProps = new VkExtensionProperties[numDeviceExtensions];
            var activeExtensions = new nint[numDeviceExtensions];
            uint activeExtensionCount = 0;

            VkDevice device;
            fixed (VkExtensionProperties* pExtensionProps = extensionProps)
            fixed (nint* pActiveExtensions = activeExtensions)
            {
                VulkanUtil.CheckResult(vkEnumerateDeviceExtensionProperties(dcs.PhysicalDevice, null, &numDeviceExtensions, pExtensionProps));

                // TODO: all of these version-gated options are technically conditional on a physical device feature. We should be using that instead.
                dcs.HasMemReqs2Ext = dcs.ApiVersion >= new VkVersion(1, 1, 0);
                dcs.HasMaintenance1Ext = dcs.ApiVersion >= new VkVersion(1, 1, 0);
                dcs.HasDedicatedAllocationExt = dcs.ApiVersion >= new VkVersion(1, 1, 0);
                dcs.HasDriverPropertiesExt = dcs.ApiVersion >= new VkVersion(1, 2, 0);
                dcs.HasDebugMarkerExt = false;
                dcs.HasDynamicRendering = dcs.ApiVersion >= new VkVersion(1, 3, 0);
                dcs.HasSync2Ext = dcs.ApiVersion >= new VkVersion(1, 3, 0);

                for (var i = 0; i < numDeviceExtensions; i++)
                {
                    var name = Util.GetString(pExtensionProps[i].extensionName);
                    switch (name)
                    {
                        case "VK_EXT_debug_marker":
                        //case "VK_EXT_debug_utils":
                            dcs.HasDebugMarkerExt = true;
                            goto EnableExtension;

                        case "VK_KHR_swapchain":
                        case "VK_KHR_portability_subset":
                            goto EnableExtension;

                        case "VK_KHR_maintenance1":
                            dcs.HasMaintenance1Ext = true;
                            goto EnableExtension;
                        case "VK_KHR_get_memory_requirements2":
                            dcs.HasMemReqs2Ext = true;
                            goto EnableExtension;
                        case "VK_KHR_dedicated_allocation":
                            dcs.HasDedicatedAllocationExt = true;
                            goto EnableExtension;
                        case "VK_KHR_driver_properties":
                            dcs.HasDriverPropertiesExt = true;
                            goto EnableExtension;

                        case "VK_KHR_dynamic_rendering":
                            dcs.HasDynamicRendering = true;
                            goto EnableExtension;
                        case "VK_KHR_synchronization2":
                            dcs.HasSync2Ext = true;
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
                if (dcs.HasStdValidationLayer)
                {
                    layerNames.Add(CommonStrings.StandardValidationLayerName);
                }
                if (dcs.HasKhrValidationLayer)
                {
                    layerNames.Add(CommonStrings.KhronosValidationLayerName);
                }

                fixed (VkPhysicalDeviceFeatures* pPhysicalDeviceFeatures = &dcs.PhysicalDeviceFeatures)
                {
                    var deviceCreateInfo = new VkDeviceCreateInfo()
                    {
                        sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                        queueCreateInfoCount = 1,
                        pQueueCreateInfos = &queueCreateInfo,

                        pEnabledFeatures = pPhysicalDeviceFeatures,

                        enabledLayerCount = layerNames.Count,
                        ppEnabledLayerNames = (sbyte**)layerNames.Data,

                        enabledExtensionCount = activeExtensionCount,
                        ppEnabledExtensionNames = (sbyte**)pActiveExtensions,
                    };

                    if (dcs.HasDynamicRendering)
                    {
                        // make sure we enable dynamic rendering
                        var dynamicRenderingFeatures = new VkPhysicalDeviceDynamicRenderingFeatures()
                        {
                            sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DYNAMIC_RENDERING_FEATURES,
                            pNext = deviceCreateInfo.pNext,
                            dynamicRendering = (VkBool32)true,
                        };

                        deviceCreateInfo.pNext = &dynamicRenderingFeatures;
                    }

                    if (dcs.HasSync2Ext)
                    {
                        // make sure we enable synchronization2
                        var sync2Features = new VkPhysicalDeviceSynchronization2Features()
                        {
                            sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SYNCHRONIZATION_2_FEATURES,
                            pNext = deviceCreateInfo.pNext,
                            synchronization2 = (VkBool32)true,
                        };

                        deviceCreateInfo.pNext = &sync2Features;
                    }

                    VulkanUtil.CheckResult(vkCreateDevice(dcs.PhysicalDevice, &deviceCreateInfo, null, &device));
                }

                VkQueue localQueue;
                vkGetDeviceQueue(device, (uint)dcs.QueueFamilyInfo.MainGraphicsFamilyIdx, 0, &localQueue);
                dcs.MainQueue = localQueue;

                return device;
            }
        }
    }
}
