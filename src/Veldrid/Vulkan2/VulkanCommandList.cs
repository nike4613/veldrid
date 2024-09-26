using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TerraFX.Interop.Vulkan;
using IResourceRefCountTarget = Veldrid.Vulkan.IResourceRefCountTarget;
using ResourceRefCount = Veldrid.Vulkan.ResourceRefCount;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal unsafe sealed class VulkanCommandList : CommandList, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _device;
        private readonly VkCommandPool _pool;
        public ResourceRefCount RefCount { get; }

        public override string? Name { get; set; }

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
            _device = device;
            _pool = pool;
            RefCount = new(this);
        }

        public override void Dispose()
        {
            RefCount.DecrementDispose();
        }

        void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyCommandPool(_device.Device, _pool, null);
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
                    VulkanUtil.CheckResult(vkAllocateCommandBuffers(_device.Device, &allocateInfo, pBuffers));
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

        public void SubmitToQueue(VkQueue queue, VulkanFence? submitFence)
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
            // TODO: extra fences to track queues which have been submitted
            {
                var syncToMainSem = _device.GetSemaphore();

                var beginInfo = new VkCommandBufferBeginInfo()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                    flags = VkCommandBufferUsageFlags.VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
                };
                VulkanUtil.CheckResult(vkBeginCommandBuffer(syncCb, &beginInfo));

                EmitInitialResourceSync(syncCb);

                VulkanUtil.CheckResult(vkEndCommandBuffer(syncCb));

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
                        // TODO: optionally signal a semaphore for a 3rd submit so we can signal a second fence (or just use the same fence)
                    },
                ];

                var fence = submitFence is not null ? submitFence.DeviceFence : VkFence.NULL;

                fixed (VkSubmitInfo2* pSubmitInfos = submitInfos)
                {
                    VulkanUtil.CheckResult(_device.vkQueueSubmit2(queue, (uint)submitInfos.Length, pSubmitInfos, fence));
                }
            }

            // TODO: record the associated fence in the GD so that it can notice when the queue is finished

            RefCount.Increment();

            lock (_commandBufferListLock)
            {
                // record that we've submitted this command list, and associate the relevant resources
                // note: we submit default for syncCb because we *also* use this dict as the list of submitted command buffers
                _submittedStagingInfos.Add(cb, resourceInfo);
                _submittedStagingInfos.Add(syncCb, default);
            }
        }

        private void EmitInitialResourceSync(VkCommandBuffer cb)
        {
            // TODO:
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

        // TODO: implement all other members
    }
}
