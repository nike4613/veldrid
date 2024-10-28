using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Runtime.CompilerServices;

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
        private readonly Dictionary<ISynchronizedResource, ResourceSyncInfo> _resourceSyncInfo = new();
        private readonly List<(ISynchronizedResource Resource, ResourceBarrierInfo Barrier)> _pendingBarriers = new();
        private int _pendingImageBarriers;
        private int _pendingBufferBarriers;

        // Transient per-use fields
        private StagingResourceInfo _currentStagingInfo;

        // When we grab command buffers, we always actually allocate 2 of them: one for global memory sync, and the other for actual commands.
        private VkCommandBuffer _currentCb;
        private VkCommandBuffer _syncCb;

        private bool _bufferBegun;
        private bool _bufferEnded;

        // Dynamic state

        // Graphics State
        private uint _viewportCount;
        private VkViewport[] _viewports = [];
        private VkRect2D[] _scissorRects = [];
        private bool _viewportsChanged = false;
        private bool _scissorRectsChanged = false;

        private VkClearValue[] _clearValues = [];
        private bool[] _validClearValues = [];
        private VkClearValue? _depthClearValue;

        private VulkanFramebuffer? _currentFramebuffer;
        private VulkanPipeline? _currentGraphicsPipeline;
        private BoundResourceSetInfo[] _currentGraphicsResourceSets = [];
        private bool[] _graphicsResourceSetsChanged = [];
        private VkBuffer[] _vertexBindings = [];
        private ulong[] _vertexOffsets = [];
        private uint _numVertexBindings = 0;
        private bool _vertexBindingsChanged;
        private bool _currentFramebufferEverActive;
        private bool _framebufferRenderPassInstanceActive; // <-- This is true when the framebuffer's rendering context (whether a VkRenderPass or a dynamic context) is active

        // Compute State
        private VulkanPipeline? _currentComputePipeline;
        private BoundResourceSetInfo[] _currentComputeResourceSets = [];
        private bool[] _computeResourceSetsChanged = [];

        public VulkanCommandList(VulkanGraphicsDevice device, VkCommandPool pool, in CommandListDescription description)
            : base(description, device.Features, device.UniformBufferMinOffsetAlignment, device.StructuredBufferMinOffsetAlignment)
        {
            Device = device;
            _pool = pool;
            RefCount = new(this);
        }

        public override void Dispose() => RefCount?.DecrementDispose();

        void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyCommandPool(Device.Device, _pool, null);
        }

        internal readonly struct StagingResourceInfo
        {
            public List<VulkanBuffer> BuffersUsed { get; }
            public List<VulkanTexture> TexturesUsed { get; }
            public HashSet<ResourceRefCount> RefCounts { get; }

            public bool IsValid => RefCounts != null;

            public StagingResourceInfo()
            {
                BuffersUsed = new();
                TexturesUsed = new List<VulkanTexture>();
                RefCounts = new();
            }

            public void AddResource(IResourceRefCountTarget resource)
            {
                AddRefCount(resource.RefCount);
            }

            public void AddRefCount(ResourceRefCount refCount)
            {
                if (RefCounts.Add(refCount))
                {
                    refCount.Increment();
                }
            }

            public void Clear()
            {
                BuffersUsed.Clear();
                TexturesUsed.Clear();
                RefCounts.Clear();
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
                while (needToCreate > 0 && _availableCommandBuffers.TryPop(out var cb))
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
            if (stagingInfo.BuffersUsed.Count > 0)
            {
                Device.ReturnPooledStagingBuffers(CollectionsMarshal.AsSpan(stagingInfo.BuffersUsed));
            }
            if (stagingInfo.TexturesUsed.Count > 0)
            {
                Device.ReturnPooledStagingTextures(CollectionsMarshal.AsSpan(stagingInfo.TexturesUsed));
            }

            foreach (var refcount in stagingInfo.RefCounts)
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
            VkSemaphore MainCompleteSemaphore,
            StagingResourceInfo StagingResourceInfo,
            Action<VulkanCommandList>? OnSubmitCompleted,
            bool FenceWasRented
            );

        public (VkSemaphore SubmitSem, VkFence SubmitFence) SubmitToQueue(VkQueue queue, VulkanFence? submitFence, Action<VulkanCommandList>? onSubmitCompleted, VkPipelineStageFlags2 completionSemaphoreStages)
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
            var mainCompleteSem = completionSemaphoreStages != 0 ? Device.GetSemaphore() : default;
            var fence = submitFence is not null ? submitFence.DeviceFence : Device.GetSubmissionFence();
            var fenceWasRented = submitFence is null;

            if (submitFence is not null)
            {
                // if we're to wait on a fence controlled by a VulkanFence, make sure to ref it
                resourceInfo.AddRefCount(submitFence.RefCount);
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

                // now that we've finished everything we need to do with synchro, clear our dict
                _resourceSyncInfo.Clear();
                _pendingBarriers.Clear();
                _pendingImageBarriers = 0;
                _pendingBufferBarriers = 0;

                // then submit everything with just one submission
                var syncSemaphoreInfo = new VkSemaphoreSubmitInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_SUBMIT_INFO,
                    semaphore = syncToMainSem,
                    // TODO: I think we can do better here. We know in principle which stages are in the second scope of our sync, so should duplicate that here.
                    stageMask = VkPipelineStageFlags2.VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
                };

                var syncCmdSubmitInfo = new VkCommandBufferSubmitInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_SUBMIT_INFO,
                    commandBuffer = syncCb,
                };

                var mainSemaphoreInfo = new VkSemaphoreSubmitInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_SUBMIT_INFO,
                    semaphore = mainCompleteSem,
                    stageMask = completionSemaphoreStages,
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
                        signalSemaphoreInfoCount = completionSemaphoreStages != 0 ? 1u : 0u,
                        pSignalSemaphoreInfos = &mainSemaphoreInfo,
                    },
                ];

                fixed (VkSubmitInfo2* pSubmitInfos = submitInfos)
                {
                    VulkanUtil.CheckResult(Device.vkQueueSubmit2(queue, (uint)submitInfos.Length, pSubmitInfos, fence));
                }
            }

            RefCount.Increment();
            Device.RegisterFenceCompletionCallback(fence, new(this, cb, syncCb, syncToMainSem, mainCompleteSem, resourceInfo, onSubmitCompleted, fenceWasRented));

            return (mainCompleteSem, fence);
        }

        internal void OnSubmissionFenceCompleted(VkFence fence, in FenceCompletionCallbackInfo callbackInfo, bool errored)
        {
            Debug.Assert(this == callbackInfo.CommandList);

            try
            {
                // a submission finished, lets get the associated resource info and remove it from the list
                lock (_commandBufferListLock)
                {
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

                // return the semaphores
                Device.ReturnSemaphore(callbackInfo.SyncSemaphore);
                if (callbackInfo.MainCompleteSemaphore != VkSemaphore.NULL)
                {
                    Device.ReturnSemaphore(callbackInfo.MainCompleteSemaphore);
                }

                // recycle the staging info
                var resourceInfo = callbackInfo.StagingResourceInfo;
                RecycleStagingInfo(ref resourceInfo);

                // and finally, invoke the callback if it was provided
                callbackInfo.OnSubmitCompleted?.Invoke(this);
            }
            finally
            {
                RefCount.Decrement();
            }
        }

        private static int ReadersStageIndex(VkPipelineStageFlags bit)
            => bit switch
            {
                VkPipelineStageFlags.VK_PIPELINE_STAGE_VERTEX_INPUT_BIT => 0,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_VERTEX_SHADER_BIT => 1,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT => 2,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT => 3,

                // tesselation and geometry shaders are assigned the same pipeline stage. We make sure when building needed barriers that we over-allocate barriers for this.
                VkPipelineStageFlags.VK_PIPELINE_STAGE_TESSELLATION_CONTROL_SHADER_BIT => 4,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_TESSELLATION_EVALUATION_SHADER_BIT => 4,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_GEOMETRY_SHADER_BIT => 4,

                VkPipelineStageFlags.VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT => 5,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT => 6,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT => 7,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT => 8,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_HOST_BIT => 9,

                _ => Illegal.Value<VkPipelineStageFlags, int>()
            };

        private static int ReadersAccessIndex(VkAccessFlags bit)
            => bit switch
            {
                // All Shaders
                VkAccessFlags.VK_ACCESS_SHADER_READ_BIT => 0,
                VkAccessFlags.VK_ACCESS_SHADER_WRITE_BIT => 1,
                VkAccessFlags.VK_ACCESS_UNIFORM_READ_BIT => 2,

                // Vertex Input
                VkAccessFlags.VK_ACCESS_VERTEX_ATTRIBUTE_READ_BIT => 0,
                VkAccessFlags.VK_ACCESS_INDEX_READ_BIT => 1,

                // Color Attachments
                VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_READ_BIT => 0,
                VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT => 1,

                // Depth Stencil
                VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT => 0,
                VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT => 1,

                // Transfer
                VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT => 0,
                VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT => 1,

                // Host
                VkAccessFlags.VK_ACCESS_HOST_READ_BIT => 0,
                VkAccessFlags.VK_ACCESS_HOST_WRITE_BIT => 1,

                _ => Illegal.Value<VkAccessFlags, int>()
            };

        private static ReadOnlySpan<VkAccessFlags> ValidAccessFlags => [
            // VERTEX_INPUT
            VkAccessFlags.VK_ACCESS_VERTEX_ATTRIBUTE_READ_BIT | VkAccessFlags.VK_ACCESS_INDEX_READ_BIT,
            // VERTEX_SHADER
            VkAccessFlags.VK_ACCESS_SHADER_READ_BIT | VkAccessFlags.VK_ACCESS_UNIFORM_READ_BIT,
            // FRAGMENT_SHADER
            VkAccessFlags.VK_ACCESS_SHADER_READ_BIT | VkAccessFlags.VK_ACCESS_UNIFORM_READ_BIT,
            // COMPUTE_SHADER
            VkAccessFlags.VK_ACCESS_SHADER_READ_BIT | VkAccessFlags.VK_ACCESS_UNIFORM_READ_BIT | VkAccessFlags.VK_ACCESS_SHADER_WRITE_BIT,
            // TESS_CONTROL, TESS_EVAL, GEOMETRY
            VkAccessFlags.VK_ACCESS_SHADER_READ_BIT | VkAccessFlags.VK_ACCESS_UNIFORM_READ_BIT,
            // EARLY_FRAGMENT_TESTS
            VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT | VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT,
            // LATE_FRAGMENT_TESTS
            VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT | VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT,
            // COLOR_ATTACHMENT_OUTPUT,
            VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
            // TRANSFER
            VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT | VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT,
            // HOST
            VkAccessFlags.VK_ACCESS_HOST_READ_BIT | VkAccessFlags.VK_ACCESS_HOST_WRITE_BIT,
        ];

        private static uint PerStageAccessMask(SyncBarrierMasks masks)
        {
            const int BitsPerStage = 3;

            var result = 0u;

            for (var stageMask = (uint)masks.StageMask; stageMask != 0; )
            {
                var stageBit = stageMask & ~(stageMask - 1);
                stageMask &= ~stageBit;

                var stageIndex = ReadersStageIndex((VkPipelineStageFlags)stageBit);
                for (var accessMask = (uint)masks.AccessMask; accessMask != 0; )
                {
                    var accessBit = accessMask & ~(accessMask - 1);
                    accessMask &= ~accessBit;

                    if ((ValidAccessFlags[stageIndex] & (VkAccessFlags)accessBit) != 0)
                    {
                        // this is an access we declare to be valid
                        var accessIndex = ReadersAccessIndex((VkAccessFlags)accessBit);
                        result |= 1u << ((BitsPerStage * stageIndex) + accessIndex);
                    }
                }
            }

            return result;
        }

        private bool TryBuildSyncBarrier(ref SyncState state, in SyncRequest req, out ResourceBarrierInfo barrier)
        {
            const VkAccessFlags AllWriteAccesses =
                VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT
                | VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT
                | VkAccessFlags.VK_ACCESS_HOST_WRITE_BIT
                //| VkAccessFlags.VK_ACCESS_MEMORY_WRITE_BIT
                | VkAccessFlags.VK_ACCESS_SHADER_WRITE_BIT
                | VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT;

            const VkPipelineStageFlags TesselationStages =
                VkPipelineStageFlags.VK_PIPELINE_STAGE_TESSELLATION_CONTROL_SHADER_BIT
                | VkPipelineStageFlags.VK_PIPELINE_STAGE_TESSELLATION_EVALUATION_SHADER_BIT
                | VkPipelineStageFlags.VK_PIPELINE_STAGE_GEOMETRY_SHADER_BIT;

            var requestAccess = req.BarrierMasks.AccessMask;
            var requestStages = req.BarrierMasks.StageMask;
            if ((requestStages & TesselationStages) != 0)
            {
                // if any of the tesselation stages are set, set all of them
                requestStages |= TesselationStages;
            }

            // note: if the target layout is UNDEFINED, we treat that as "no change". If the current layout is UNDEFINED, this is the first (potential) barrier in this CL
            var needsLayoutTransition = req.Layout != 0 && state.CurrentImageLayout != 0 && req.Layout != state.CurrentImageLayout;
            var writeAccesses = requestAccess & AllWriteAccesses;
            var needsWrite = writeAccesses != 0 || needsLayoutTransition;

            barrier = new();
            if (!needsWrite)
            {
                // read-only operation.
                // These can run concurrently with other reads, but need to wait for outstanding writes.

                var perStageAccessMask = PerStageAccessMask(new() { StageMask = requestStages, AccessMask = requestAccess });
                var allAccessesHaveSeenWrite = (state.PerStageReaders & perStageAccessMask) == perStageAccessMask;

                if (state.LastWriter.StageMask != 0 && !allAccessesHaveSeenWrite)
                {
                    // If there was a preceding write and the request stage hasn't consumed that write, we need a barrier.
                    barrier.SrcStageMask |= state.LastWriter.StageMask;
                    barrier.SrcAccess |= state.LastWriter.AccessMask & AllWriteAccesses;
                }

                // In either case, we need to add the op to the ongoing reads
                state.OngoingReaders.StageMask |= requestStages;
                state.OngoingReaders.AccessMask |= requestAccess;
                // And always mark the per-stage readers
                state.PerStageReaders |= perStageAccessMask;
            }
            else
            {
                // This operation is a write. Only one is permitteed simulatneously, and we must wait for outstanding reads.
                barrier.SrcStageMask |= state.OngoingReaders.StageMask;
                barrier.SrcAccess |= state.OngoingReaders.AccessMask;

                // after the write, there are no more up-to-date readers
                state.OngoingReaders = default;
                state.PerStageReaders = 0;

                // If there's already a pending write, wait for it as well.
                // If we already are waiting for reads, they're implicitly waiting for the previous write, so we don't need to add that dependency.
                if (barrier.SrcStageMask == 0 && state.LastWriter.StageMask != 0)
                {
                    barrier.SrcStageMask |= state.LastWriter.StageMask;
                    barrier.SrcAccess |= state.LastWriter.AccessMask;
                }

                // Mark our writer
                state.LastWriter = new() { StageMask = requestStages, AccessMask = requestAccess };

                // If the actual requested access was read-only, this is a pure layout transition.
                // This meeans it is also a read, and should be updated appropriately.
                if ((requestAccess & AllWriteAccesses) == 0)
                {
                    state.OngoingReaders.StageMask |= requestStages;
                    state.OngoingReaders.AccessMask |= requestAccess;
                    state.PerStageReaders |= PerStageAccessMask(new() { StageMask = requestStages, AccessMask = requestAccess });
                }
            }

            // A barrier is needed if we have any stages to wait on, or if we need a layout transition.
            var needsBarrier = barrier.SrcStageMask != 0 || needsLayoutTransition;

            if (needsBarrier)
            {
                // if we need a barrier, populate the remaining fields for the barrier
                barrier.DstAccess = requestAccess;
                barrier.DstStageMask = requestStages;
                if (barrier.SrcStageMask == 0)
                {
                    barrier.SrcStageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT;
                }
                barrier.SrcLayout = state.CurrentImageLayout;
                barrier.DstLayout = req.Layout;
            }

            // Finally, update the resource's image layout
            state.CurrentImageLayout = req.Layout;

            return needsBarrier;
        }

        // returns true if the sync generated a barrier
        public bool SyncResource(ISynchronizedResource resource, in SyncRequest req)
        {
            ref var localSyncInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(_resourceSyncInfo, resource, out var exists);
            if (!exists)
            {
                // we don't want to pull in layout here, because we special-case that when generating barriers
                localSyncInfo = new();
            }

            var result = false;

            if (SyncResourceToState(ref localSyncInfo.LocalState, resource, req))
            {
                localSyncInfo.HasBarrier = true;
                result = true;
            }

            // mark the expected state appropriately
            if (!localSyncInfo.HasBarrier)
            {
                localSyncInfo.Expected.BarrierMasks.StageMask |= req.BarrierMasks.StageMask;
                localSyncInfo.Expected.BarrierMasks.AccessMask |= req.BarrierMasks.AccessMask;
                // we want to make note of the first layout that we expect, so we can transition into it at the right time
                if (localSyncInfo.Expected.Layout == 0)
                {
                    localSyncInfo.Expected.Layout = req.Layout;
                }
            }

            return result;
        }

        private bool SyncResourceToState(ref SyncState state, ISynchronizedResource resource, SyncRequest req)
        {
            var resourceIsImage = resource is VulkanTexture vkTex && vkTex.DeviceImage != VkImage.NULL;
            if (!resourceIsImage)
            {
                // the resource isn't an image, don't actually use the layout
                req.Layout = 0;
            }

            if (TryBuildSyncBarrier(ref state, in req, out var barrier))
            {
                // a barrier is needed, mark appropriately
                _pendingBarriers.Add((resource, barrier));

                if (resourceIsImage)
                {
                    _pendingImageBarriers++;
                }
                else
                {
                    _pendingBufferBarriers++;
                }

                return true;
            }

            return false;
        }

        private void EmitInitialResourceSync(VkCommandBuffer cb)
        {
            Debug.Assert(_pendingBarriers.Count == 0);
            Debug.Assert(_pendingImageBarriers == 0);
            Debug.Assert(_pendingBufferBarriers == 0);

            // we're just going to reuse the existing buffers we have, because it's all we actually need
            // note: we're also under a global lock here, so it's safe to mutate the global information
            foreach (var (res, info) in _resourceSyncInfo)
            {
                _ = SyncResourceToState(ref res.SyncState, res, info.Expected);
            }

            // then emit the synchronization to the target command buffer
            EmitQueuedSynchro(cb, _pendingBarriers, ref _pendingImageBarriers, ref _pendingBufferBarriers);
        }

        public void EmitQueuedSynchro()
        {
            EmitQueuedSynchro(_currentCb, _pendingBarriers, ref _pendingImageBarriers, ref _pendingBufferBarriers);
        }

        private void EmitQueuedSynchro(VkCommandBuffer cb,
            List<(ISynchronizedResource resource, ResourceBarrierInfo barrier)> pendingBarriers,
            ref int pendingImageBarriers, ref int pendingBufferBarriers)
        {
            if (Device._deviceCreateState.HasSync2Ext)
            {
                EmitQueuedSynchroSync2(Device, cb, pendingBarriers, ref pendingImageBarriers, ref pendingBufferBarriers);
            }
            else
            {
                EmitQueuedSynchroVk11(cb, pendingBarriers, ref pendingImageBarriers, ref pendingBufferBarriers);
            }
        }

        private static VkImageAspectFlags GetAspectForTexture(VulkanTexture tex)
        {
            if ((tex.Usage & TextureUsage.DepthStencil) != 0)
            {
                return FormatHelpers.IsStencilFormat(tex.Format)
                    ? VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT | VkImageAspectFlags.VK_IMAGE_ASPECT_STENCIL_BIT
                    : VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT;
            }
            else
            {
                return VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT;
            }
        }

        private static void EmitQueuedSynchroSync2(VulkanGraphicsDevice device, VkCommandBuffer cb,
            List<(ISynchronizedResource resource, ResourceBarrierInfo barrier)> pendingBarriers,
            ref int pendingImageBarriers, ref int pendingBufferBarriers)
        {
            var imgBarriers = ArrayPool<VkImageMemoryBarrier2>.Shared.Rent(pendingImageBarriers);
            var bufBarriers = ArrayPool<VkBufferMemoryBarrier2>.Shared.Rent(pendingBufferBarriers);
            var imgIdx = 0;
            var bufIdx = 0;
            foreach (var (resource, barrier) in pendingBarriers)
            {
                switch (resource)
                {
                    case VulkanTexture vkTex when vkTex.DeviceImage != VkImage.NULL:
                    {
                        ref var vkBarrier = ref imgBarriers[imgIdx++];
                        vkBarrier = new()
                        {
                            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2,
                            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                            srcStageMask = (VkPipelineStageFlags2)(uint)barrier.SrcStageMask,
                            dstStageMask = (VkPipelineStageFlags2)(uint)barrier.DstStageMask,
                            srcAccessMask = (VkAccessFlags2)(uint)barrier.SrcAccess,
                            dstAccessMask = (VkAccessFlags2)(uint)barrier.DstAccess,
                            oldLayout = barrier.SrcLayout,
                            newLayout = barrier.DstLayout,
                            image = vkTex.DeviceImage,
                            subresourceRange = new()
                            {
                                baseArrayLayer = 0,
                                baseMipLevel = 0,
                                layerCount = vkTex.ArrayLayers,
                                levelCount = vkTex.MipLevels,
                                aspectMask = GetAspectForTexture(vkTex)
                            }
                        };
                    }
                    break;

                    case VulkanBuffer vkBuf:
                        var deviceBuf = vkBuf.DeviceBuffer;
                        goto AnyBuffer;
                    case VulkanTexture vkTex when vkTex.StagingBuffer != VkBuffer.NULL:
                        deviceBuf = vkTex.StagingBuffer;
                        goto AnyBuffer;

                    AnyBuffer:
                        {
                            ref var vkBarrier = ref bufBarriers[bufIdx++];
                            vkBarrier = new()
                            {
                                sType = VkStructureType.VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER_2,
                                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                                dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                                srcStageMask = (VkPipelineStageFlags2)(uint)barrier.SrcStageMask,
                                dstStageMask = (VkPipelineStageFlags2)(uint)barrier.DstStageMask,
                                srcAccessMask = (VkAccessFlags2)(uint)barrier.SrcAccess,
                                dstAccessMask = (VkAccessFlags2)(uint)barrier.DstAccess,
                                offset = 0,
                                size = VK_WHOLE_SIZE,
                                buffer  = deviceBuf,
                            };
                        }
                        break;
                }
            }

            pendingBarriers.Clear();
            pendingImageBarriers = 0;
            pendingBufferBarriers = 0;

            if (bufIdx > 0 || imgIdx > 0)
            {
                fixed (VkBufferMemoryBarrier2* pBufBarriers = bufBarriers)
                fixed (VkImageMemoryBarrier2* pImgBarriers = imgBarriers)
                {
                    var depInfo = new VkDependencyInfo()
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_DEPENDENCY_INFO,
                        bufferMemoryBarrierCount = (uint)bufIdx,
                        pBufferMemoryBarriers = pBufBarriers,
                        imageMemoryBarrierCount = (uint)imgIdx,
                        pImageMemoryBarriers = pImgBarriers,
                    };
                    device.vkCmdPipelineBarrier2(cb, &depInfo);
                }
            }

            ArrayPool<VkImageMemoryBarrier2>.Shared.Return(imgBarriers);
            ArrayPool<VkBufferMemoryBarrier2>.Shared.Return(bufBarriers);
        }

        private void EmitQueuedSynchroVk11(VkCommandBuffer cb,
            List<(ISynchronizedResource resource, ResourceBarrierInfo barrier)> pendingBarriers,
            ref int pendingImageBarriers, ref int pendingBufferBarriers)
        {
            var imgBarriers = ArrayPool<VkImageMemoryBarrier>.Shared.Rent(pendingImageBarriers);
            var bufBarriers = ArrayPool<VkBufferMemoryBarrier>.Shared.Rent(pendingBufferBarriers);
            var imgIdx = 0;
            var bufIdx = 0;

            // TODO: is there something better we can (or should) do for this?
            VkPipelineStageFlags srcStageMask = 0;
            VkPipelineStageFlags dstStageMask = 0;

            foreach (var (resource, barrier) in pendingBarriers)
            {
                switch (resource)
                {
                    case VulkanTexture vkTex when vkTex.DeviceImage != VkImage.NULL:
                    {
                        ref var vkBarrier = ref imgBarriers[imgIdx++];
                        srcStageMask |= barrier.SrcStageMask;
                        dstStageMask |= barrier.DstStageMask;
                        vkBarrier = new()
                        {
                            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2,
                            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                            srcAccessMask = barrier.SrcAccess,
                            dstAccessMask = barrier.DstAccess,
                            oldLayout = barrier.SrcLayout,
                            newLayout = barrier.DstLayout,
                            image = vkTex.DeviceImage,
                            subresourceRange = new()
                            {
                                baseArrayLayer = 0,
                                baseMipLevel = 0,
                                layerCount = vkTex.ArrayLayers,
                                levelCount = vkTex.MipLevels,
                                aspectMask = GetAspectForTexture(vkTex)
                            }
                        };
                    }
                    break;

                    case VulkanBuffer vkBuf:
                        var deviceBuf = vkBuf.DeviceBuffer;
                        goto AnyBuffer;
                    case VulkanTexture vkTex when vkTex.StagingBuffer != VkBuffer.NULL:
                        deviceBuf = vkTex.StagingBuffer;
                        goto AnyBuffer;

                        AnyBuffer:
                        {
                            ref var vkBarrier = ref bufBarriers[bufIdx++];
                            srcStageMask |= barrier.SrcStageMask;
                            dstStageMask |= barrier.DstStageMask;
                            vkBarrier = new()
                            {
                                sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2,
                                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                                dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                                srcAccessMask = barrier.SrcAccess,
                                dstAccessMask = barrier.DstAccess,
                                offset = 0,
                                size = VK_WHOLE_SIZE,
                                buffer = deviceBuf,
                            };
                        }
                        break;
                }
            }

            pendingBarriers.Clear();
            pendingImageBarriers = 0;
            pendingBufferBarriers = 0;

            if (bufIdx > 0 || imgIdx > 0)
            {
                fixed (VkBufferMemoryBarrier* pBufBarriers = bufBarriers)
                fixed (VkImageMemoryBarrier* pImgBarriers = imgBarriers)
                {
                    vkCmdPipelineBarrier(cb,
                        srcStageMask,
                        dstStageMask,
                        0, 0, null,
                        (uint)bufIdx, pBufBarriers,
                        (uint)imgIdx, pImgBarriers);
                }
            }

            ArrayPool<VkImageMemoryBarrier>.Shared.Return(imgBarriers);
            ArrayPool<VkBufferMemoryBarrier>.Shared.Return(bufBarriers);
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

            //if (_bufferEnded)
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

                _pendingImageBarriers = 0;
                _pendingBufferBarriers = 0;
                _pendingBarriers.Clear();
                _resourceSyncInfo.Clear();

                // We always want to pre-allocate BOTH our main command buffer AND our sync command buffer
                Span<VkCommandBuffer> requestedBuffers = [default, default];
                GetNextCommandBuffers(requestedBuffers);
                _currentCb = requestedBuffers[0];
                _syncCb = requestedBuffers[1];

                UpdateBufferNames(Name);
            }

            ClearCachedState();
            _currentFramebuffer = null;
            _currentGraphicsPipeline = null;
            _currentComputePipeline = null;
            ClearSets(_currentGraphicsResourceSets);
            ClearSets(_currentComputeResourceSets);
            _numVertexBindings = 0;
            _viewportCount = 0;
            _depthClearValue = null;
            _viewportsChanged = false;
            _scissorRectsChanged = false;
            _currentFramebufferEverActive = false;
            _framebufferRenderPassInstanceActive = false;
            Util.ClearArray(_graphicsResourceSetsChanged);
            Util.ClearArray(_computeResourceSetsChanged);
            Util.ClearArray(_clearValues);
            Util.ClearArray(_validClearValues);
            Util.ClearArray(_scissorRects);
            Util.ClearArray(_vertexBindings);
            Util.ClearArray(_vertexOffsets);
            Util.ClearArray(_viewports);
            Util.ClearArray(_scissorRects);

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

            EnsureNoRenderPass();
            EmitQueuedSynchro(); // at the end of the CL, any queued synchronization needs to be emitted so we don't miss any

            _bufferBegun = false;
            _bufferEnded = true;

            VulkanUtil.CheckResult(vkEndCommandBuffer(_currentCb));
        }

        internal void AddStagingResource(VulkanBuffer buffer)
        {
            _currentStagingInfo.BuffersUsed.Add(buffer);
        }

        internal void AddStagingResource(VulkanTexture texture)
        {
            _currentStagingInfo.TexturesUsed.Add(texture);
        }

        private static void ClearSets(Span<BoundResourceSetInfo> boundSets)
        {
            foreach (ref BoundResourceSetInfo boundSetInfo in boundSets)
            {
                boundSetInfo.Offsets.Dispose();
                boundSetInfo = default;
            }
        }

        protected override void ResolveTextureCore(Texture source, Texture destination)
        {
            var srcTex = Util.AssertSubtype<Texture, VulkanTexture>(source);
            var dstTex = Util.AssertSubtype<Texture, VulkanTexture>(source);
            _currentStagingInfo.AddResource(srcTex);
            _currentStagingInfo.AddResource(dstTex);

            // texture resolve cannot be done in a render pass
            EnsureNoRenderPass();

            var aspectFlags = ((source.Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil)
                ? VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT | VkImageAspectFlags.VK_IMAGE_ASPECT_STENCIL_BIT
                : VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT;
            var region = new VkImageResolve()
            {
                extent = new VkExtent3D() { width = source.Width, height = source.Height, depth = source.Depth },
                srcSubresource = new VkImageSubresourceLayers() { layerCount = 1, aspectMask = aspectFlags },
                dstSubresource = new VkImageSubresourceLayers() { layerCount = 1, aspectMask = aspectFlags }
            };

            // generate synchro for the source and destination
            SyncResource(srcTex, new()
            {
                Layout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                BarrierMasks = new()
                {
                    StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                    AccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
                }
            });
            SyncResource(srcTex, new()
            {
                Layout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                BarrierMasks = new()
                {
                    StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                    AccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT,
                }
            });

            // make sure that it's actually emitted
            EmitQueuedSynchro();

            // then emit the actual resolve command
            vkCmdResolveImage(_currentCb,
                srcTex.DeviceImage,
                VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                dstTex.DeviceImage,
                VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                1, &region);
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, nint source, uint sizeInBytes)
        {
            var stagingBuffer = Device.GetPooledStagingBuffer(sizeInBytes);
            AddStagingResource(stagingBuffer);

            Device.UpdateBuffer(stagingBuffer, 0, source, sizeInBytes);
            CopyBuffer(stagingBuffer, 0, buffer, bufferOffsetInBytes, sizeInBytes);
        }

        protected override void CopyBufferCore(DeviceBuffer source, DeviceBuffer destination, ReadOnlySpan<BufferCopyCommand> commands)
        {
            var srcBuf = Util.AssertSubtype<DeviceBuffer, VulkanBuffer>(source);
            var dstBuf = Util.AssertSubtype<DeviceBuffer, VulkanBuffer>(destination);
            _currentStagingInfo.AddResource(srcBuf);
            _currentStagingInfo.AddResource(dstBuf);

            EnsureNoRenderPass();

            // emit synchro for the 2 buffers
            SyncResource(srcBuf, new()
            {
                BarrierMasks = new()
                {
                    StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                    AccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
                }
            });
            SyncResource(dstBuf, new()
            {
                BarrierMasks = new()
                {
                    StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                    AccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT,
                }
            });
            EmitQueuedSynchro();

            // then actually execute the copy
            // conveniently, BufferCopyCommand has the same layout as VkBufferCopy! We can issue a bunch of copies at once, as a result.
            fixed (BufferCopyCommand* commandPtr = commands)
            {
                int offset = 0;
                int prevOffset = 0;

                while (offset < commands.Length)
                {
                    if (commands[offset].Length != 0)
                    {
                        offset++;
                        continue;
                    }

                    int count = offset - prevOffset;
                    if (count > 0)
                    {
                        vkCmdCopyBuffer(
                            _currentCb,
                            srcBuf.DeviceBuffer,
                            dstBuf.DeviceBuffer,
                            (uint)count,
                            (VkBufferCopy*)(commandPtr + prevOffset));
                    }

                    while (offset < commands.Length)
                    {
                        if (commands[offset].Length != 0)
                        {
                            break;
                        }
                        offset++;
                    }
                    prevOffset = offset;
                }

                {
                    int count = offset - prevOffset;
                    if (count > 0)
                    {
                        vkCmdCopyBuffer(
                            _currentCb,
                            srcBuf.DeviceBuffer,
                            dstBuf.DeviceBuffer,
                            (uint)count,
                            (VkBufferCopy*)(commandPtr + prevOffset));
                    }
                }
            }
        }

        protected override void CopyTextureCore(
            Texture source,
            uint srcX, uint srcY, uint srcZ, uint srcMipLevel, uint srcBaseArrayLayer,
            Texture destination,
            uint dstX, uint dstY, uint dstZ, uint dstMipLevel, uint dstBaseArrayLayer,
            uint width, uint height, uint depth, uint layerCount)
        {
            var srcTex = Util.AssertSubtype<Texture, VulkanTexture>(source);
            var dstTex = Util.AssertSubtype<Texture, VulkanTexture>(destination);

            EnsureNoRenderPass();

            var srcIsStaging = (srcTex.Usage & TextureUsage.Staging) == TextureUsage.Staging;
            var dstIsStaging = (dstTex.Usage & TextureUsage.Staging) == TextureUsage.Staging;

            // before doing anything, make sure we're sync'd
            SyncResource(srcTex, new()
            {
                Layout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                BarrierMasks = new()
                {
                    StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                    AccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
                },
            });
            SyncResource(dstTex, new()
            {
                Layout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                BarrierMasks = new()
                {
                    StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                    AccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT,
                },
            });
            EmitQueuedSynchro();

            if (!srcIsStaging && !dstIsStaging)
            {
                // both textures are non-staging, we can issue a simple CopyImage command
                var copyRegion = new VkImageCopy()
                {
                    extent = new() { width = width, height = height, depth = depth },
                    srcOffset = new() { x = (int)srcX, y = (int)srcY, z = (int)srcZ },
                    dstOffset = new() { x = (int)dstX, y = (int)dstY, z = (int)dstZ },
                    srcSubresource = new()
                    {
                        aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                        layerCount = layerCount,
                        mipLevel = srcMipLevel,
                        baseArrayLayer = srcBaseArrayLayer,
                    },
                    dstSubresource = new()
                    {
                        aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                        layerCount = layerCount,
                        mipLevel = dstMipLevel,
                        baseArrayLayer = dstBaseArrayLayer,
                    },
                };
                vkCmdCopyImage(_currentCb,
                    srcTex.DeviceImage, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    dstTex.DeviceImage, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    1, &copyRegion);
            }
            else if (srcIsStaging && !dstIsStaging)
            {
                // copy from a staging texture to a non-staging texture
                var srcBuf = srcTex.StagingBuffer;
                var srcLayout = srcTex.GetSubresourceLayout(srcMipLevel, srcBaseArrayLayer);

                VkImageSubresourceLayers dstSubresource = new()
                {
                    aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                    layerCount = layerCount,
                    mipLevel = dstMipLevel,
                    baseArrayLayer = dstBaseArrayLayer
                };

                Util.GetMipDimensions(srcTex, srcMipLevel, out var mipWidth, out var mipHeight, out _);
                var blockSize = FormatHelpers.IsCompressedFormat(srcTex.Format) ? 4u : 1u;
                var bufferRowLength = Math.Max(mipWidth, blockSize);
                var bufferImageHeight = Math.Max(mipHeight, blockSize);
                var compressedX = srcX / blockSize;
                var compressedY = srcY / blockSize;
                var blockSizeInBytes = blockSize == 1
                    ? FormatSizeHelpers.GetSizeInBytes(srcTex.Format)
                    : FormatHelpers.GetBlockSizeInBytes(srcTex.Format);
                var rowPitch = FormatHelpers.GetRowPitch(bufferRowLength, srcTex.Format);
                var depthPitch = FormatHelpers.GetDepthPitch(rowPitch, bufferImageHeight, srcTex.Format);

                var copyWidth = Math.Min(width, mipWidth);
                var copyheight = Math.Min(height, mipHeight);

                VkBufferImageCopy regions = new()
                {
                    bufferOffset = srcLayout.offset
                        + (srcZ * depthPitch)
                        + (compressedY * rowPitch)
                        + (compressedX * blockSizeInBytes),
                    bufferRowLength = bufferRowLength,
                    bufferImageHeight = bufferImageHeight,
                    imageExtent = new VkExtent3D() { width = copyWidth, height = copyheight, depth = depth },
                    imageOffset = new VkOffset3D() { x = (int)dstX, y = (int)dstY, z = (int)dstZ },
                    imageSubresource = dstSubresource
                };

                vkCmdCopyBufferToImage(_currentCb, srcBuf,
                    dstTex.DeviceImage, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    1, &regions);
            }
            else if (!srcIsStaging && dstIsStaging)
            {
                // copy from real texture to staging texture
                var srcImg = srcTex.DeviceImage;
                var dstBuf = dstTex.StagingBuffer;

                var srcAspect = (srcTex.Usage & TextureUsage.DepthStencil) != 0
                    ? VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT
                    : VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT;

                Util.GetMipDimensions(dstTex, dstMipLevel, out var mipWidth, out var mipHeight);
                var blockSize = FormatHelpers.IsCompressedFormat(dstTex.Format) ? 4u : 1u;
                var bufferRowLength = Math.Max(mipWidth, blockSize);
                var bufferImageHeight = Math.Max(mipHeight, blockSize);
                var compressedDstX = dstX / blockSize;
                var compressedDstY = dstY / blockSize;
                var blockSizeInBytes = blockSize == 1
                    ? FormatSizeHelpers.GetSizeInBytes(dstTex.Format)
                    : FormatHelpers.GetBlockSizeInBytes(dstTex.Format);
                var rowPitch = FormatHelpers.GetRowPitch(bufferRowLength, dstTex.Format);
                var depthPitch = FormatHelpers.GetDepthPitch(rowPitch, bufferImageHeight, dstTex.Format);

                var layers = ArrayPool<VkBufferImageCopy>.Shared.Rent((int)layerCount);
                for (var layer = 0u; layer < layerCount; layer++)
                {
                    var dstLayout = dstTex.GetSubresourceLayout(dstMipLevel, dstBaseArrayLayer + layer);

                    var srcSubresource = new VkImageSubresourceLayers()
                    {
                        aspectMask = srcAspect,
                        layerCount = 1,
                        mipLevel = srcMipLevel,
                        baseArrayLayer = srcBaseArrayLayer + layer
                    };

                    var region = new VkBufferImageCopy()
                    {
                        bufferRowLength = bufferRowLength,
                        bufferImageHeight = bufferImageHeight,
                        bufferOffset = dstLayout.offset
                            + (dstZ * depthPitch)
                            + (compressedDstY * rowPitch)
                            + (compressedDstX * blockSizeInBytes),
                        imageExtent = new() { width = width, height = height, depth = depth },
                        imageOffset = new() { x = (int)srcX, y = (int)srcY, z = (int)srcZ },
                        imageSubresource = srcSubresource
                    };

                    layers[layer] = region;
                }

                fixed (VkBufferImageCopy* pLayers = layers)
                {
                    vkCmdCopyImageToBuffer(_currentCb,
                        srcImg, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        dstBuf,
                        layerCount, pLayers);
                }

                ArrayPool<VkBufferImageCopy>.Shared.Return(layers);
            }
            else
            {
                Debug.Assert(srcIsStaging && dstIsStaging);
                // both buffers are staging, just do a (set of) buffer->buffer copies
                var srcBuf = srcTex.StagingBuffer;
                var srcLayout = srcTex.GetSubresourceLayout(srcMipLevel, srcBaseArrayLayer);
                var dstBuf = dstTex.StagingBuffer;
                var dstLayout = dstTex.GetSubresourceLayout(dstMipLevel, dstBaseArrayLayer);

                var zLimit = uint.Max(depth, layerCount);
                var itemIndex = 0;
                VkBufferCopy[] copyRegions;
                if (!FormatHelpers.IsCompressedFormat(srcTex.Format))
                {
                    // uncompressed format, emit a copy for regions according to each height value
                    copyRegions = ArrayPool<VkBufferCopy>.Shared.Rent((int)(zLimit * height));
                    var pixelSize = FormatSizeHelpers.GetSizeInBytes(srcTex.Format);
                    for (var zz = 0u; zz < zLimit; zz++)
                    {
                        for (var yy = 0u; yy < height; yy++)
                        {
                            copyRegions[itemIndex++] = new()
                            {
                                srcOffset = srcLayout.offset
                                    + (srcLayout.depthPitch * (zz + srcZ))
                                    + (srcLayout.rowPitch * (yy + srcY))
                                    + (pixelSize * srcX),
                                dstOffset = dstLayout.offset
                                    + (dstLayout.depthPitch * (zz + dstZ))
                                    + (dstLayout.rowPitch * (yy + dstY))
                                    + (pixelSize * dstX),
                                size = width * pixelSize,
                            };
                        }
                    }
                }
                else
                {
                    // compressed format, emit a copy for regions according to compressed rows
                    var denseRowSize = FormatHelpers.GetRowPitch(width, source.Format);
                    var numRows = FormatHelpers.GetNumRows(height, source.Format);
                    var compressedSrcX = srcX / 4;
                    var compressedSrcY = srcY / 4;
                    var compressedDstX = dstX / 4;
                    var compressedDstY = dstY / 4;
                    var blockSizeInBytes = FormatHelpers.GetBlockSizeInBytes(source.Format);

                    copyRegions = ArrayPool<VkBufferCopy>.Shared.Rent((int)(zLimit * numRows));
                    for (var zz = 0u; zz < zLimit; zz++)
                    {
                        for (var row = 0u; row < numRows; row++)
                        {
                            copyRegions[itemIndex++] = new()
                            {
                                srcOffset = srcLayout.offset
                                    + (srcLayout.depthPitch * (zz + srcZ))
                                    + (srcLayout.rowPitch * (row + compressedSrcY))
                                    + (blockSizeInBytes * compressedSrcX),
                                dstOffset = dstLayout.offset
                                    + (dstLayout.depthPitch * (zz + dstZ))
                                    + (dstLayout.rowPitch * (row + compressedDstY))
                                    + (blockSizeInBytes * compressedDstX),
                                size = denseRowSize
                            };
                        }
                    }
                }

                // we now have a set of copy regions, now we just need to submit the copy
                fixed (VkBufferCopy* pCopyRegions = copyRegions)
                {
                    vkCmdCopyBuffer(_currentCb, srcBuf, dstBuf, (uint)itemIndex, pCopyRegions);
                }
                ArrayPool<VkBufferCopy>.Shared.Return(copyRegions);
            }
        }

        private protected override void PushDebugGroupCore(ReadOnlySpan<char> name)
        {
            var vkCmdDebugMarkerBeginEXT = Device.vkCmdDebugMarkerBeginEXT;
            if (vkCmdDebugMarkerBeginEXT is null) return;

            var byteBuffer = (stackalloc byte[256]);
            Util.GetNullTerminatedUtf8(name, ref byteBuffer);
            fixed (byte* utf8Ptr = byteBuffer)
            {
                VkDebugMarkerMarkerInfoEXT markerInfo = new()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_DEBUG_MARKER_MARKER_INFO_EXT,
                    pMarkerName = (sbyte*)utf8Ptr
                };
                vkCmdDebugMarkerBeginEXT(_currentCb, &markerInfo);
            }
        }

        private protected override void PopDebugGroupCore()
        {
            var vkCmdDebugMarkerEndEXT = Device.vkCmdDebugMarkerEndEXT;
            if (vkCmdDebugMarkerEndEXT is null) return;
            vkCmdDebugMarkerEndEXT(_currentCb);
        }

        private protected override void InsertDebugMarkerCore(ReadOnlySpan<char> name)
        {
            var vkCmdDebugMarkerInsertEXT = Device.vkCmdDebugMarkerInsertEXT;
            if (vkCmdDebugMarkerInsertEXT is null) return;

            var byteBuffer = (stackalloc byte[256]);
            Util.GetNullTerminatedUtf8(name, ref byteBuffer);
            fixed (byte* utf8Ptr = byteBuffer)
            {
                VkDebugMarkerMarkerInfoEXT markerInfo = new()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_DEBUG_MARKER_MARKER_INFO_EXT,
                    pMarkerName = (sbyte*)utf8Ptr
                };
                vkCmdDebugMarkerInsertEXT(_currentCb, &markerInfo);
            }
        }


        private void EnsureRenderPass()
        {
            throw new NotImplementedException();
        }

        private bool EnsureNoRenderPass()
        {
            if (!_framebufferRenderPassInstanceActive)
            {
                // no render pass is actually active, nothing to do
                return false;
            }

            throw new NotImplementedException();
        }

        private protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
        {
            var clearValue = new VkClearValue()
            {
                color = Unsafe.BitCast<RgbaFloat, VkClearColorValue>(clearColor)
            };

            if (!_framebufferRenderPassInstanceActive)
            {
                // We don't yet have a render pass, queue up the clear for the next render pass.
                _clearValues[index] = clearValue;
                _validClearValues[index] = true;
            }
            else
            {
                // We have a render pass, so we need to emit a vkCmdClearAttachments call.
                //
                // The synchronization here is strange though; We normally can't emit synchronization
                // calls within a render pass, because they're subject to some very strict rules.
                // For this case specifically, however, because we're targeting a color target of
                // the current render pass, it *should* be safe to write-sync and immediately enqueue
                // the resulting buffer.

                var tex = Util.AssertSubtype<Texture, VulkanTexture>(_currentFramebuffer!.ColorTargets[(int)index].Target);

                if (SyncResource(tex, new()
                    {
                        BarrierMasks = new()
                        {
                            // CmdClearAttachments operats as COLOR_ATTACHMENT_OUTPUT (for color attachments)
                            StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                            AccessMask = VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                        }
                    }))
                {
                    // a barrier was necessary, flush immediately
                    EmitQueuedSynchro();
                }

                // now that we've set up the synchro, emit the call
                var clearAttachment = new VkClearAttachment()
                {
                    colorAttachment = index,
                    aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                    clearValue = clearValue,
                };
                var clearRect = new VkClearRect()
                {
                    baseArrayLayer = 0,
                    layerCount = 1,
                    rect = new()
                    {
                        offset = new(),
                        extent = new VkExtent2D() { width = tex.Width, height = tex.Height },
                    }
                };
                // TODO: maybe we should try to batch these?
                vkCmdClearAttachments(_currentCb, 1, &clearAttachment, 1, &clearRect);
            }
        }

        private protected override void ClearDepthStencilCore(float depth, byte stencil)
        {
            var clearValue = new VkClearValue()
            {
                depthStencil = new VkClearDepthStencilValue()
                {
                    depth = depth,
                    stencil = stencil
                }
            };

            if (!_framebufferRenderPassInstanceActive)
            {
                // no render pass is active, queue it up for when we start it
                _depthClearValue = clearValue;
            }
            else
            {
                // A render pass is currently active. All of the same caveats apply as in ClearColorTargetCore.
                var renderableExtent = _currentFramebuffer!.RenderableExtent;
                if (renderableExtent.width > 0 && renderableExtent.height > 0)
                {
                    var tex = Util.AssertSubtype<Texture, VulkanTexture>(_currentFramebuffer!.DepthTarget!.Value.Target);

                    if (SyncResource(tex, new()
                    {
                        BarrierMasks = new()
                        {
                            // CmdClearAttachments operats as EARLY_FRAGMENT_TESTS and LATE_FRAGMENT_TESTS (for depth and stencil attachments)
                            StageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT
                                        | VkPipelineStageFlags.VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT,
                            AccessMask = VkAccessFlags.VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT,
                        }
                    }))
                    {
                        // a barrier was necessary, flush immediately
                        EmitQueuedSynchro();
                    }

                    var aspect = FormatHelpers.IsStencilFormat(tex.Format)
                        ? VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT | VkImageAspectFlags.VK_IMAGE_ASPECT_STENCIL_BIT
                        : VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT;

                    var clearAttachment = new VkClearAttachment()
                    {
                        aspectMask = aspect,
                        clearValue = clearValue
                    };
                    var clearRect = new VkClearRect()
                    {
                        baseArrayLayer = 0,
                        layerCount = 1,
                        rect = new()
                        {
                            offset = new(),
                            extent = renderableExtent,
                        }
                    };
                    vkCmdClearAttachments(_currentCb, 1, &clearAttachment, 1, &clearRect);
                }
            }
        }


        protected override void SetFramebufferCore(Framebuffer fb)
        {
            var vkFbb = Util.AssertSubtype<Framebuffer, VulkanFramebufferBase>(fb);
            _currentStagingInfo.AddRefCount(vkFbb.RefCount);

            // the framebuffer we actually want to bind to is the real "current framebuffer"
            // For user-created framebufferws, it will be exactly the one passed in.
            // For swapchain framebuffers, it will be the active buffer from the swapchain.
            var vkFb = vkFbb.CurrentFramebuffer;
            _currentStagingInfo.AddRefCount(vkFb.RefCount);

            // finish any active render pass
            EnsureNoRenderPass();

            // then set up the new target framebuffer
            _currentFramebuffer = vkFb;
            _currentFramebufferEverActive = false;

            _viewportCount = uint.Max(1, (uint)vkFb.ColorTargets.Length);
            Util.EnsureArrayMinimumSize(ref _viewports, _viewportCount);
            Util.EnsureArrayMinimumSize(ref _scissorRects, _viewportCount);
            Util.ClearArray(_viewports);
            Util.ClearArray(_scissorRects);
            _viewportsChanged = false;
            _scissorRectsChanged = false;

            var clearValueCount = (uint)vkFb.ColorTargets.Length;
            Util.EnsureArrayMinimumSize(ref _clearValues, clearValueCount);
            Util.EnsureArrayMinimumSize(ref _validClearValues, clearValueCount);
            Util.ClearArray(_clearValues);
            Util.ClearArray(_validClearValues);
            _depthClearValue = null;
        }

        public override void SetViewport(uint index, in Viewport viewport)
        {
            var yInverted = Device.IsClipSpaceYInverted;
            var vpY = yInverted
                ? viewport.Y
                : viewport.Height + viewport.Y;
            var vpHeight = yInverted
                ? viewport.Height
                : -viewport.Height;

            _viewportsChanged = true;
            _viewports[index] = new VkViewport()
            {
                x = viewport.X,
                y = vpY,
                width = viewport.Width,
                height = vpHeight,
                minDepth = viewport.MinDepth,
                maxDepth = viewport.MaxDepth
            };
        }

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            var  scissor = new VkRect2D()
            {
                offset = new VkOffset2D() { x = (int)x, y = (int)y },
                extent = new VkExtent2D() { width = width, height = height }
            };

            var scissorRects = _scissorRects;
            if (scissorRects[index].offset.x != scissor.offset.x ||
                scissorRects[index].offset.y != scissor.offset.y ||
                scissorRects[index].extent.width != scissor.extent.width ||
                scissorRects[index].extent.height != scissor.extent.height)
            {
                _scissorRectsChanged = true;
                scissorRects[index] = scissor;
            }
        }

        private protected override void SetPipelineCore(Pipeline pipeline)
        {
            var vkPipeline = Util.AssertSubtype<Pipeline, VulkanPipeline>(pipeline);
            _currentStagingInfo.AddResource(vkPipeline);

            if (!vkPipeline.IsComputePipeline)
            {
                // this is a graphics pipeline
                if (_currentGraphicsPipeline == vkPipeline) return; // no work to do

                // the graphics pipeline changed, resize everything and bind the pipeline
                var resourceCount = vkPipeline.ResourceSetCount;
                Util.EnsureArrayMinimumSize(ref _currentGraphicsResourceSets, resourceCount);
                Util.EnsureArrayMinimumSize(ref _graphicsResourceSetsChanged, resourceCount);
                ClearSets(_currentGraphicsResourceSets);

                var vertexBufferCount = vkPipeline.VertexLayoutCount;
                Util.EnsureArrayMinimumSize(ref _vertexBindings, vertexBufferCount);
                Util.EnsureArrayMinimumSize(ref _vertexOffsets, vertexBufferCount);

                vkCmdBindPipeline(_currentCb, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_GRAPHICS, vkPipeline.DevicePipeline);
                _currentGraphicsPipeline = vkPipeline;
            }
            else
            {
                // this is a compute pipeline
                if (_currentComputePipeline == vkPipeline) return; // no work to do

                // the compute pipeline changed, resize everything and bind it
                var resourceCount = vkPipeline.ResourceSetCount;
                Util.EnsureArrayMinimumSize(ref _currentComputeResourceSets, resourceCount);
                Util.EnsureArrayMinimumSize(ref _computeResourceSetsChanged, resourceCount);
                ClearSets(_currentComputeResourceSets);

                vkCmdBindPipeline(_currentCb, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_COMPUTE, vkPipeline.DevicePipeline);
                _currentComputePipeline = vkPipeline;
            }
        }

        private protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer, uint offset)
        {
            var vkBuffer = Util.AssertSubtype<DeviceBuffer, VulkanBuffer>(buffer);

            var bufferChanged = _vertexBindings[index] != vkBuffer.DeviceBuffer;
            if (bufferChanged || _vertexOffsets[index] != offset)
            {
                _vertexBindingsChanged = true;
                if (bufferChanged)
                {
                    _currentStagingInfo.AddResource(vkBuffer);
                    _vertexBindings[index] = vkBuffer.DeviceBuffer;
                }

                _vertexOffsets[index] = offset;
                _numVertexBindings = uint.Max(index + 1, _numVertexBindings);
            }
        }

        private protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format, uint offset)
        {
            var vkBuffer = Util.AssertSubtype<DeviceBuffer, VulkanBuffer>(buffer);
            _currentStagingInfo.AddResource(vkBuffer);

            vkCmdBindIndexBuffer(_currentCb, vkBuffer.DeviceBuffer, offset, Vulkan.VkFormats.VdToVkIndexFormat(format));
        }

        protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs, ReadOnlySpan<uint> dynamicOffsets)
        {
            ref BoundResourceSetInfo set = ref _currentGraphicsResourceSets[slot];
            if (!set.Equals(rs, dynamicOffsets))
            {
                set.Offsets.Dispose();
                set = new BoundResourceSetInfo(rs, dynamicOffsets);
                _graphicsResourceSetsChanged[slot] = true;
                Util.AssertSubtype<ResourceSet, VulkanResourceSet>(rs);
            }
        }

        protected override void SetComputeResourceSetCore(uint slot, ResourceSet rs, ReadOnlySpan<uint> dynamicOffsets)
        {
            ref BoundResourceSetInfo set = ref _currentComputeResourceSets[slot];
            if (!set.Equals(rs, dynamicOffsets))
            {
                set.Offsets.Dispose();
                set = new BoundResourceSetInfo(rs, dynamicOffsets);
                _computeResourceSetsChanged[slot] = true;
                Util.AssertSubtype<ResourceSet, VulkanResourceSet>(rs);
            }
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

        private protected override void GenerateMipmapsCore(Texture texture)
        {
            throw new NotImplementedException();
        }

        // TODO: implement all other members
    }
}
