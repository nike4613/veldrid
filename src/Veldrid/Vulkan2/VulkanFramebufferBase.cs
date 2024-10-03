using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using VkFormats = Veldrid.Vulkan.VkFormats;
using VkMemoryBlock = Veldrid.Vulkan.VkMemoryBlock;
using VkDeviceMemoryManager = Veldrid.Vulkan.VkDeviceMemoryManager;
using ResourceRefCount = Veldrid.Vulkan.ResourceRefCount;
using IResourceRefCountTarget = Veldrid.Vulkan.IResourceRefCountTarget;
using static TerraFX.Interop.Vulkan.VkStructureType;
using static TerraFX.Interop.Vulkan.Vulkan;


namespace Veldrid.Vulkan2
{
    internal abstract class VulkanFramebufferBase : Framebuffer
    {
        internal VulkanFramebufferBase()
        {
        }

        internal VulkanFramebufferBase(
            FramebufferAttachmentDescription? depthTargetDesc,
            ReadOnlySpan<FramebufferAttachmentDescription> colorTargetDescs)
            : base(depthTargetDesc, colorTargetDescs)
        {
        }

        // note: this is abstract so that derived types have to initialize it last, making sure that
        // native resources are correctly cleaned up in all cases
        public abstract ResourceRefCount RefCount { get; }

        public abstract VulkanFramebuffer CurrentFramebuffer { get; }

        public sealed override bool IsDisposed => RefCount.IsDisposed;
        public sealed override void Dispose() => RefCount?.DecrementDispose();

        public uint AttachmentCount { get; protected set; }
        public FramebufferAttachment[] ColorTargetsArray => _colorTargets;
    }
}
