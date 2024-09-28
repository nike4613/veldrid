using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using TerraFX.Interop.Vulkan;
using IResourceRefCountTarget = Veldrid.Vulkan.IResourceRefCountTarget;
using ResourceRefCount = Veldrid.Vulkan.ResourceRefCount;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal unsafe sealed class VulkanCommandList : CommandList, IResourceRefCountTarget
    {
        public VulkanGraphicsDevice Device { get; }
        private readonly VkCommandPool _pool;
        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        private string? _name;

        // Persistent reuse fields
        private readonly object _commandBufferListLock = new();
        private readonly Stack<VkCommandBuffer> _availableCommandBuffers = new();
        private readonly Stack<StagingResourceInfo> _availableStagingInfo = new();
        private readonly Dictionary<VkCommandBuffer, StagingResourceInfo> _submittedStagingInfos = new();
        private readonly Dictionary<ISynchronizedResource, ResourceSyncInfo> _resourceSyncInfo = new();

        // Transient per-use fields
        private StagingResourceInfo _currentStagingInfo;

        // When we grab command buffers, we always actually allocate 2 of them: one for global memory sync, and the other for actual commands.
        private VkCommandBuffer _currentCb;
        private VkCommandBuffer _syncCb;

        private bool _bufferBegun;
        private bool _bufferEnded;

        public VulkanCommandList(VulkanGraphicsDevice device, VkCommandPool pool, in CommandListDescription description)
            : base(description, device.Features, device.UniformBufferMinOffsetAlignment, device.StructuredBufferMinOffsetAlignment)
        {
            Device = device;
            _pool = pool;
            RefCount = new(this);
        }

        public override void Dispose()
        {
            RefCount.DecrementDispose();
        }

        void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyCommandPool(Device.Device, _pool, null);
        }

        internal readonly struct StagingResourceInfo
        {
            //public List<VkBuffer> BuffersUsed { get; }
            //public List<VkTexture> TexturesUsed { get; }
            public HashSet<ResourceRefCount> Resources { get; }

            public bool IsValid => Resources != null;

            public StagingResourceInfo()
            {
                //BuffersUsed = new List<VkBuffer>();
                //TexturesUsed = new List<VkTexture>();
                Resources = new HashSet<ResourceRefCount>();
            }

            public void AddResource(ResourceRefCount count)
            {
                if (Resources.Add(count))
                {
                    count.Increment();
                }
            }

            public void Clear()
            {
                //BuffersUsed.Clear();
                //TexturesUsed.Clear();
                Resources.Clear();
            }
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                // TODO: staging buffer name?
                UpdateBufferNames(value);
            }
        }

        private void UpdateBufferNames(string? name)
        {
            if (Device.HasSetMarkerName)
            {
                if (_currentCb != VkCommandBuffer.NULL)
                {
                    Device.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_COMMAND_BUFFER_EXT,
                        (ulong)_currentCb.Value, name);
                }

                if (_syncCb != VkCommandBuffer.NULL)
                {
                    Device.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_COMMAND_BUFFER_EXT,
                    (ulong)_syncCb.Value, name + " (synchronization)");
                }
            }
        }

        private VkCommandBuffer GetNextCommandBuffer()
        {
            VkCommandBuffer result = default;
            GetNextCommandBuffers(new(ref result));
            return result;
        }

        private void GetNextCommandBuffers(Span<VkCommandBuffer> buffers)
        {
            lock (_commandBufferListLock)
            {
                var needToCreate = buffers.Length;
                while (needToCreate > 0 && _availableCommandBuffers.TryPeek(out var cb))
                {
                    VulkanUtil.CheckResult(vkResetCommandBuffer(cb, 0));
                    needToCreate--;
                    buffers[needToCreate] = cb;
                }

                if (needToCreate <= 0) { return; }

                // note: access to the underlying pool must be synchronized by the application
                var allocateInfo = new VkCommandBufferAllocateInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
                    commandPool = _pool,
                    commandBufferCount = (uint)needToCreate,
                    level = VkCommandBufferLevel.VK_COMMAND_BUFFER_LEVEL_PRIMARY,
                };

                fixed (VkCommandBuffer* pBuffers = buffers)
                {
                    VulkanUtil.CheckResult(vkAllocateCommandBuffers(Device.Device, &allocateInfo, pBuffers));
                }
            }
        }

        private void ReturnCommandBuffer(VkCommandBuffer cb)
        {
            lock (_commandBufferListLock)
            {
                _availableCommandBuffers.Push(cb);
            }
        }

        private StagingResourceInfo GetStagingInfo()
        {
            if (!_availableStagingInfo.TryPop(out var result))
            {
                result = new();
            }
            return result;
        }

        private void RecycleStagingInfo(ref StagingResourceInfo stagingInfo)
        {
            // TODO: recycle staging buffers

            foreach (var refcount in stagingInfo.Resources)
            {
                refcount.Decrement();
            }

            stagingInfo.Clear();
            _availableStagingInfo.Push(stagingInfo);
            stagingInfo = default;
        }

        internal record struct FenceCompletionCallbackInfo(
            VulkanCommandList CommandList,
            VkCommandBuffer MainCb,
            VkCommandBuffer SyncCb,
            VkSemaphore SyncSemaphore,
            Action<VulkanCommandList>? OnSubmitCompleted,
            bool FenceWasRented
            );

        public void SubmitToQueue(VkQueue queue, VulkanFence? submitFence, Action<VulkanCommandList>? onSubmitCompleted)
        {
            if (!_bufferEnded)
            {
                throw new VeldridException("Buffer must be ended to be submitted");
            }

            var cb = _currentCb;
            _currentCb = default;
            var syncCb = _syncCb;
            _syncCb = default;

            var resourceInfo = _currentStagingInfo;
            _currentStagingInfo = default;

            // now we want to do all of the actual submission work
            var syncToMainSem = Device.GetSemaphore();
            var fence = submitFence is not null ? submitFence.DeviceFence : Device.GetSubmissionFence();
            var fenceWasRented = submitFence is null;

            if (submitFence is not null)
            {
                // if we're to wait on a fence controlled by a VulkanFence, make sure to ref it
                resourceInfo.AddResource(submitFence.RefCount);
            }

            {
                // record the synchronization command buffer first
                var beginInfo = new VkCommandBufferBeginInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                    flags = VkCommandBufferUsageFlags.VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
                };
                VulkanUtil.CheckResult(vkBeginCommandBuffer(syncCb, &beginInfo));
                EmitInitialResourceSync(syncCb);
                VulkanUtil.CheckResult(vkEndCommandBuffer(syncCb));

                // then submit everything with just one submission
                var syncSemaphoreInfo = new VkSemaphoreSubmitInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_SUBMIT_INFO,
                    semaphore = syncToMainSem,
                    stageMask = VkPipelineStageFlags2.VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
                };

                var syncCmdSubmitInfo = new VkCommandBufferSubmitInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_SUBMIT_INFO,
                    commandBuffer = syncCb,
                };

                var mainCmdSubmitInfo = new VkCommandBufferSubmitInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_SUBMIT_INFO,
                    commandBuffer = cb,
                };

                Span<VkSubmitInfo2> submitInfos = [
                    // first, the synchronization command buffer
                    new()
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_SUBMIT_INFO_2,
                        commandBufferInfoCount = 1,
                        pCommandBufferInfos = &syncCmdSubmitInfo,
                        signalSemaphoreInfoCount = 1,
                        pSignalSemaphoreInfos = &syncSemaphoreInfo,
                    },
                    // then, the main command buffer
                    new()
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_SUBMIT_INFO_2,
                        waitSemaphoreInfoCount = 1,
                        pWaitSemaphoreInfos = &syncSemaphoreInfo,
                        commandBufferInfoCount = 1,
                        pCommandBufferInfos = &mainCmdSubmitInfo,
                    },
                ];

                fixed (VkSubmitInfo2* pSubmitInfos = submitInfos)
                {
                    VulkanUtil.CheckResult(Device.vkQueueSubmit2(queue, (uint)submitInfos.Length, pSubmitInfos, fence));
                }
            }

            RefCount.Increment();
            Device.RegisterFenceCompletionCallback(fence, new(this, cb, syncCb, syncToMainSem, onSubmitCompleted, fenceWasRented));

            lock (_commandBufferListLock)
            {
                // record that we've submitted this command list, and associate the relevant resources
                // note: we don't need to add the syncCb here, because it's part of the FenceCompletionCallbackInfo registered above
                _submittedStagingInfos.Add(cb, resourceInfo);
            }
        }

        internal void OnSubmissionFenceCompleted(VkFence fence, in FenceCompletionCallbackInfo callbackInfo, bool errored)
        {
            Debug.Assert(this == callbackInfo.CommandList);

            try
            {
                // a submission finished, lets get the associated resource info and remove it from the list
                StagingResourceInfo resourceInfo;
                lock (_commandBufferListLock)
                {
                    if (!_submittedStagingInfos.Remove(callbackInfo.MainCb, out resourceInfo))
                    {
                        if (!errored) ThrowUnreachableStateException();
                    }

                    // return the command buffers
                    _availableCommandBuffers.Push(callbackInfo.MainCb);
                    _availableCommandBuffers.Push(callbackInfo.SyncCb);
                }

                // reset and return the fence as needed
                if (callbackInfo.FenceWasRented)
                {
                    VulkanUtil.CheckResult(vkResetFences(Device.Device, 1, &fence));
                    Device.ReturnSubmissionFence(fence);
                }
                else
                {
                    // if the fence wasn't rented, it's part of a VulkanFence object, and the application may still care about its state
                }

                // recycle the staging info
                RecycleStagingInfo(ref resourceInfo);

                // and finally, invoke the callback if it was provided
                callbackInfo.OnSubmitCompleted?.Invoke(this);
            }
            finally
            {
                RefCount.Decrement();
            }
        }

        private void EmitInitialResourceSync(VkCommandBuffer cb)
        {
            // TODO:
        }

        [DoesNotReturn]
        private static void ThrowUnreachableStateException()
        {
            throw new Exception("Implementation reached unexpected condition.");
        }

        public override void Begin()
        {
            if (_bufferBegun)
            {
                throw new VeldridException(
                    "CommandList must be in its initial state, or End() must have been called, for Begin() to be valid to call.");
            }

            if (_bufferEnded)
            {
                _bufferEnded = false;

                if (_currentCb != VkCommandBuffer.NULL)
                {
                    ReturnCommandBuffer(_currentCb);
                }

                if (_syncCb != VkCommandBuffer.NULL)
                {
                    ReturnCommandBuffer(_syncCb);
                }

                if (_currentStagingInfo.IsValid)
                {
                    RecycleStagingInfo(ref _currentStagingInfo);
                }

                // We always want to pre-allocate BOTH our main command buffer AND our sync command buffer
                Span<VkCommandBuffer> requestedBuffers = [default, default];
                GetNextCommandBuffers(requestedBuffers);
                _currentCb = requestedBuffers[0];
                _syncCb = requestedBuffers[1];

                UpdateBufferNames(Name);
            }

            ClearCachedState();
            // TODO: clear other cached state

            _currentStagingInfo = GetStagingInfo();

            var beginInfo = new VkCommandBufferBeginInfo()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                flags = VkCommandBufferUsageFlags.VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
            };
            VulkanUtil.CheckResult(vkBeginCommandBuffer(_currentCb, &beginInfo));
            _bufferBegun = true;
        }

        public override void End()
        {
            if (!_bufferBegun)
            {
                throw new VeldridException("CommandBuffer must have been started before End() may be called.");
            }

            _bufferBegun = false;
            _bufferEnded = true;

            // TODO: finish render passes

            VulkanUtil.CheckResult(vkEndCommandBuffer(_currentCb));
        }

        private protected override void SetPipelineCore(Pipeline pipeline)
        {
            throw new NotImplementedException();
        }

        private protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer, uint offset)
        {
            throw new NotImplementedException();
        }

        private protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format, uint offset)
        {
            throw new NotImplementedException();
        }

        protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs, ReadOnlySpan<uint> dynamicOffsets)
        {
            throw new NotImplementedException();
        }

        protected override void SetComputeResourceSetCore(uint slot, ResourceSet set, ReadOnlySpan<uint> dynamicOffsets)
        {
            throw new NotImplementedException();
        }

        protected override void SetFramebufferCore(Framebuffer fb)
        {
            throw new NotImplementedException();
        }

        private protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
        {
            throw new NotImplementedException();
        }

        private protected override void ClearDepthStencilCore(float depth, byte stencil)
        {
            throw new NotImplementedException();
        }

        public override void SetViewport(uint index, in Viewport viewport)
        {
            throw new NotImplementedException();
        }

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            throw new NotImplementedException();
        }

        private protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            throw new NotImplementedException();
        }

        private protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            throw new NotImplementedException();
        }

        protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            throw new NotImplementedException();
        }

        protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            throw new NotImplementedException();
        }

        public override void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            throw new NotImplementedException();
        }

        protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
        {
            throw new NotImplementedException();
        }

        protected override void ResolveTextureCore(Texture source, Texture destination)
        {
            throw new NotImplementedException();
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, nint source, uint sizeInBytes)
        {
            throw new NotImplementedException();
        }

        protected override void CopyBufferCore(DeviceBuffer source, DeviceBuffer destination, ReadOnlySpan<BufferCopyCommand> commands)
        {
            throw new NotImplementedException();
        }

        protected override void CopyTextureCore(Texture source, uint srcX, uint srcY, uint srcZ, uint srcMipLevel, uint srcBaseArrayLayer, Texture destination, uint dstX, uint dstY, uint dstZ, uint dstMipLevel, uint dstBaseArrayLayer, uint width, uint height, uint depth, uint layerCount)
        {
            throw new NotImplementedException();
        }

        private protected override void GenerateMipmapsCore(Texture texture)
        {
            throw new NotImplementedException();
        }

        private protected override void PushDebugGroupCore(ReadOnlySpan<char> name)
        {
            throw new NotImplementedException();
        }

        private protected override void PopDebugGroupCore()
        {
            throw new NotImplementedException();
        }

        private protected override void InsertDebugMarkerCore(ReadOnlySpan<char> name)
        {
            throw new NotImplementedException();
        }

        // TODO: implement all other members
    }
}
