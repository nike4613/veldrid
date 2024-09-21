using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TerraFX.Interop.Vulkan;
using Veldrid.Android;
using Veldrid.MetalBindings;

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

        internal static VkSurfaceKHR CreateSurface(VkInstance instance, List<FixedUtf8String> surfaceExtensions, SwapchainSource swapchainSource)
        {
            HashSet<string> instanceExtensions = new(surfaceExtensions.Select(n => n.ToString()));

            void ThrowIfMissing(string name)
            {
                if (!instanceExtensions.Contains(name))
                {
                    throw new VeldridException($"The required instance extension was not available: {name}");
                }
            }

            ThrowIfMissing(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME);

            switch (swapchainSource)
            {
                case XlibSwapchainSource xlibSource:
                    ThrowIfMissing(CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME);
                    return CreateXlib((nint)GetInstanceProcAddr(instance, "vkCreateXlibSurfaceKHR"u8), instance, xlibSource);

                case WaylandSwapchainSource waylandSource:
                    ThrowIfMissing(CommonStrings.VK_KHR_WAYLAND_SURFACE_EXTENSION_NAME);
                    return CreateWayland((nint)GetInstanceProcAddr(instance, "vkCreateWaylandSurfaceKHR"u8), instance, waylandSource);

                case Win32SwapchainSource win32Source:
                    ThrowIfMissing(CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME);
                    return CreateWin32((nint)GetInstanceProcAddr(instance, "vkCreateWin32SurfaceKHR"u8), instance, win32Source);

                case AndroidSurfaceSwapchainSource androidSource:
                    ThrowIfMissing(CommonStrings.VK_KHR_ANDROID_SURFACE_EXTENSION_NAME);
                    IntPtr aNativeWindow = AndroidRuntime.ANativeWindow_fromSurface(androidSource.JniEnv, androidSource.Surface);
                    return CreateAndroidSurface((nint)GetInstanceProcAddr(instance, "vkCreateAndroidSurfaceKHR"u8), instance, aNativeWindow);

                case AndroidWindowSwapchainSource aWindowSource:
                    ThrowIfMissing(CommonStrings.VK_KHR_ANDROID_SURFACE_EXTENSION_NAME);
                    return CreateAndroidSurface((nint)GetInstanceProcAddr(instance, "vkCreateAndroidSurfaceKHR"u8), instance, aWindowSource.ANativeWindow);

                case NSWindowSwapchainSource nsWindowSource:
                    if (instanceExtensions.Contains(CommonStrings.VK_EXT_METAL_SURFACE_EXTENSION_NAME))
                    {
                        return CreateNSWindowSurfaceExt((nint)GetInstanceProcAddr(instance, "vkCreateMetalSurfaceEXT"u8), instance, nsWindowSource);
                    }
                    if (instanceExtensions.Contains(CommonStrings.VK_MVK_MACOS_SURFACE_EXTENSION_NAME))
                    {
                        return CreateNSWindowSurfaceMvk((nint)GetInstanceProcAddr(instance, "vkCreateMacOSSurfaceMVK"u8), instance, nsWindowSource);
                    }
                    throw new VeldridException($"Neither macOS surface extension was available: " +
                        $"{CommonStrings.VK_EXT_METAL_SURFACE_EXTENSION_NAME}, {CommonStrings.VK_MVK_MACOS_SURFACE_EXTENSION_NAME}");

                case NSViewSwapchainSource nsViewSource:
                    if (instanceExtensions.Contains(CommonStrings.VK_EXT_METAL_SURFACE_EXTENSION_NAME))
                    {
                        return CreateNSViewSurfaceExt((nint)GetInstanceProcAddr(instance, "vkCreateMetalSurfaceEXT"u8), instance, nsViewSource);
                    }
                    if (instanceExtensions.Contains(CommonStrings.VK_MVK_MACOS_SURFACE_EXTENSION_NAME))
                    {
                        return CreateNSViewSurfaceMvk((nint)GetInstanceProcAddr(instance, "vkCreateMacOSSurfaceMVK"u8), instance, nsViewSource);
                    }
                    throw new VeldridException($"Neither macOS surface extension was available: " +
                        $"{CommonStrings.VK_EXT_METAL_SURFACE_EXTENSION_NAME}, {CommonStrings.VK_MVK_MACOS_SURFACE_EXTENSION_NAME}");

                case UIViewSwapchainSource uiViewSource:
                    if (instanceExtensions.Contains(CommonStrings.VK_EXT_METAL_SURFACE_EXTENSION_NAME))
                    {
                        return CreateUIViewSurfaceExt((nint)GetInstanceProcAddr(instance, "vkCreateMetalSurfaceEXT"u8), instance, uiViewSource);
                    }
                    if (instanceExtensions.Contains(CommonStrings.VK_MVK_IOS_SURFACE_EXTENSION_NAME))
                    {
                        return CreateUIViewSurfaceMvk((nint)GetInstanceProcAddr(instance, "vkCreateIOSSurfaceMVK"u8), instance, uiViewSource);
                    }
                    throw new VeldridException($"Neither macOS surface extension was available: " +
                        $"{CommonStrings.VK_EXT_METAL_SURFACE_EXTENSION_NAME}, {CommonStrings.VK_MVK_IOS_SURFACE_EXTENSION_NAME}");

                default:
                    throw new VeldridException($"The provided SwapchainSource cannot be used to create a Vulkan surface.");
            }
        }

        private static VkSurfaceKHR CreateWin32(
            IntPtr khr, VkInstance instance, Win32SwapchainSource win32Source)
        {
            VkWin32SurfaceCreateInfoKHR surfaceCI = new()
            {
                sType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR,
                hwnd = (void*)win32Source.Hwnd,
                hinstance = (void*)win32Source.Hinstance
            };
            VkSurfaceKHR surface;
            VkResult result = ((delegate* unmanaged<VkInstance, VkWin32SurfaceCreateInfoKHR*, VkAllocationCallbacks*, VkSurfaceKHR*, VkResult>)khr)(
                instance, &surfaceCI, null, &surface);
            VulkanUtil.CheckResult(result);
            return surface;
        }

        private static VkSurfaceKHR CreateXlib(
            IntPtr khr, VkInstance instance, XlibSwapchainSource xlibSource)
        {
            VkXlibSurfaceCreateInfoKHR xsci = new()
            {
                sType = VK_STRUCTURE_TYPE_XLIB_SURFACE_CREATE_INFO_KHR,
                dpy = (void*)xlibSource.Display,
                window = (nuint)xlibSource.Window
            };
            VkSurfaceKHR surface;
            VkResult result = ((delegate* unmanaged<VkInstance, VkXlibSurfaceCreateInfoKHR*, VkAllocationCallbacks*, VkSurfaceKHR*, VkResult>)khr)(
                instance, &xsci, null, &surface);
            VulkanUtil.CheckResult(result);
            return surface;
        }

        private static VkSurfaceKHR CreateWayland(
            IntPtr khr, VkInstance instance, WaylandSwapchainSource waylandSource)
        {
            VkWaylandSurfaceCreateInfoKHR wsci = new()
            {
                sType = VK_STRUCTURE_TYPE_WAYLAND_SURFACE_CREATE_INFO_KHR,
                display = (void*)waylandSource.Display,
                surface = (void*)waylandSource.Surface
            };
            VkSurfaceKHR surface;
            VkResult result = ((delegate* unmanaged<VkInstance, VkWaylandSurfaceCreateInfoKHR*, VkAllocationCallbacks*, VkSurfaceKHR*, VkResult>)khr)(
                instance, &wsci, null, &surface);
            VulkanUtil.CheckResult(result);
            return surface;
        }

        private static VkSurfaceKHR CreateAndroidSurface(IntPtr khr, VkInstance instance, IntPtr aNativeWindow)
        {
            VkAndroidSurfaceCreateInfoKHR androidSurfaceCI = new()
            {
                sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR,
                window = (void*)aNativeWindow
            };
            VkSurfaceKHR surface;
            VkResult result = ((delegate* unmanaged<VkInstance, VkAndroidSurfaceCreateInfoKHR*, VkAllocationCallbacks*, VkSurfaceKHR*, VkResult>)khr)(
                instance, &androidSurfaceCI, null, &surface);
            VulkanUtil.CheckResult(result);
            return surface;
        }

        private static unsafe VkSurfaceKHR CreateNSWindowSurfaceExt(IntPtr ext, VkInstance instance, NSWindowSwapchainSource nsWindowSource)
        {
            NSWindow nswindow = new(nsWindowSource.NSWindow);
            return CreateNSViewSurfaceExt(ext, instance, new NSViewSwapchainSource(nswindow.contentView.NativePtr));
        }

        private static unsafe VkSurfaceKHR CreateNSWindowSurfaceMvk(IntPtr mvk, VkInstance instance, NSWindowSwapchainSource nsWindowSource)
        {
            NSWindow nswindow = new(nsWindowSource.NSWindow);
            return CreateNSViewSurfaceMvk(mvk, instance, new NSViewSwapchainSource(nswindow.contentView.NativePtr));
        }

        private static void GetMetalLayerFromNSView(NSView contentView, out CAMetalLayer metalLayer)
        {
            if (!CAMetalLayer.TryCast(contentView.layer, out metalLayer))
            {
                metalLayer = CAMetalLayer.New();
                contentView.wantsLayer = true;
                contentView.layer = metalLayer.NativePtr;
            }
        }

        private static unsafe VkSurfaceKHR CreateNSViewSurfaceExt(
            IntPtr ext, VkInstance instance, NSViewSwapchainSource nsViewSource)
        {
            NSView contentView = new(nsViewSource.NSView);
            GetMetalLayerFromNSView(contentView, out CAMetalLayer metalLayer);

            VkMetalSurfaceCreateInfoEXT surfaceCI = new()
            {
                sType = VK_STRUCTURE_TYPE_METAL_SURFACE_CREATE_INFO_EXT,
                pLayer = (nint*)metalLayer.NativePtr
            };
            VkSurfaceKHR surface;
            VkResult result = ((delegate* unmanaged<VkInstance, VkMetalSurfaceCreateInfoEXT*, VkAllocationCallbacks*, VkSurfaceKHR*, VkResult>)ext)(
                instance, &surfaceCI, null, &surface);
            VulkanUtil.CheckResult(result);
            return surface;
        }

        private static unsafe VkSurfaceKHR CreateNSViewSurfaceMvk(
            IntPtr mvk, VkInstance instance, NSViewSwapchainSource nsViewSource)
        {
            NSView contentView = new(nsViewSource.NSView);
            GetMetalLayerFromNSView(contentView, out _);

            VkMacOSSurfaceCreateInfoMVK surfaceCI = new()
            {
                sType = VK_STRUCTURE_TYPE_MACOS_SURFACE_CREATE_INFO_MVK,
                pView = (void*)contentView.NativePtr
            };
            VkSurfaceKHR surface;
            VkResult result = ((delegate* unmanaged<VkInstance, VkMacOSSurfaceCreateInfoMVK*, VkAllocationCallbacks*, VkSurfaceKHR*, VkResult>)mvk)(
                instance, &surfaceCI, null, &surface);
            VulkanUtil.CheckResult(result);
            return surface;
        }

        private static void GetMetalLayerFromUIView(UIView uiView, out CAMetalLayer metalLayer)
        {
            if (!CAMetalLayer.TryCast(uiView.layer, out metalLayer))
            {
                metalLayer = CAMetalLayer.New();
                metalLayer.frame = uiView.frame;
                metalLayer.opaque = true;
                uiView.layer.addSublayer(metalLayer.NativePtr);
            }
        }

        private static VkSurfaceKHR CreateUIViewSurfaceExt(
            IntPtr ext, VkInstance instance, UIViewSwapchainSource uiViewSource)
        {
            UIView uiView = new(uiViewSource.UIView);
            GetMetalLayerFromUIView(uiView, out CAMetalLayer metalLayer);

            VkMetalSurfaceCreateInfoEXT surfaceCI = new()
            {
                sType = VK_STRUCTURE_TYPE_METAL_SURFACE_CREATE_INFO_EXT,
                pLayer = (nint*)metalLayer.NativePtr
            };
            VkSurfaceKHR surface;
            VkResult result = ((delegate* unmanaged<VkInstance, VkMetalSurfaceCreateInfoEXT*, VkAllocationCallbacks*, VkSurfaceKHR*, VkResult>)ext)(
                instance, &surfaceCI, null, &surface);
            VulkanUtil.CheckResult(result);
            return surface;
        }

        private static VkSurfaceKHR CreateUIViewSurfaceMvk(
            IntPtr mvk, VkInstance instance, UIViewSwapchainSource uiViewSource)
        {
            UIView uiView = new(uiViewSource.UIView);
            GetMetalLayerFromUIView(uiView, out _);

            VkIOSSurfaceCreateInfoMVK surfaceCI = new()
            {
                sType = VK_STRUCTURE_TYPE_IOS_SURFACE_CREATE_INFO_MVK,
                pView = uiView.NativePtr.ToPointer()
            };
            VkSurfaceKHR surface;
            VkResult result = ((delegate* unmanaged<VkInstance, VkIOSSurfaceCreateInfoMVK*, VkAllocationCallbacks*, VkSurfaceKHR*, VkResult>)mvk)(
                instance, &surfaceCI, null, &surface);
            VulkanUtil.CheckResult(result);
            return surface;
        }
    }
}
