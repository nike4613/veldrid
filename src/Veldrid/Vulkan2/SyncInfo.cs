﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TerraFX.Interop.Vulkan;

namespace Veldrid.Vulkan2
{
    internal struct SyncBarrierMasks
    {
        public VkAccessFlags2 AccessMask;
        public VkPipelineStageFlags2 StageMask;
    }

    internal struct SyncRequest
    {
        public SyncBarrierMasks BarrierMasks;
        // requested layout (for images only)
        public VkImageLayout Layout;
    }

    internal struct SyncState
    {
        public SyncBarrierMasks LastWriter;
        // Bitfield marking which accesses in which shader stages stages have seen the last write
        public uint PerStageReaders; // TODO: turn this into a concrete layout
        public VkImageLayout CurrentLayout;
    }

    internal struct ResourceSyncInfo
    {
        public SyncRequest Expected;
        public SyncState LocalState;
    }
}