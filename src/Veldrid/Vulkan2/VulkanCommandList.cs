using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Buffers;

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

            public void AddResource(ISynchronizedResource resource)
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
            Action<VulkanCommandList>? OnSubmitCompleted,
            bool FenceWasRented
            );

        public VkSemaphore SubmitToQueue(VkQueue queue, VulkanFence? submitFence, Action<VulkanCommandList>? onSubmitCompleted, VkPipelineStageFlags2 completionSemaphoreStages)
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
            Device.RegisterFenceCompletionCallback(fence, new(this, cb, syncCb, syncToMainSem, mainCompleteSem, onSubmitCompleted, fenceWasRented));

            lock (_commandBufferListLock)
            {
                // record that we've submitted this command list, and associate the relevant resources
                // note: we don't need to add the syncCb here, because it's part of the FenceCompletionCallbackInfo registered above
                _submittedStagingInfos.Add(cb, resourceInfo);
            }

            return mainCompleteSem;
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

                // return the semaphores
                Device.ReturnSemaphore(callbackInfo.SyncSemaphore);
                if (callbackInfo.MainCompleteSemaphore != VkSemaphore.NULL)
                {
                    Device.ReturnSemaphore(callbackInfo.MainCompleteSemaphore);
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
                        result |= 1u << (BitsPerStage * stageIndex + accessIndex);
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

            var needsLayoutTransition = req.Layout != state.CurrentImageLayout;
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

        public void SyncResource(ISynchronizedResource resource, in SyncRequest req)
        {
            ref var localSyncInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(_resourceSyncInfo, resource, out var exists);
            if (!exists)
            {
                localSyncInfo = new();
            }

            if (SyncResourceToState(ref localSyncInfo.LocalState, resource, req))
            {
                localSyncInfo.HasBarrier = true;
            }

            // mark the expected state appropriately
            if (!localSyncInfo.HasBarrier)
            {
                localSyncInfo.Expected.BarrierMasks.StageMask |= req.BarrierMasks.StageMask;
                localSyncInfo.Expected.BarrierMasks.AccessMask |= req.BarrierMasks.AccessMask;
                if (localSyncInfo.Expected.Layout == 0)
                {
                    localSyncInfo.Expected.Layout = req.Layout;
                }
            }
        }

        private bool SyncResourceToState(ref SyncState state, ISynchronizedResource resource, in SyncRequest req)
        {
            if (TryBuildSyncBarrier(ref state, in req, out var barrier))
            {
                // a barrier is needed, mark appropriately
                _pendingBarriers.Add((resource, barrier));

                if (resource is VulkanTexture vkTex && vkTex.DeviceImage != VkImage.NULL)
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
                                sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2,
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

            EmitQueuedSynchro(); // at the end of the CL, any queued synchronization needs to be emitted so we don't miss any

            _bufferBegun = false;
            _bufferEnded = true;

            // TODO: finish render passes

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
