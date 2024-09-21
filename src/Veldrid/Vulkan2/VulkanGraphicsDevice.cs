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

namespace Veldrid.Vulkan2
{
    internal unsafe sealed partial class VulkanGraphicsDevice : GraphicsDevice
    {

        private static delegate* unmanaged<void> GetInstanceProcAddr(VkInstance instance, ReadOnlySpan<byte> name)
        {
            fixed (byte* pName = name)
            {
                if (pName[name.Length] != 0)
                {
                    return RetryWithPooledNullTerminator(instance, name);

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    static delegate* unmanaged<void> RetryWithPooledNullTerminator(VkInstance instance, ReadOnlySpan<byte> name)
                    {
                        var arr = ArrayPool<byte>.Shared.Rent(name.Length + 1);
                        name.CopyTo(arr);
                        arr[name.Length] = 0;
                        var result =  GetInstanceProcAddr(instance, arr.AsSpan(0, name.Length));
                        ArrayPool<byte>.Shared.Return(arr);
                        return result;
                    }
                }

                return vkGetInstanceProcAddr(instance, (sbyte*)pName);
            }
        }
        private static delegate* unmanaged<void> GetDeviceProcAddr(VkDevice device, ReadOnlySpan<byte> name)
        {
            fixed (byte* pName = name)
            {
                if (pName[name.Length] != 0)
                {
                    return RetryWithPooledNullTerminator(device, name);

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    static delegate* unmanaged<void> RetryWithPooledNullTerminator(VkDevice instance, ReadOnlySpan<byte> name)
                    {
                        var arr = ArrayPool<byte>.Shared.Rent(name.Length + 1);
                        name.CopyTo(arr);
                        arr[name.Length] = 0;
                        var result = GetDeviceProcAddr(instance, arr.AsSpan(0, name.Length));
                        ArrayPool<byte>.Shared.Return(arr);
                        return result;
                    }
                }

                return vkGetDeviceProcAddr(device, (sbyte*)pName);
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
