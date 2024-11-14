using System;


namespace Veldrid.Vulkan
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
        public override void Dispose() => RefCount?.DecrementDispose();

        public uint AttachmentCount { get; protected set; }
        public FramebufferAttachment[] ColorTargetsArray => _colorTargets;
    }
}
