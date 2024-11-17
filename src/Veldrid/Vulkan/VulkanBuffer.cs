﻿using System;
using System.Diagnostics;

using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan
{
    internal unsafe sealed class VulkanBuffer : DeviceBuffer, IResourceRefCountTarget, ISynchronizedResource
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkBuffer _buffer;
        private readonly VkMemoryBlock _memory;

        private SyncState _syncState;
        private string? _name;

        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        public Span<SyncState> AllSyncStates => new(ref _syncState);
        SyncSubresource ISynchronizedResource.SubresourceCounts => new(1, 1);
        ref SyncState ISynchronizedResource.SyncStateForSubresource(SyncSubresource subresource)
        {
            Debug.Assert(subresource == default);
            return ref _syncState;
        }

        public VkBuffer DeviceBuffer => _buffer;
        public ref readonly VkMemoryBlock Memory => ref _memory;

        internal VulkanBuffer(VulkanGraphicsDevice gd, in BufferDescription bd, VkBuffer buffer, VkMemoryBlock memory) : base(bd)
        {
            _gd = gd;
            _buffer = buffer;
            _memory = memory;
            RefCount = new(this);
        }

        public override void Dispose() => RefCount?.DecrementDispose();

        void IResourceRefCountTarget.RefZeroed()
        {
            vkDestroyBuffer(_gd.Device, _buffer, null);
            _gd.MemoryManager.Free(_memory);
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_BUFFER_EXT, _buffer.Value, value);
            }
        }
    }
}