using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal partial class VulkanGraphicsDevice
    {
        private static unsafe delegate* unmanaged<void> GetInstanceProcAddr(VkInstance instance, ReadOnlySpan<byte> name)
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
                        var result = GetInstanceProcAddr(instance, arr.AsSpan(0, name.Length));
                        ArrayPool<byte>.Shared.Return(arr);
                        return result;
                    }
                }

                return vkGetInstanceProcAddr(instance, (sbyte*)pName);
            }
        }
        private static unsafe delegate* unmanaged<void> GetInstanceProcAddr(VkInstance instance, ReadOnlySpan<byte> name1, ReadOnlySpan<byte> name2)
        {
            var result = GetInstanceProcAddr(instance, name1);
            if (result is null)
            {
                result = GetInstanceProcAddr(instance, name2);
            }
            return result;
        }
        private unsafe delegate* unmanaged<void> GetInstanceProcAddr(ReadOnlySpan<byte> name)
            => GetInstanceProcAddr(_deviceCreateState.Instance, name);
        private unsafe delegate* unmanaged<void> GetInstanceProcAddr(ReadOnlySpan<byte> name1, ReadOnlySpan<byte> name2)
        {
            var result = GetInstanceProcAddr(name1);
            if (result is null)
            {
                result = GetInstanceProcAddr(name2);
            }
            return result;
        }

        private static unsafe delegate* unmanaged<void> GetDeviceProcAddr(VkDevice device, ReadOnlySpan<byte> name)
        {
            fixed (byte* pName = name)
            {
                if (pName[name.Length] != 0)
                {
                    return RetryWithPooledNullTerminator(device, name);

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    static delegate* unmanaged<void> RetryWithPooledNullTerminator(VkDevice device, ReadOnlySpan<byte> name)
                    {
                        var arr = ArrayPool<byte>.Shared.Rent(name.Length + 1);
                        name.CopyTo(arr);
                        arr[name.Length] = 0;
                        var result = GetDeviceProcAddr(device, arr.AsSpan(0, name.Length));
                        ArrayPool<byte>.Shared.Return(arr);
                        return result;
                    }
                }

                return vkGetDeviceProcAddr(device, (sbyte*)pName);
            }
        }
        private static unsafe delegate* unmanaged<void> GetDeviceProcAddr(VkDevice device, ReadOnlySpan<byte> name1, ReadOnlySpan<byte> name2)
        {
            var result = GetDeviceProcAddr(device, name1);
            if (result is null)
            {
                result = GetDeviceProcAddr(device, name2);
            }
            return result;
        }
        private unsafe delegate* unmanaged<void> GetDeviceProcAddr(ReadOnlySpan<byte> name)
            => GetDeviceProcAddr(_deviceCreateState.Device, name);
        private unsafe delegate* unmanaged<void> GetDeviceProcAddr(ReadOnlySpan<byte> name1, ReadOnlySpan<byte> name2)
        {
            var result = GetDeviceProcAddr(name1);
            if (result is null)
            {
                result = GetDeviceProcAddr(name2);
            }
            return result;
        }
    }
}
