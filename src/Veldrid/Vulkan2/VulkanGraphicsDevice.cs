﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using VkFormats = Veldrid.Vulkan.VkFormats;
using VkMemoryBlock = Veldrid.Vulkan.VkMemoryBlock;
using VkDeviceMemoryManager = Veldrid.Vulkan.VkDeviceMemoryManager;
using static TerraFX.Interop.Vulkan.VkStructureType;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal sealed partial class VulkanGraphicsDevice : GraphicsDevice
    {
        internal readonly DeviceCreateState _deviceCreateState;

        private readonly VkDeviceMemoryManager _memoryManager;
        private readonly VulkanDescriptorPoolManager _descriptorPoolManager;
        internal readonly object QueueLock = new();

        public VkDeviceMemoryManager MemoryManager => _memoryManager;

        private readonly ConcurrentBag<VkSemaphore> _availableSemaphores = new();
        private readonly ConcurrentBag<VkFence> _availableSubmissionFences = new();

        private const int MaxSharedCommandLists = 8;
        private readonly ConcurrentBag<VulkanCommandList> _sharedCommandLists = new();

        private readonly object _fenceCompletionCallbackLock = new();
        private readonly List<FenceCompletionCallbackInfo> _fenceCompletionCallbacks = new();

        private const uint MinStagingBufferSize = 64;
        private const uint MaxStagingBufferSize = 512;
        private readonly List<VulkanBuffer> _availableStagingBuffers = new();

        // optional functions

        // synchronization2
        internal readonly unsafe delegate* unmanaged<VkQueue, uint, VkSubmitInfo2*, VkFence, VkResult> vkQueueSubmit2;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, VkDependencyInfo*, void> vkCmdPipelineBarrier2;

        // debug marker ext
        internal readonly unsafe delegate* unmanaged<VkDevice, VkDebugMarkerObjectNameInfoEXT*, VkResult> vkDebugMarkerSetObjectNameEXT;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, VkDebugMarkerMarkerInfoEXT*, void> vkCmdDebugMarkerBeginEXT;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, void> vkCmdDebugMarkerEndEXT;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, VkDebugMarkerMarkerInfoEXT*, void> vkCmdDebugMarkerInsertEXT;
        // dedicated allocation and memreq2
        internal readonly unsafe delegate* unmanaged<VkDevice, VkBufferMemoryRequirementsInfo2*, VkMemoryRequirements2*, void> vkGetBufferMemoryRequirements2;
        internal readonly unsafe delegate* unmanaged<VkDevice, VkImageMemoryRequirementsInfo2*, VkMemoryRequirements2*, void> vkGetImageMemoryRequirements2;

        public string? DriverName { get; }
        public string? DriverInfo { get; }

        private unsafe VulkanGraphicsDevice(ref DeviceCreateState deviceCreateState)
        {
            try
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
                VendorName = $"id:{_deviceCreateState.PhysicalDeviceProperties.vendorID:x8}";

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

                // for sanity, right now we hard-require sync2
                vkQueueSubmit2 =
                    (delegate* unmanaged<VkQueue, uint, VkSubmitInfo2*, VkFence, VkResult>)
                    GetDeviceProcAddr("vkQueueSubmit2"u8, "vkQueueSubmit2KHR"u8);
                vkCmdPipelineBarrier2 =
                    (delegate* unmanaged<VkCommandBuffer, VkDependencyInfo*, void>)
                    GetDeviceProcAddr("vkCmdPipelineBarrier2"u8, "vkCmdPipelineBarrier2KHR"u8);

                // Create other bits and pieces
                _memoryManager = new(
                    _deviceCreateState.Device,
                    _deviceCreateState.PhysicalDevice,
                    _deviceCreateState.PhysicalDeviceProperties.limits.bufferImageGranularity,
                    chunkGranularity: 1024);

                Features = new(
                    computeShader: _deviceCreateState.QueueFamilyInfo.MainComputeFamilyIdx >= 0,
                    geometryShader: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.geometryShader,
                    tessellationShaders: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.tessellationShader,
                    multipleViewports: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.multiViewport,
                    samplerLodBias: true,
                    drawBaseVertex: true,
                    drawBaseInstance: true,
                    drawIndirect: true,
                    drawIndirectBaseInstance: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.drawIndirectFirstInstance,
                    fillModeWireframe: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.fillModeNonSolid,
                    samplerAnisotropy: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.samplerAnisotropy,
                    depthClipDisable: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.depthClamp,
                    texture1D: true,
                    independentBlend: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.independentBlend,
                    structuredBuffer: true,
                    subsetTextureView: true,
                    commandListDebugMarkers: _deviceCreateState.HasDebugMarkerExt,
                    bufferRangeBinding: true,
                    shaderFloat64: (VkBool32)_deviceCreateState.PhysicalDeviceFeatures.shaderFloat64);

                ResourceFactory = new VulkanResourceFactory(this);
                _descriptorPoolManager = new(this);

                // TODO: MainSwapchain

                EagerlyAllocateSomeResources();
                PostDeviceCreated();
            }
            catch
            {
                // eagerly dispose if we threw here
                DisposeDirectOwned();
                throw;
            }
        }

        private bool _disposed;

        protected override unsafe void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            DisposeDirectOwned();
        }

        private unsafe void DisposeDirectOwned()
        {
            if (_disposed) return;
            _disposed = true;

            // TODO: destroy all other associated information

            var dcs = _deviceCreateState;

            while (_sharedCommandLists.TryTake(out var cl))
            {
                cl.Dispose();
            }

            while (_availableSemaphores.TryTake(out var semaphore))
            {
                vkDestroySemaphore(dcs.Device, semaphore, null);
            }

            while (_availableSubmissionFences.TryTake(out var fence))
            {
                vkDestroyFence(dcs.Device, fence, null);
            }

            if (_descriptorPoolManager is { } poolManager)
            {
                poolManager.DestroyAll();
            }

            if (_memoryManager is { } memoryManager)
            {
                memoryManager.Dispose();
            }

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

        private unsafe void EagerlyAllocateSomeResources()
        {
            // eagerly allocate a few semaphores and submission fences
            // semaphores are used for synchronization between command lists
            // (we use them particularly for the memory sync, as well as being able to associate multiple fences with a submit)

            var semaphoreCreateInfo = new VkSemaphoreCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
            };

            var fenceCreateInfo = new VkFenceCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO,
            };

            for (var i = 0; i < 4; i++)
            {
                VkSemaphore semaphore;
                VulkanUtil.CheckResult(vkCreateSemaphore(Device, &semaphoreCreateInfo, null, &semaphore));
                _availableSemaphores.Add(semaphore);

                VkFence fence;
                VulkanUtil.CheckResult(vkCreateFence(Device, &fenceCreateInfo, null, &fence));
                _availableSubmissionFences.Add(fence);
            }
        }

        public VkDevice Device => _deviceCreateState.Device;

        public unsafe bool HasSetMarkerName => vkDebugMarkerSetObjectNameEXT is not null;

        [SkipLocalsInit]
        internal unsafe void SetDebugMarkerName(VkDebugReportObjectTypeEXT type, ulong target, ReadOnlySpan<char> name)
        {
            if (vkDebugMarkerSetObjectNameEXT is null) return;

            Span<byte> utf8Buffer = stackalloc byte[1024];
            Util.GetNullTerminatedUtf8(name, ref utf8Buffer);
            SetDebugMarkerName(type, target, utf8Buffer);
        }

        internal unsafe void SetDebugMarkerName(VkDebugReportObjectTypeEXT type, ulong target, ReadOnlySpan<byte> nameUtf8)
        {
            if (vkDebugMarkerSetObjectNameEXT is null) return;

            fixed (byte* utf8Ptr = nameUtf8)
            {
                VkDebugMarkerObjectNameInfoEXT nameInfo = new()
                {
                    sType = VK_STRUCTURE_TYPE_DEBUG_MARKER_OBJECT_NAME_INFO_EXT,
                    objectType = type,
                    @object = target,
                    pObjectName = (sbyte*)utf8Ptr
                };

                VulkanUtil.CheckResult(vkDebugMarkerSetObjectNameEXT(Device, &nameInfo));
            }
        }

        internal unsafe VkCommandPool CreateCommandPool(bool transient)
        {
            var commandPoolCreateInfo = new VkCommandPoolCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
                flags = VkCommandPoolCreateFlags.VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
                queueFamilyIndex = (uint)_deviceCreateState.QueueFamilyInfo.MainGraphicsFamilyIdx,
            };

            if (transient)
            {
                commandPoolCreateInfo.flags |= VkCommandPoolCreateFlags.VK_COMMAND_POOL_CREATE_TRANSIENT_BIT;
            }

            VkCommandPool commandPool;
            VulkanUtil.CheckResult(vkCreateCommandPool(_deviceCreateState.Device, &commandPoolCreateInfo, null, &commandPool));
            return commandPool;
        }

        internal unsafe VkSemaphore GetSemaphore()
        {
            if (_availableSemaphores.TryTake(out var semaphore))
            {
                return semaphore;
            }

            var semCreateInfo = new VkSemaphoreCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
            };

            VulkanUtil.CheckResult(vkCreateSemaphore(Device, &semCreateInfo, null, &semaphore));
            return semaphore;
        }

        internal void ReturnSemaphore(VkSemaphore semaphore)
        {
            _availableSemaphores.Add(semaphore);
        }

        internal unsafe VkFence GetSubmissionFence(bool reset = true)
        {
            if (_availableSubmissionFences.TryTake(out var fence))
            {
                if (reset)
                {
                    VulkanUtil.CheckResult(vkResetFences(Device, 1, &fence));
                }
                return fence;
            }

            var fenceCreateInfo = new VkFenceCreateInfo()
            {
                sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO,
            };

            VulkanUtil.CheckResult(vkCreateFence(Device, &fenceCreateInfo, null, &fence));
            return fence;
        }

        internal void ReturnSubmissionFence(VkFence fence)
        {
            _availableSubmissionFences.Add(fence);
        }

        private struct FenceCompletionCallbackInfo
        {
            public VkFence Fence;
            public VulkanCommandList.FenceCompletionCallbackInfo CallbackInfo;
        }

        internal void RegisterFenceCompletionCallback(VkFence fence, in VulkanCommandList.FenceCompletionCallbackInfo callbackInfo)
        {
            lock (_fenceCompletionCallbackLock)
            {
                _fenceCompletionCallbacks.Add(new()
                {
                    Fence = fence,
                    CallbackInfo = callbackInfo,
                });
            }
        }

        private void CheckFencesForCompletion()
        {
            lock (_fenceCompletionCallbackLock)
            {
                var list = _fenceCompletionCallbacks;
                for (int i = 0; i < list.Count; i++)
                {
                    ref var callback = ref CollectionsMarshal.AsSpan(list)[i];
                    var result = vkGetFenceStatus(_deviceCreateState.Device, callback.Fence);
                    if (result == VkResult.VK_SUCCESS)
                    {
                        // the fence is complete, invoke the callback
                        callback.CallbackInfo.CommandList.OnSubmissionFenceCompleted(callback.Fence, in callback.CallbackInfo, errored: false);
                    }
                    else if (result is not VkResult.VK_NOT_READY)
                    {
                        // some error condition, also invoke the callback to give it a chance to clean up, but tell it that this is an error condition
                        callback.CallbackInfo.CommandList.OnSubmissionFenceCompleted(callback.Fence, in callback.CallbackInfo, errored: true);
                    }
                    else // result is VkResult.VK_NOT_READY
                    {
                        Debug.Assert(result is VkResult.VK_NOT_READY);
                        // not ready, keep it in the list
                        continue;
                    }

                    // NOTE: `callback` is invalidated once the list is modified. Do not read after this point.
                    list.RemoveAt(i);
                    i -= 1;
                }
            }
        }

        private protected override void SubmitCommandsCore(CommandList commandList, Fence? fence)
        {
            var cl = Util.AssertSubtype<CommandList, VulkanCommandList>(commandList);
            var vkFence = Util.AssertSubtypeOrNull<Fence, VulkanFence>(fence);

            lock (QueueLock)
            {
                cl.SubmitToQueue(_deviceCreateState.MainQueue, vkFence, null);
            }
        }

        internal VulkanCommandList GetAndBeginCommandList()
        {
            if (!_sharedCommandLists.TryTake(out var sharedList))
            {
                var desc = new CommandListDescription() { Transient = true };
                sharedList = (VulkanCommandList)ResourceFactory.CreateCommandList(desc);
                sharedList.Name = "GraphicsDevice Shared CommandList";
            }

            sharedList.Begin();
            return sharedList;
        }

        private static readonly Action<VulkanCommandList> s_returnClToPool = static cl =>
        {
            var device = cl.Device;

            if (device._sharedCommandLists.Count < MaxSharedCommandLists)
            {
                device._sharedCommandLists.Add(cl);
            }
            else
            {
                cl.Dispose();
            }
        };

        internal void EndAndSubmitCommands(VulkanCommandList cl)
        {
            cl.End();

            lock (QueueLock)
            {
                cl.SubmitToQueue(_deviceCreateState.MainQueue, null, s_returnClToPool);
            }
        }

        internal VulkanBuffer GetPooledStagingBuffer(uint size)
        {
            lock (_availableStagingBuffers)
            {
                for (int i = 0; i < _availableStagingBuffers.Count; i++)
                {
                    var buffer = _availableStagingBuffers[i];
                    if (buffer.SizeInBytes >= size)
                    {
                        _availableStagingBuffers.RemoveAt(i);
                        return buffer;
                    }
                }
            }

            uint newBufferSize = Math.Max(MinStagingBufferSize, size);
            var buf =  (VulkanBuffer)ResourceFactory.CreateBuffer(
                new BufferDescription(newBufferSize, BufferUsage.StagingWrite));
            buf.Name = "Staging Buffer (GraphicsDevice)";
            return buf;
        }

        internal void ReturnPooledStagingBuffers(ReadOnlySpan<VulkanBuffer> buffers)
        {
            lock (_availableStagingBuffers)
            {
                foreach (var buf in buffers)
                {
                    _availableStagingBuffers.Add(buf);
                }
            }
        }

        private protected override void WaitForIdleCore()
        {
            lock (QueueLock)
            {
                vkQueueWaitIdle(_deviceCreateState.MainQueue);
            }

            // when the queue has gone idle, all of our fences *should* be signalled.
            // Make sure we clean up their associated information.
            CheckFencesForCompletion();
        }

        public override unsafe void ResetFence(Fence fence)
        {
            var vkFence = Util.AssertSubtype<Fence, VulkanFence>(fence);
            var devFence = vkFence.DeviceFence;
            VulkanUtil.CheckResult(vkResetFences(Device, 1, &devFence));
        }

        public override unsafe bool WaitForFence(Fence fence, ulong nanosecondTimeout)
        {
            var vkFence = Util.AssertSubtype<Fence, VulkanFence>(fence);
            var devFence = vkFence.DeviceFence;

            var result = vkWaitForFences(Device, 1, &devFence, VK_TRUE, nanosecondTimeout) == VkResult.VK_SUCCESS;
            // if we're waiting for fences, they're probably submission fences
            CheckFencesForCompletion();

            return result;
        }

        public override unsafe bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            VkFence[]? arr = null;
            Span<VkFence> vkFences = fences.Length > 16
                ? (arr = ArrayPool<VkFence>.Shared.Rent(fences.Length))
                : stackalloc VkFence[16];

            for (var i = 0; i < fences.Length; i++)
            {
                vkFences[i] = Util.AssertSubtype<Fence, VulkanFence>(fences[i]).DeviceFence;
            }

            bool result;
            fixed (VkFence* pFences = vkFences)
            {
                result = vkWaitForFences(Device, (uint)fences.Length, pFences, waitAll ? VK_TRUE : VK_FALSE, nanosecondTimeout) == VkResult.VK_SUCCESS;
            }

            if (arr is not null)
            {
                ArrayPool<VkFence>.Shared.Return(arr);
            }

            // if we're waiting for fences, they're probably submission fences
            CheckFencesForCompletion();

            return result;
        }

        public unsafe override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
        {
            VkImageUsageFlags usageFlags = VkImageUsageFlags.VK_IMAGE_USAGE_SAMPLED_BIT;
            usageFlags |= depthFormat
                ? VkImageUsageFlags.VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT
                : VkImageUsageFlags.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;

            VkImageFormatProperties formatProperties;
            vkGetPhysicalDeviceImageFormatProperties(
                _deviceCreateState.PhysicalDevice,
                VkFormats.VdToVkPixelFormat(format, depthFormat ? TextureUsage.DepthStencil : default),
                VkImageType.VK_IMAGE_TYPE_2D,
                VkImageTiling.VK_IMAGE_TILING_OPTIMAL,
                usageFlags,
                0,
                &formatProperties);

            VkSampleCountFlags vkSampleCounts = formatProperties.sampleCounts;
            if ((vkSampleCounts & VkSampleCountFlags.VK_SAMPLE_COUNT_64_BIT) == VkSampleCountFlags.VK_SAMPLE_COUNT_64_BIT)
            {
                return TextureSampleCount.Count64;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.VK_SAMPLE_COUNT_32_BIT) == VkSampleCountFlags.VK_SAMPLE_COUNT_32_BIT)
            {
                return TextureSampleCount.Count32;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.VK_SAMPLE_COUNT_16_BIT) == VkSampleCountFlags.VK_SAMPLE_COUNT_16_BIT)
            {
                return TextureSampleCount.Count16;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.VK_SAMPLE_COUNT_8_BIT) == VkSampleCountFlags.VK_SAMPLE_COUNT_8_BIT)
            {
                return TextureSampleCount.Count8;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.VK_SAMPLE_COUNT_4_BIT) == VkSampleCountFlags.VK_SAMPLE_COUNT_4_BIT)
            {
                return TextureSampleCount.Count4;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.VK_SAMPLE_COUNT_2_BIT) == VkSampleCountFlags.VK_SAMPLE_COUNT_2_BIT)
            {
                return TextureSampleCount.Count2;
            }
            return TextureSampleCount.Count1;
        }

        private protected unsafe override bool GetPixelFormatSupportCore(PixelFormat format, TextureType type, TextureUsage usage, out PixelFormatProperties properties)
        {
            VkFormat vkFormat = VkFormats.VdToVkPixelFormat(format, usage);
            VkImageType vkType = VkFormats.VdToVkTextureType(type);
            VkImageTiling tiling = usage == TextureUsage.Staging
                ? VkImageTiling.VK_IMAGE_TILING_LINEAR
                : VkImageTiling.VK_IMAGE_TILING_OPTIMAL;
            VkImageUsageFlags vkUsage = VkFormats.VdToVkTextureUsage(usage);

            VkImageFormatProperties vkProps;
            VkResult result = vkGetPhysicalDeviceImageFormatProperties(
                _deviceCreateState.PhysicalDevice,
                vkFormat,
                vkType,
                tiling,
                vkUsage,
                0,
                &vkProps);

            if (result == VkResult.VK_ERROR_FORMAT_NOT_SUPPORTED)
            {
                properties = default;
                return false;
            }
            VulkanUtil.CheckResult(result);

            properties = new PixelFormatProperties(
               vkProps.maxExtent.width,
               vkProps.maxExtent.height,
               vkProps.maxExtent.depth,
               vkProps.maxMipLevels,
               vkProps.maxArrayLayers,
               (uint)vkProps.sampleCounts);
            return true;
        }

        private protected unsafe override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, nint source, uint sizeInBytes)
        {
            var vkBuffer = Util.AssertSubtype<DeviceBuffer, VulkanBuffer>(buffer);
            VulkanBuffer? copySrcBuffer = null;

            byte* mappedPtr;
            byte* destPtr;
            if (vkBuffer.Memory.IsPersistentMapped)
            {
                mappedPtr = (byte*)vkBuffer.Memory.BlockMappedPointer;
                destPtr = mappedPtr + bufferOffsetInBytes;
            }
            else
            {
                copySrcBuffer = GetPooledStagingBuffer(sizeInBytes);
                mappedPtr = (byte*)copySrcBuffer.Memory.BlockMappedPointer;
                destPtr = mappedPtr;
            }

            Unsafe.CopyBlock(destPtr, (void*)source, sizeInBytes);

            if (copySrcBuffer is not null)
            {
                var cl = GetAndBeginCommandList();
                cl.AddStagingResource(copySrcBuffer);
                cl.CopyBuffer(copySrcBuffer, 0, vkBuffer, bufferOffsetInBytes, sizeInBytes);
                EndAndSubmitCommands(cl);
            }
        }

        private protected unsafe override MappedResource MapCore(MappableResource resource, uint bufferOffsetInBytes, uint sizeInBytes, MapMode mode, uint subresource)
        {
            VkMemoryBlock memoryBlock;
            void* mappedPtr = null;
            var rowPitch = 0u;
            var depthPitch = 0u;

            if (resource is VulkanBuffer buffer)
            {
                memoryBlock = buffer.Memory;
            }
            else
            {
                throw new NotImplementedException();
            }

            if (memoryBlock.DeviceMemory != VkDeviceMemory.NULL)
            {
                var atomSize = _deviceCreateState.PhysicalDeviceProperties.limits.nonCoherentAtomSize;
                var mapOffset = memoryBlock.Offset + bufferOffsetInBytes;
                var bindOffset = mapOffset / atomSize * atomSize;
                var bindSize = (sizeInBytes + atomSize - 1) / atomSize * atomSize;

                if (memoryBlock.IsPersistentMapped)
                {
                    mappedPtr = (byte*)memoryBlock.BaseMappedPointer + mapOffset;
                }
                else
                {
                    var result = vkMapMemory(Device, memoryBlock.DeviceMemory, bindOffset, bindSize, 0, &mappedPtr);
                    if (result is not VkResult.VK_ERROR_MEMORY_MAP_FAILED)
                    {
                        VulkanUtil.CheckResult(result);
                    }
                    else
                    {
                        ThrowMapFailedException(resource, subresource);
                    }

                    mappedPtr = (byte*)mappedPtr + (mapOffset - bindOffset);
                }
            }

            return new MappedResource(resource, mode, (nint)mappedPtr, bufferOffsetInBytes, sizeInBytes, subresource, rowPitch, depthPitch);
        }

        private protected override void UnmapCore(MappableResource resource, uint subresource)
        {
            VkMemoryBlock memoryBlock;
            if (resource is VulkanBuffer buffer)
            {
                memoryBlock = buffer.Memory;
            }
            else
            {
                throw new NotImplementedException();
            }

            if (memoryBlock.DeviceMemory != VkDeviceMemory.NULL && !memoryBlock.IsPersistentMapped)
            {
                vkUnmapMemory(Device, memoryBlock.DeviceMemory);
            }
        }

        private protected override void UpdateTextureCore(Texture texture, nint source, uint sizeInBytes, uint x, uint y, uint z, uint width, uint height, uint depth, uint mipLevel, uint arrayLayer)
        {
            throw new NotImplementedException();
        }

        private protected override void SwapBuffersCore(Swapchain swapchain)
        {
            throw new NotImplementedException();
        }
    }
}