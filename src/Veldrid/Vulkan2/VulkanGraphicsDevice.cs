using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using VkFormats = Veldrid.Vulkan.VkFormats;
using VkMemoryBlock = Veldrid.Vulkan.VkMemoryBlock;
using FixedUtf8String = Veldrid.Vulkan.FixedUtf8String;
using VkDeviceMemoryManager = Veldrid.Vulkan.VkDeviceMemoryManager;
using static TerraFX.Interop.Vulkan.VkStructureType;
using static TerraFX.Interop.Vulkan.Vulkan;
using Vortice.Direct3D11;

namespace Veldrid.Vulkan2
{
    internal sealed partial class VulkanGraphicsDevice : GraphicsDevice
    {
        internal readonly DeviceCreateState _deviceCreateState;
        internal readonly List<FixedUtf8String> _surfaceExtensions;

        private readonly VkDeviceMemoryManager _memoryManager;
        private readonly VulkanDescriptorPoolManager _descriptorPoolManager;
        private readonly ConcurrentDictionary<VkFormat, VkFilter> _filters = new();
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
        private readonly List<VulkanTexture> _availableStagingTextures = new();

        private readonly Dictionary<MappableResource, ResourceMapping> _mappedResources = new();
        private readonly object _mappedResourcesLock = new();

#if DEBUG
        internal readonly ConcurrentDictionary<VkImage, WeakReference<VulkanTexture>> NativeToManagedImages = new();
#endif

        // optional functions

        // synchronization2
        internal readonly unsafe delegate* unmanaged<VkQueue, uint, VkSubmitInfo2*, VkFence, VkResult> vkQueueSubmit2;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, VkDependencyInfo*, void> vkCmdPipelineBarrier2;

        // dynamic_rendering
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, VkRenderingInfo*, void> vkCmdBeginRendering;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, void> vkCmdEndRendering;

        // dedicated allocation and memreq2
        internal readonly unsafe delegate* unmanaged<VkDevice, VkBufferMemoryRequirementsInfo2*, VkMemoryRequirements2*, void> vkGetBufferMemoryRequirements2;
        internal readonly unsafe delegate* unmanaged<VkDevice, VkImageMemoryRequirementsInfo2*, VkMemoryRequirements2*, void> vkGetImageMemoryRequirements2;

        // debug marker ext
        internal readonly unsafe delegate* unmanaged<VkDevice, VkDebugMarkerObjectNameInfoEXT*, VkResult> vkDebugMarkerSetObjectNameEXT;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, VkDebugMarkerMarkerInfoEXT*, void> vkCmdDebugMarkerBeginEXT;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, void> vkCmdDebugMarkerEndEXT;
        internal readonly unsafe delegate* unmanaged<VkCommandBuffer, VkDebugMarkerMarkerInfoEXT*, void> vkCmdDebugMarkerInsertEXT;

        public VkDevice Device => _deviceCreateState.Device;
        public unsafe bool HasSetMarkerName => vkDebugMarkerSetObjectNameEXT is not null;
        public new VulkanResourceFactory ResourceFactory => (VulkanResourceFactory)base.ResourceFactory;
        public VulkanDescriptorPoolManager DescriptorPoolManager => _descriptorPoolManager;

        public string? DriverName { get; }
        public string? DriverInfo { get; }

        private unsafe VulkanGraphicsDevice(ref DeviceCreateState deviceCreateState, SwapchainDescription? swapchainDesc, List<FixedUtf8String> surfaceExtensions)
        {
            try
            {
                // once we adopt the DCS, default-out the source because the caller will try to free the handles (which we now own)
                _deviceCreateState = deviceCreateState;
                deviceCreateState = default;
                _surfaceExtensions = surfaceExtensions;

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

                if (_deviceCreateState.HasDynamicRendering)
                {
                    vkCmdBeginRendering =
                        (delegate* unmanaged<VkCommandBuffer, VkRenderingInfo*, void>)
                        GetDeviceProcAddr("vkCmdBeginRendering"u8, "vkCmdBeginRenderingKHR"u8);
                    vkCmdEndRendering =
                        (delegate* unmanaged<VkCommandBuffer, void>)
                        GetDeviceProcAddr("vkCmdEndRendering"u8, "vkCmdEndRenderingKHR"u8);
                }

                if (_deviceCreateState.HasSync2Ext)
                {
                    vkQueueSubmit2 =
                        (delegate* unmanaged<VkQueue, uint, VkSubmitInfo2*, VkFence, VkResult>)
                        GetDeviceProcAddr("vkQueueSubmit2"u8, "vkQueueSubmit2KHR"u8);
                    vkCmdPipelineBarrier2 =
                        (delegate* unmanaged<VkCommandBuffer, VkDependencyInfo*, void>)
                        GetDeviceProcAddr("vkCmdPipelineBarrier2"u8, "vkCmdPipelineBarrier2KHR"u8);
                }

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

                base.ResourceFactory = new VulkanResourceFactory(this);
                _descriptorPoolManager = new(this);

                // TODO: MainSwapchain
                if (swapchainDesc is { } desc)
                {
                    Debug.Assert(_deviceCreateState.Surface != VkSurfaceKHR.NULL);

                    // note: the main swapchain takes ownership of the created surface
                    MainSwapchain = new VulkanSwapchain(this, desc, ref Unsafe.AsRef(ref _deviceCreateState.Surface), _deviceCreateState.QueueFamilyInfo.PresentFamilyIdx);
                }

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

            // if we have any unbalanced mappings, clean those up first
            lock (_mappedResourcesLock)
            {
                foreach (var (resource, mapInfo) in _mappedResources)
                {
                    mapInfo.RefCount.DecrementDispose();
                    // force-deallocate
                    while (!mapInfo.RefCount.IsClosed)
                        mapInfo.RefCount.Decrement();
                }
            }

            // TODO: destroy all other associated information
            MainSwapchain?.Dispose();

            var dcs = _deviceCreateState;

            lock (_availableStagingBuffers)
            {
                foreach (var buf in _availableStagingBuffers)
                {
                    buf.Dispose();
                }
            }

            lock (_availableStagingTextures)
            {
                foreach (var tex in _availableStagingTextures)
                {
                    tex.Dispose();
                }
            }

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

        internal unsafe void SetDebugMarkerName(VkDebugReportObjectTypeEXT type, ulong target, ReadOnlySpan<char> name)
        {
            if (vkDebugMarkerSetObjectNameEXT is null) return;
            DoSet(this, type, target, name);

            [SkipLocalsInit]
            static void DoSet(VulkanGraphicsDevice @this, VkDebugReportObjectTypeEXT type, ulong target, ReadOnlySpan<char> name)
            {
                Span<byte> utf8Buffer = stackalloc byte[128];
                Util.GetNullTerminatedUtf8(name, ref utf8Buffer);
                @this.SetDebugMarkerName(type, target, utf8Buffer);
            }
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
                _ = cl.SubmitToQueue(_deviceCreateState.MainQueue, vkFence, null, 0);
            }

            // also take the opportunity to check for fence completions
            CheckFencesForCompletion();
        }

        internal VulkanCommandList GetAndBeginCommandList()
        {
            if (!_sharedCommandLists.TryTake(out var sharedList))
            {
                var desc = new CommandListDescription() { Transient = true };
                sharedList = ResourceFactory.CreateCommandList(desc);
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

        internal (VkSemaphore Sem, VkFence Fence) EndAndSubmitCommands(VulkanCommandList cl, VkPipelineStageFlags2 semaphoreStages = 0)
        {
            cl.End();
            CheckFencesForCompletion();

            lock (QueueLock)
            {
                return cl.SubmitToQueue(_deviceCreateState.MainQueue, null, s_returnClToPool, semaphoreStages);
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
                        // note: don't reset sync state, as it is REQUIRED that we sync against it for writes
                        return buffer;
                    }
                }
            }

            uint newBufferSize = Math.Max(MinStagingBufferSize, size);
            var buf =  ResourceFactory.CreateBuffer(
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

        internal VulkanTexture GetPooledStagingTexture(uint width, uint height, uint depth, PixelFormat format)
        {
            var totalSize = FormatHelpers.GetRegionSize(width, height, depth, format);
            lock (_availableStagingTextures)
            {
                for (int i = 0; i < _availableStagingTextures.Count; i++)
                {
                    var tex = _availableStagingTextures[i];
                    if (tex.Memory.Size >= totalSize)
                    {
                        _availableStagingTextures.RemoveAt(i);
                        tex.SetStagingDimensions(width, height, depth, format);
                        // note: we CANNOT reset sync state, because writes must be correctly sync'd against readers
                        return tex;
                    }
                }
            }

            var texWidth = uint.Max(256, width);
            var texHeight = uint.Max(256, height);
            var newTex = ResourceFactory.CreateTexture(
                TextureDescription.Texture3D(texWidth, texHeight, depth, 1, format, TextureUsage.Staging));
            newTex.SetStagingDimensions(width, height, depth, format);
            newTex.Name = "Staging Texture (GraphicsDevice)";
            return newTex;
        }

        internal void ReturnPooledStagingTextures(ReadOnlySpan<VulkanTexture> textures)
        {
            lock (_availableStagingTextures)
            {
                foreach (var tex in textures)
                {
                    _availableStagingTextures.Add(tex);
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

        internal unsafe VkFilter GetFormatFilter(VkFormat format)
        {
            if (!_filters.TryGetValue(format, out VkFilter filter))
            {
                VkFormatProperties vkFormatProps;
                vkGetPhysicalDeviceFormatProperties(_deviceCreateState.PhysicalDevice, format, &vkFormatProps);
                filter = (vkFormatProps.optimalTilingFeatures & VkFormatFeatureFlags.VK_FORMAT_FEATURE_SAMPLED_IMAGE_FILTER_LINEAR_BIT) != 0
                    ? VkFilter.VK_FILTER_LINEAR
                    : VkFilter.VK_FILTER_NEAREST;
                _filters.TryAdd(format, filter);
            }

            return filter;
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
                // note: we DON'T need an explicit flush here, because queue submission does so implicitly

                // QueueLock is how we sync global sync state
                lock (QueueLock)
                {
                    // the buffer WAS written to though, make sure we note that
                    copySrcBuffer.AllSyncStates.Fill(new()
                    {
                        LastWriter = new()
                        {
                            AccessMask = VkAccessFlags.VK_ACCESS_HOST_WRITE_BIT,
                            StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_HOST_BIT,
                        },
                        PerStageReaders = 0,
                    });
                }

                var cl = GetAndBeginCommandList();
                cl.AddStagingResource(copySrcBuffer);
                // then CopyBuffer will handle synchro in the CommandList itself
                cl.CopyBuffer(copySrcBuffer, 0, vkBuffer, bufferOffsetInBytes, sizeInBytes);
                EndAndSubmitCommands(cl);
            }
            else
            {
                // not a staging buffer, we need to explicitly flush

                // note: we don't need to flush because the memory is "coherent"

                // QueueLock is how we sync global sync state
                lock (QueueLock)
                {
                    vkBuffer.AllSyncStates.Fill(new()
                    {
                        LastWriter = new()
                        {
                            AccessMask = VkAccessFlags.VK_ACCESS_HOST_WRITE_BIT,
                            StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_HOST_BIT,
                        },
                        PerStageReaders = 0,
                    });
                }
            }
        }

        private protected unsafe override void UpdateTextureCore(Texture texture,
            nint source, uint sizeInBytes,
            uint x, uint y, uint z,
            uint width, uint height, uint depth,
            uint mipLevel, uint arrayLayer)
        {
            var tex = Util.AssertSubtype<Texture, VulkanTexture>(texture);

            if ((tex.Usage & TextureUsage.Staging) != 0)
            {
                // staging buffer, persistent-mapped VkBuffer, not an image
                UpdateStagingTexture(tex, source, x, y, z, width, height, depth, mipLevel, arrayLayer);
            }
            else
            {
                // not staging, backed by an actual VkImage, meaning we need to use a staging texture
                var stagingTex = GetPooledStagingTexture(width, height, depth, tex.Format);
                // use the helper directly to avoid an unnecessary synchronization op to device memory
                UpdateStagingTexture(stagingTex, source, 0, 0, 0, width, height, depth, 0, 0);
                // a queue submit implicitly synchronizes host->device, which normally requires the flush

                var cl = GetAndBeginCommandList();
                cl.AddStagingResource(stagingTex);
                cl.CopyTexture(
                    stagingTex, 0, 0, 0, 0, 0,
                    tex, x, y, z, mipLevel, arrayLayer,
                    width, height, depth, 1);
                EndAndSubmitCommands(cl);
            }
        }

        private unsafe void UpdateStagingTexture(
            VulkanTexture tex,
            nint source, uint x, uint y, uint z,
            uint width, uint height, uint depth,
            uint mipLevel, uint arrayLayer)
        {
            var layout = tex.GetSubresourceLayout(mipLevel, arrayLayer);
            var basePtr = (byte*)tex.Memory.BlockMappedPointer + layout.offset;

            var rowPitch = FormatHelpers.GetRowPitch(width, tex.Format);
            var depthPitch = FormatHelpers.GetDepthPitch(rowPitch, height, tex.Format);
            Util.CopyTextureRegion(
                (void*)source,
                0, 0, 0,
                rowPitch, depthPitch,
                basePtr,
                x, y, z,
                (uint)layout.rowPitch, (uint)layout.depthPitch,
                width, height, depth,
                tex.Format);

            // QueueLock synchronizes access to global sync state
            lock (QueueLock)
            {
                tex.AllSyncStates.Fill(new()
                {
                    LastWriter = new()
                    {
                        AccessMask = VkAccessFlags.VK_ACCESS_HOST_WRITE_BIT,
                        StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_HOST_BIT,
                    },
                    PerStageReaders = 0,
                    // note: staging textures don't track layout
                });
            }
        }


        // TODO: currently, mapping a resource maps ALL subresources, even though the Veldrid API only asks for 1
        private protected unsafe override MappedResource MapCore(MappableResource resource, uint bufferOffsetInBytes, uint sizeInBytes, MapMode mode, uint subresource)
        {
            VkMemoryBlock memoryBlock;
            ISynchronizedResource syncResource;
            void* mappedPtr = null;
            var rowPitch = 0u;
            var depthPitch = 0u;
            var syncSubresource = new SyncSubresourceRange(0, 0, 1, 1);

            if (resource is VulkanBuffer buffer)
            {
                syncResource = buffer;
                memoryBlock = buffer.Memory;
            }
            else
            {
                var tex = Util.AssertSubtype<MappableResource, VulkanTexture>(resource);
                syncResource = tex;
                Util.GetMipLevelAndArrayLayer(tex, subresource, out var mipLevel, out var arrayLayer);
                syncSubresource = new(arrayLayer, mipLevel, 1, 1);

                var layout = tex.GetSubresourceLayout(mipLevel, arrayLayer);
                memoryBlock = tex.Memory;
                bufferOffsetInBytes += (uint)layout.offset;
                rowPitch = (uint)layout.rowPitch;
                depthPitch = (uint)layout.depthPitch;
            }

            if (memoryBlock.DeviceMemory != VkDeviceMemory.NULL)
            {
                var mapOffset = memoryBlock.Offset + bufferOffsetInBytes;

                if ((mode & MapMode.Read) != 0)
                {
                    var barrierMasks = new SyncBarrierMasks()
                    {
                        StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_HOST_BIT,
                        AccessMask = VkAccessFlags.VK_ACCESS_HOST_READ_BIT,
                    };
                    var syncRequest = new SyncRequest()
                    {
                        BarrierMasks = barrierMasks,
                        // note: host reads must be done in PREINITIALIZED or GENERAL.
                        // Because we don't have a good way to know which we're in here, we always transition
                        // to GENERAL for a map operation.
                        Layout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL
                    };

                    var needSyncOrLayoutTransition = false;
                    lock (QueueLock)
                    {
                        ref var syncInfo = ref syncResource.SyncStateForSubresource(new(syncSubresource.BaseLayer, syncSubresource.BaseMip));
                        var syncInfoCopy = syncInfo;
                        needSyncOrLayoutTransition = VulkanCommandList.TryBuildSyncBarrier(ref syncInfoCopy, syncRequest, transitionFromUnknown: true, out _);
                        if (!needSyncOrLayoutTransition)
                        {
                            // we don't need to do an explicit sync, actually update the sync info
                            syncInfo = syncInfoCopy;
                        }
                    }

                    if (needSyncOrLayoutTransition)
                    {
                        // a read mode was requested, we need to sync-to-host to make sure the memory is visible
                        var cl = GetAndBeginCommandList();
                        cl.SyncResourceDyn(syncResource, syncSubresource, syncRequest);
                        var (_, fence) = EndAndSubmitCommands(cl);
                        // now we need to wait for our fence so we know that the sync has gone through
                        VulkanUtil.CheckResult(vkWaitForFences(_deviceCreateState.Device, 1, &fence, 1, ulong.MaxValue));
                        // since we just waited on a fence, lets process pending fences and return stuff to pools
                        CheckFencesForCompletion();
                    }
                }

                ResourceMapping? mapping;
                lock (_mappedResourcesLock)
                {
                    if (_mappedResources.TryGetValue(resource, out mapping) && !mapping.RefCount.IsClosed)
                    {
                        // mapping already exists, update the mode and increment
                        mapping.RefCount.Increment();
                        mapping.UpdateMode(mode);
                    }
                    else
                    {
                        // need to create a new mapping
                        if (memoryBlock.IsPersistentMapped)
                        {
                            mappedPtr = (byte*)memoryBlock.BaseMappedPointer + mapOffset;
                        }
                        else
                        {
                            var atomSize = _deviceCreateState.PhysicalDeviceProperties.limits.nonCoherentAtomSize;
                            var bindOffset = mapOffset / atomSize * atomSize;
                            var bindSize = (sizeInBytes + atomSize - 1) / atomSize * atomSize;

                            // TODO: I'm pretty sure this is STILL wrong if mapping multiple subresources independently
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

                        // and an associated resource
                        mapping = new(this, syncResource, memoryBlock);
                        mapping.UpdateMode(mode);
                        _mappedResources[resource] = mapping;
                    }
                }

                // Note: InvalidateMappedMemoryRanges is only needed if memory is allocated without HOST_COHERENT
                // We never actually allocate with HOST_COHERENT, only HOST_CACHED, so we need to invalidate.
                var mappedRange = new VkMappedMemoryRange()
                {
                    sType = VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE,
                    memory = memoryBlock.DeviceMemory,
                    offset = memoryBlock.Offset,
                    size = memoryBlock.Size,
                };

                VulkanUtil.CheckResult(vkInvalidateMappedMemoryRanges(Device, 1, &mappedRange));
            }

            return new MappedResource(resource, mode, (nint)mappedPtr, bufferOffsetInBytes, sizeInBytes, subresource, rowPitch, depthPitch);
        }

        private sealed class ResourceMapping : Vulkan.IResourceRefCountTarget
        {
            public readonly VulkanGraphicsDevice Device;
            public readonly ISynchronizedResource Resource;
            public readonly VkMemoryBlock MemoryBlock;
            public Vulkan.ResourceRefCount RefCount { get; }

            private int _mode;
            public MapMode Mode => (MapMode)(byte)Volatile.Read(ref _mode);

            public ResourceMapping(VulkanGraphicsDevice device, ISynchronizedResource resource, VkMemoryBlock memoryBlock)
            {
                Device = device;
                Resource = resource;
                MemoryBlock = memoryBlock;
                RefCount = new(this);
                resource.RefCount.Increment();
            }

            public void UpdateMode(MapMode mode)
            {
                int oldMode, newMode;
                do
                {
                    oldMode = Volatile.Read(ref _mode);
                    newMode = oldMode | (byte)mode;
                }
                while (Interlocked.CompareExchange(ref _mode, newMode, oldMode) != oldMode);
            }

            // RefZeroed corresponds to us needing to unmap, but ONLY to unmap. Sync is always done independently, because it may need to happen multiple times.
            public void RefZeroed()
            {
                if (MemoryBlock.DeviceMemory != VkDeviceMemory.NULL && !MemoryBlock.IsPersistentMapped)
                {
                    vkUnmapMemory(Device.Device, MemoryBlock.DeviceMemory);
                }

                Resource.RefCount.Decrement();
            }
        }

        private protected unsafe override void UnmapCore(MappableResource resource, uint subresource)
        {
            VkMemoryBlock memoryBlock;
            ref SyncState syncState = ref Unsafe.NullRef<SyncState>();
            if (resource is VulkanBuffer buffer)
            {
                memoryBlock = buffer.Memory;
                syncState = ref buffer.AllSyncStates[0];
            }
            else
            {
                var tex = Util.AssertSubtype<MappableResource, VulkanTexture>(resource);
                memoryBlock = tex.Memory;
                Util.GetMipLevelAndArrayLayer(tex, subresource, out var mipLevel, out var arrayLayer);
                syncState = ref tex.SyncStateForSubresource(new(arrayLayer, mipLevel));
            }

            lock (_mappedResourcesLock)
            {
                if (_mappedResources.TryGetValue(resource, out var mapping) && !mapping.RefCount.IsClosed)
                {
                    if ((mapping.Mode & MapMode.Write) != 0)
                    {
                        // the mapping is mapped with write access, so we should flush it
                        var mappedRange = new VkMappedMemoryRange()
                        {
                            sType = VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE,
                            memory = memoryBlock.DeviceMemory,
                            offset = 0,
                            size = VK_WHOLE_SIZE,
                        };

                        VulkanUtil.CheckResult(vkFlushMappedMemoryRanges(Device, 1, &mappedRange));

                        // the queue lock is what we use to sync access to global sync state
                        lock (QueueLock)
                        {
                            syncState = new()
                            {
                                PerStageReaders = 0,
                                LastWriter = new()
                                {
                                    AccessMask = VkAccessFlags.VK_ACCESS_HOST_WRITE_BIT,
                                    StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_HOST_BIT,
                                }
                            };
                        }
                    }

                    // AFTER syncing, decrement to (possibly) unmap
                    mapping.RefCount.Decrement();
                    if (mapping.RefCount.IsClosed)
                    {
                        // if was the last ourstanding reference, remove the resource from this dict
                        _mappedResources.Remove(resource);
                    }
                }
            }
        }

        private protected unsafe override void SwapBuffersCore(Swapchain swapchain)
        {
            var vkSwapchain = Util.AssertSubtype<Swapchain, VulkanSwapchain>(swapchain);
            var deviceSwapchain = vkSwapchain.DeviceSwapchain;
            var imageIndex = vkSwapchain.ImageIndex;

            // transition all swapchain images into PRESENT_SRC layout
            var cl = GetAndBeginCommandList();
            foreach (ref var colorTarget in vkSwapchain.Framebuffer.CurrentFramebuffer.ColorTargetsArray.AsSpan())
            {
                var tex = Util.AssertSubtype<Texture, VulkanTexture>(colorTarget.Target);
                cl.SyncResource(tex, new(colorTarget.ArrayLayer, colorTarget.MipLevel, 1, 1), new()
                {
                    Layout = VkImageLayout.VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
                    BarrierMasks = new()
                    {
                        StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                        AccessMask = 0,
                    }
                });
            }
            // note: synchro affects things in submission order, so we don't need to semaphore-wait
            EndAndSubmitCommands(cl);

            var presentInfo = new VkPresentInfoKHR()
            {
                sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
                swapchainCount = 1,
                pSwapchains = &deviceSwapchain,
                pImageIndices = &imageIndex,
            };

            var presentLock = vkSwapchain.PresentQueueIndex == _deviceCreateState.QueueFamilyInfo.MainGraphicsFamilyIdx ? QueueLock : vkSwapchain.PresentLock;
            lock (presentLock)
            {
                var presentResult = vkQueuePresentKHR(vkSwapchain.PresentQueue, &presentInfo);
                
                if (presentResult
                    is not VkResult.VK_SUCCESS
                    and not VkResult.VK_SUBOPTIMAL_KHR
                    and not VkResult.VK_ERROR_OUT_OF_DATE_KHR)
                {
                    VulkanUtil.ThrowResult(presentResult);
                }

                VkFence fence = vkSwapchain.ImageAvailableFence;
                if (vkSwapchain.AcquireNextImage(Device, VkSemaphore.NULL, fence))
                {
                    VulkanUtil.CheckResult(vkWaitForFences(Device, 1, &fence, (VkBool32)true, ulong.MaxValue));

                    VulkanUtil.CheckResult(vkResetFences(Device, 1, &fence));
                }
            }
        }
    }
}
