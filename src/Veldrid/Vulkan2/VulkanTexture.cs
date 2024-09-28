﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TerraFX.Interop.Vulkan;
using VkVersion = Veldrid.Vulkan.VkVersion;
using VulkanUtil = Veldrid.Vulkan.VulkanUtil;
using VkFormats = Veldrid.Vulkan.VkFormats;
using VkDeviceMemoryManager = Veldrid.Vulkan.VkDeviceMemoryManager;
using VkMemoryBlock = Veldrid.Vulkan.VkMemoryBlock;
using IResourceRefCountTarget = Veldrid.Vulkan.IResourceRefCountTarget;
using ResourceRefCount = Veldrid.Vulkan.ResourceRefCount;
using static TerraFX.Interop.Vulkan.VkStructureType;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Veldrid.Vulkan2
{
    internal unsafe sealed class VulkanTexture : Texture, IResourceRefCountTarget, ISynchronizedResource
    {
        private readonly VulkanGraphicsDevice _gd;
        private readonly VkImage _image;
        private readonly VkBuffer _stagingBuffer;
        private readonly VkMemoryBlock _memory;
        private string? _name;
        private readonly uint _actualImageArrayLayers;
        private readonly bool _isSwapchainTexture;
        private readonly bool _leaveOpen;

        public VkFormat VkFormat { get; }
        public VkSampleCountFlags VkSampleCount { get; }

        public ResourceRefCount RefCount { get; }
        public override bool IsDisposed => RefCount.IsDisposed;

        private SyncState _syncState;
        public ref SyncState SyncState => ref _syncState;

        internal VulkanTexture(
            VulkanGraphicsDevice gd, in TextureDescription description,
            VkImage image, VkMemoryBlock memory, VkBuffer stagingBuffer,
            bool isSwapchainTexture, bool leaveOpen)
        {
            _gd = gd;
            _image = image;
            _memory = memory;
            _stagingBuffer = stagingBuffer;
            _isSwapchainTexture = isSwapchainTexture;
            _leaveOpen = leaveOpen;

            Width = description.Width;
            Height = description.Height;
            Depth = description.Depth;
            MipLevels = description.MipLevels;
            ArrayLayers = description.ArrayLayers;
            Format = description.Format;
            Usage = description.Usage;
            Type = description.Type;
            SampleCount = description.SampleCount;

            VkSampleCount = VkFormats.VdToVkSampleCount(description.SampleCount);
            VkFormat = VkFormats.VdToVkPixelFormat(description.Format, description.Usage);

            _actualImageArrayLayers = (description.Usage & TextureUsage.Cubemap) != 0
                ? 6 * description.ArrayLayers
                 : description.ArrayLayers;

            RefCount = new(this);
        }

        private protected override void DisposeCore() => RefCount?.DecrementDispose();

        void IResourceRefCountTarget.RefZeroed()
        {
            if (_leaveOpen)
            {
                return;
            }

            if (_stagingBuffer != VkBuffer.NULL)
            {
                vkDestroyBuffer(_gd.Device, _stagingBuffer, null);
            }

            if (_image != VkImage.NULL)
            {
                vkDestroyImage(_gd.Device, _image, null);
            }

            if (_memory.DeviceMemory != VkDeviceMemory.NULL)
            {
                _gd.MemoryManager.Free(_memory);
            }
        }

        public override string? Name
        {
            get => _name;
            set
            {
                _name = value;
                if (_image != VkImage.NULL)
                {
                    _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_IMAGE_EXT, _image.Value, value);
                }
                if (_stagingBuffer != VkBuffer.NULL)
                {
                    _gd.SetDebugMarkerName(VkDebugReportObjectTypeEXT.VK_DEBUG_REPORT_OBJECT_TYPE_BUFFER_EXT, _stagingBuffer.Value, value);
                }
            }
        }

        internal VkSubresourceLayout GetSubresourceLayout(uint mipLevel, uint arrayLevel)
        {
            VkSubresourceLayout layout;
            bool staging = _stagingBuffer != VkBuffer.NULL;
            if (!staging)
            {
                VkImageAspectFlags aspect = (Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil
                    ? (VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT | VkImageAspectFlags.VK_IMAGE_ASPECT_STENCIL_BIT)
                    : VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT;
                VkImageSubresource imageSubresource = new()
                {
                    arrayLayer = arrayLevel,
                    mipLevel = mipLevel,
                    aspectMask = aspect
                };

                vkGetImageSubresourceLayout(_gd.Device, _image, &imageSubresource, &layout);
            }
            else
            {
                base.GetSubresourceLayout(mipLevel, arrayLevel, out uint rowPitch, out uint depthPitch);

                layout.offset = Util.ComputeSubresourceOffset(this, mipLevel, arrayLevel);
                layout.rowPitch = rowPitch;
                layout.depthPitch = depthPitch;
                layout.arrayPitch = depthPitch;
                layout.size = depthPitch;
            }
            return layout;
        }

        public override uint GetSizeInBytes(uint subresource)
        {
            Util.GetMipLevelAndArrayLayer(this, subresource, out uint mipLevel, out uint arrayLayer);
            var layout = GetSubresourceLayout(mipLevel, arrayLayer);
            return (uint)layout.size;
        }

        internal override void GetSubresourceLayout(uint mipLevel, uint arrayLevel, out uint rowPitch, out uint depthPitch)
        {
            var layout = GetSubresourceLayout(mipLevel, arrayLevel);
            rowPitch = (uint)layout.rowPitch;
            depthPitch = (uint)layout.depthPitch;
        }

        private protected override TextureView CreateFullTextureView(GraphicsDevice gd)
        {
            return base.CreateFullTextureView(gd);
        }
    }
}