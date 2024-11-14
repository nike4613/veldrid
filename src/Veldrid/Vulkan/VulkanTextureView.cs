using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan
{
    internal sealed unsafe class VulkanTextureView : TextureView, IResourceRefCountTarget
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkImageView _imageView;
        private string? _name;

        public VkImageView ImageView => _imageView;
        public new VulkanTexture Target => (VulkanTexture)base.Target;

        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        public uint RealArrayLayers
            => (Target.Usage & TextureUsage.Cubemap) != 0 ? ArrayLayers * 6 : ArrayLayers;

        internal VulkanTextureView(VulkanGraphicsDevice gd, in TextureViewDescription description, VkImageView imageView) : base(description)
        {
            _gd = gd;
            _imageView = imageView;

            Target.RefCount.Increment();
            RefCount = new(this);
        }

        public override void Dispose() => RefCount?.DecrementDispose();
        void IResourceRefCountTarget.RefZeroed()
        {
            if (_imageView != VkImageView.NULL)
            {
                vkDestroyImageView(_gd.Device, _imageView, null);
            }

            Target.RefCount.Decrement();
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_IMAGE_VIEW_EXT, _imageView.Value, value);
            }
        }
    }
}
