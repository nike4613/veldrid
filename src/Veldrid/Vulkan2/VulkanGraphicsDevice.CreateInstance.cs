using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Diagnostics;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using CommonStrings = Veldrid.Vulkan.CommonStrings;
using FixedUtf8String = Veldrid.Vulkan.FixedUtf8String;
using static TerraFX.Interop.Vulkan.VkStructureType;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal unsafe partial class VulkanGraphicsDevice
    {
        private static VkInstance CreateInstance(ref DeviceCreateState dcs, out List<FixedUtf8String> surfaceExtensions)
        {
            var availInstanceLayers = GetInstanceLayers();
            var availInstanceExtensions = GetInstanceExtensions();

            // Identify several extensions we care about
            surfaceExtensions = new();
            if (availInstanceExtensions.Contains(CommonStrings.VK_KHR_portability_subset))
            {
                surfaceExtensions.Add(CommonStrings.VK_KHR_portability_subset);
            }
            if (availInstanceExtensions.Contains(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME))
            {
                surfaceExtensions.Add(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME);
            }
            surfaceExtensions.AddRange(GetSurfaceExtensions(availInstanceExtensions));

            // now collect that information into the final list

            var fixedStringHolder = new List<FixedUtf8String>();
            try
            {
                var instanceExtensionPtrs = new List<nint>();
                var instanceLayerPtrs = new List<nint>();

                foreach (var ext in surfaceExtensions)
                {
                    instanceExtensionPtrs.Add(ext);
                }

                if (dcs.VkOptions.InstanceExtensions is { } requiredInstanceExtensionsList)
                {
                    foreach (var requiredExt in requiredInstanceExtensionsList)
                    {
                        if (!availInstanceExtensions.Contains(requiredExt))
                        {
                            ThrowExtNotAvailable(requiredExt);

                            [DoesNotReturn]
                            static void ThrowExtNotAvailable(string ext)
                                => throw new VeldridException($"The required instance extension was not available: {ext}");
                        }

                        var u8str = new FixedUtf8String(requiredExt);
                        instanceExtensionPtrs.Add(u8str);
                        fixedStringHolder.Add(u8str);
                    }
                }

                dcs.HasDebugReportExt = false;
                dcs.HasStdValidationLayer = false;
                dcs.HasKhrValidationLayer = false;

                if (dcs.GdOptions.Debug)
                {
                    if (availInstanceExtensions.Contains(CommonStrings.VK_EXT_DEBUG_REPORT_EXTENSION_NAME))
                    {
                        dcs.HasDebugReportExt = true;
                        instanceExtensionPtrs.Add(CommonStrings.VK_EXT_DEBUG_REPORT_EXTENSION_NAME);
                    }
                    if (availInstanceLayers.Contains(CommonStrings.StandardValidationLayerName))
                    {
                        dcs.HasStdValidationLayer = true;
                        instanceLayerPtrs.Add(CommonStrings.StandardValidationLayerName);
                    }
                    if (availInstanceLayers.Contains(CommonStrings.KhronosValidationLayerName))
                    {
                        dcs.HasKhrValidationLayer = true;
                        instanceLayerPtrs.Add(CommonStrings.KhronosValidationLayerName);
                    }
                }

                if (availInstanceExtensions.Contains(CommonStrings.VK_KHR_get_physical_device_properties2))
                {
                    dcs.HasDeviceProperties2Ext = true;
                    instanceExtensionPtrs.Add(CommonStrings.VK_KHR_get_physical_device_properties2);
                }

                VkInstance instance;
                fixed (byte* appInfoName = "Veldrid Vk2"u8)
                fixed (IntPtr* ppInstanceExtensions = CollectionsMarshal.AsSpan(instanceExtensionPtrs))
                fixed (IntPtr* ppInstanceLayers = CollectionsMarshal.AsSpan(instanceLayerPtrs))
                {
                    var appinfo = new VkApplicationInfo()
                    {
                        sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
                        apiVersion = new VkVersion(1, 0, 0),
                        // TODO: it'd be nice to be able to customize these...
                        applicationVersion = new VkVersion(1, 0, 0),
                        engineVersion = new VkVersion(1, 0, 0),
                        pApplicationName = (sbyte*)appInfoName,
                        pEngineName = (sbyte*)appInfoName,
                    };

                    var instCreateInfo = new VkInstanceCreateInfo()
                    {
                        sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                        pApplicationInfo = &appinfo,

                        enabledExtensionCount = (uint)instanceExtensionPtrs.Count,
                        ppEnabledExtensionNames = (sbyte**)ppInstanceExtensions,
                        enabledLayerCount = (uint)instanceLayerPtrs.Count,
                        ppEnabledLayerNames = (sbyte**)ppInstanceLayers,
                    };

                    VulkanUtil.CheckResult(vkCreateInstance(&instCreateInfo, null, &instance));

                    // now that we have an instance, we can try to upgrade our target version
                    var vkEnumerateInstanceVersion =
                        (delegate* unmanaged<uint*, VkResult>)GetInstanceProcAddr(instance, "vkEnumerateInstanceVersion"u8);
                    if (vkEnumerateInstanceVersion is not null)
                    {
                        uint supportedVersion;
                        VulkanUtil.CheckResult(vkEnumerateInstanceVersion(&supportedVersion));

                        if (supportedVersion > appinfo.apiVersion)
                        {
                            // the target supports a higher vulkan version than we initialized with, recreate our instance using it
                            vkDestroyInstance(instance, null);

                            appinfo.apiVersion = supportedVersion;
                            VulkanUtil.CheckResult(vkCreateInstance(&instCreateInfo, null, &instance));
                        }
                    }

                    dcs.ApiVersion = new(appinfo.apiVersion);
                }

                // if this instance supports debug reporting, configure that
                dcs.DebugCallbackHandle = default;
                if (dcs.HasDebugReportExt)
                {
                    var flags = VkDebugReportFlagsEXT.VK_DEBUG_REPORT_ERROR_BIT_EXT;
                    if (dcs.GdOptions.Debug)
                    {
                        flags |=
                            VkDebugReportFlagsEXT.VK_DEBUG_REPORT_INFORMATION_BIT_EXT |
                            VkDebugReportFlagsEXT.VK_DEBUG_REPORT_WARNING_BIT_EXT |
                            VkDebugReportFlagsEXT.VK_DEBUG_REPORT_PERFORMANCE_WARNING_BIT_EXT |
                            VkDebugReportFlagsEXT.VK_DEBUG_REPORT_DEBUG_BIT_EXT;
                    }
                    dcs.DebugCallbackHandle = InstallDebugCallback(instance, flags);
                }

                return instance;
            }
            finally
            {
                // clean up after ourselves (and also make sure the FixedUtf8String objects are kept alive)
                foreach (var str in fixedStringHolder)
                {
                    str.Dispose();
                }
            }
        }

        private static VkDebugReportCallbackEXT InstallDebugCallback(VkInstance instance, VkDebugReportFlagsEXT flags)
        {
            var vkCreateDebugReportCallbackEXT =
                (delegate* unmanaged<VkInstance, VkDebugReportCallbackCreateInfoEXT*, VkAllocationCallbacks*, VkDebugReportCallbackEXT*, VkResult>)
                GetInstanceProcAddr(instance, "vkCreateDebugReportCallbackEXT"u8);

            if (vkCreateDebugReportCallbackEXT is null) return VkDebugReportCallbackEXT.NULL;

            var createInfo = new VkDebugReportCallbackCreateInfoEXT()
            {
                sType = VK_STRUCTURE_TYPE_DEBUG_REPORT_CALLBACK_CREATE_INFO_EXT,
                flags = flags,
                pfnCallback = &DebugCallback,
            };

            VkDebugReportCallbackEXT result;
            VulkanUtil.CheckResult(vkCreateDebugReportCallbackEXT(instance, &createInfo, null, &result));
            return result;

            [UnmanagedCallersOnly]
            static uint DebugCallback(
                VkDebugReportFlagsEXT flags,
                VkDebugReportObjectTypeEXT objectType,
                ulong obj,
                nuint location,
                int messageCode,
                sbyte* pLayerPrefix,
                sbyte* pMessage,
                void* pUserData)
            {
                var layerPrefix = Util.GetString(pLayerPrefix);
                var message = Util.GetString(pMessage);

#if DEBUG
                if ((flags & VkDebugReportFlagsEXT.VK_DEBUG_REPORT_ERROR_BIT_EXT) != 0)
                {
                    if (Debugger.IsAttached)
                    {
                        Debugger.Break();
                    }
                }
#endif

                var validationMsg =
                    $"[{flags.ToString().Replace("VK_DEBUG_REPORT_", "").Replace("_BIT_EXT", "")}] " +
                    $"{layerPrefix}({objectType.ToString().Replace("VK_DEBUG_REPORT_OBJECT_TYPE_", "")}) " +
                    $"{message}";

                Debug.WriteLine(validationMsg);

#if VALIDATE_USAGE
                if (flags == VkDebugReportFlagsEXT.VK_DEBUG_REPORT_ERROR_BIT_EXT)
                {
                    VulkanUtil.SetDebugCallbackException(new VeldridException("A Vulkan validation error was encountered:\n"
                        + validationMsg));
                    return 0;
                }
#endif
                return 0;
            }
        }

        private static HashSet<string> GetInstanceLayers()
        {
            uint count;
            VulkanUtil.CheckResult(vkEnumerateInstanceLayerProperties(&count, null));
            if (count == 0)
            {
                return [];
            }

            var set = new HashSet<string>((int)count);

            var propArr = new VkLayerProperties[count];
            fixed (VkLayerProperties* pProps = propArr)
            {
                VulkanUtil.CheckResult(vkEnumerateInstanceLayerProperties(&count, pProps));

                for (var i = 0; i < count; i++)
                {
                    _ = set.Add(Util.GetString(pProps[i].layerName));
                }
            }

            return set;
        }

        private static HashSet<string> GetInstanceExtensions()
        {
            uint count;
            var result = vkEnumerateInstanceExtensionProperties(null, &count, null);
            if (result is not VkResult.VK_SUCCESS)
            {
                return [];
            }
            if (count == 0)
            {
                return [];
            }

            var set = new HashSet<string>((int)count);

            var extArr = new VkExtensionProperties[count];
            fixed (VkExtensionProperties* pExt = extArr)
            {
                VulkanUtil.CheckResult(vkEnumerateInstanceExtensionProperties(null, &count, pExt));

                for (var i = 0; i < count; i++)
                {
                    _ = set.Add(Util.GetString(pExt[i].extensionName));
                }
            }

            return set;
        }

        private static IEnumerable<FixedUtf8String> GetSurfaceExtensions(HashSet<string> instanceExtensions)
        {
            if (instanceExtensions.Contains(CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME))
            {
                yield return CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME;
            }
            if (instanceExtensions.Contains(CommonStrings.VK_KHR_ANDROID_SURFACE_EXTENSION_NAME))
            {
                yield return CommonStrings.VK_KHR_ANDROID_SURFACE_EXTENSION_NAME;
            }
            if (instanceExtensions.Contains(CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME))
            {
                yield return CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME;
            }
            if (instanceExtensions.Contains(CommonStrings.VK_KHR_WAYLAND_SURFACE_EXTENSION_NAME))
            {
                yield return CommonStrings.VK_KHR_WAYLAND_SURFACE_EXTENSION_NAME;
            }
            if (instanceExtensions.Contains(CommonStrings.VK_EXT_METAL_SURFACE_EXTENSION_NAME))
            {
                yield return CommonStrings.VK_EXT_METAL_SURFACE_EXTENSION_NAME;
            }

            // Legacy MoltenVK extensions
            if (instanceExtensions.Contains(CommonStrings.VK_MVK_MACOS_SURFACE_EXTENSION_NAME))
            {
                yield return CommonStrings.VK_MVK_MACOS_SURFACE_EXTENSION_NAME;
            }
            if (instanceExtensions.Contains(CommonStrings.VK_MVK_IOS_SURFACE_EXTENSION_NAME))
            {
                yield return CommonStrings.VK_MVK_IOS_SURFACE_EXTENSION_NAME;
            }
        }
    }
}
