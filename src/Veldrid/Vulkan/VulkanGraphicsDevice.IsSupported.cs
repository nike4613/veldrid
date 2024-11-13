using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan
{
    internal partial class VulkanGraphicsDevice
    {
        private static readonly Lazy<bool> s_isSupported = new(CheckIsSupported);
        private static readonly FixedUtf8String s_name = "Veldrid";

        internal static bool IsSupported()
        {
            return s_isSupported.Value;
        }

        private static unsafe bool CheckIsSupported()
        {
            VkApplicationInfo applicationInfo = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_APPLICATION_INFO,
                apiVersion = new VkVersion(1, 0, 0),
                applicationVersion = new VkVersion(1, 0, 0),
                engineVersion = new VkVersion(1, 0, 0),
                pApplicationName = s_name,
                pEngineName = s_name
            };

            VkInstanceCreateInfo instanceCI = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                pApplicationInfo = &applicationInfo
            };

            VkInstance testInstance;
            VkResult result = vkCreateInstance(&instanceCI, null, &testInstance);
            if (result != VkResult.VK_SUCCESS)
            {
                return false;
            }

            uint physicalDeviceCount = 0;
            result = vkEnumeratePhysicalDevices(testInstance, &physicalDeviceCount, null);
            if (result != VkResult.VK_SUCCESS || physicalDeviceCount == 0)
            {
                vkDestroyInstance(testInstance, null);
                return false;
            }

            vkDestroyInstance(testInstance, null);

            return true;

#if false // Vulkan is supported even if it can't present. (This may not by useful for the typical case, but is in general.)
            HashSet<string> instanceExtensions = GetInstanceExtensions();
            if (!instanceExtensions.Contains(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME))
            {
                //return false;
            }

            foreach (FixedUtf8String surfaceExtension in GetSurfaceExtensions(instanceExtensions))
            {
                if (instanceExtensions.Contains(surfaceExtension))
                {
                    return true;
                }
            }

            return false;
#endif
        }
    }
}
