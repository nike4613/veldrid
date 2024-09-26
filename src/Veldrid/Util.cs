﻿using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Veldrid
{
    internal static class Util
    {
        public static Encoding UTF8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        [DebuggerNonUserCode]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TDerived AssertSubtype<TBase, TDerived>(TBase? value) where TDerived : class, TBase where TBase : class
        {
#if DEBUG
            if (value is not TDerived derived)
            {
                throw new VeldridException($"object {value} must be derived type {typeof(TDerived).FullName} to be used in this context.");
            }

            return derived;

#else
            return (TDerived)value;
#endif
        }

        [DebuggerNonUserCode]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [return: NotNullIfNotNull(nameof(value))]
        internal static TDerived? AssertSubtypeOrNull<TBase, TDerived>(TBase? value)
            where TBase : class
            where TDerived : class, TBase
        {
#if DEBUG
            if (value is not null and not TDerived)
            {
                throw new VeldridException($"object {value} must be null or derived type {typeof(TDerived).FullName} to be used in this context.");
            }
#endif
            return (TDerived?)value;
        }

        internal static void EnsureArrayMinimumSize<T>(ref T[] array, uint size)
        {
            if (array == null || array.Length < size)
            {
                Array.Resize(ref array, (int)size);
            }
        }

        internal static unsafe string GetString(byte* stringStart)
        {
            return Marshal.PtrToStringUTF8((IntPtr)stringStart) ?? "";
        }

        internal static unsafe string GetString(sbyte* stringStart)
        {
            return GetString((byte*)stringStart);
        }

        internal static unsafe string GetString(ReadOnlySpan<byte> span)
        {
            int length = span.IndexOf((byte)'\0');
            if (length == -1)
            {
                length = span.Length;
            }
            return Encoding.UTF8.GetString(span.Slice(0, length));
        }
        
        internal static unsafe string GetString(ReadOnlySpan<sbyte> span)
        {
            return GetString(MemoryMarshal.AsBytes(span));
        }

        internal static bool NullableEquals<T>(T? left, T? right) where T : struct, IEquatable<T>
        {
            if (left.HasValue && right.HasValue)
            {
                return left.Value.Equals(right.Value);
            }

            return left.HasValue == right.HasValue;
        }

        internal static bool ArrayEquals<T>(T[]? left, T[]? right) where T : class
        {
            return left.AsSpan().SequenceEqual(right.AsSpan());
        }

        internal static bool ArrayEqualsEquatable<T>(T[]? left, T[]? right) where T : struct, IEquatable<T>
        {
            return left.AsSpan().SequenceEqual(right.AsSpan());
        }

        internal static void ClearArray<T>(T[] array)
        {
            if (array != null)
            {
                Array.Clear(array, 0, array.Length);
            }
        }

        public static uint Clamp(uint value, uint min, uint max)
        {
            if (value <= min)
            {
                return min;
            }
            else if (value >= max)
            {
                return max;
            }
            else
            {
                return value;
            }
        }

        internal static void GetMipLevelAndArrayLayer(Texture tex, uint subresource, out uint mipLevel, out uint arrayLayer)
        {
            arrayLayer = subresource / tex.MipLevels;
            mipLevel = subresource - (arrayLayer * tex.MipLevels);
        }

        internal static void GetMipDimensions(Texture tex, uint mipLevel, out uint width, out uint height, out uint depth)
        {
            width = GetDimension(tex.Width, mipLevel);
            height = GetDimension(tex.Height, mipLevel);
            depth = GetDimension(tex.Depth, mipLevel);
        }

        internal static void GetMipDimensions(Texture tex, uint mipLevel, out uint width, out uint height)
        {
            width = GetDimension(tex.Width, mipLevel);
            height = GetDimension(tex.Height, mipLevel);
        }

        internal static uint GetDimension(uint largestLevelDimension, uint mipLevel)
        {
            uint ret = largestLevelDimension;
            for (uint i = 0; i < mipLevel; i++)
            {
                ret /= 2;
            }

            return Math.Max(1, ret);
        }

        internal static ulong ComputeSubresourceOffset(Texture tex, uint mipLevel, uint arrayLayer)
        {
            Debug.Assert((tex.Usage & TextureUsage.Staging) == TextureUsage.Staging);
            return ComputeArrayLayerOffset(tex, arrayLayer) + ComputeMipOffset(tex, mipLevel);
        }

        internal static uint ComputeMipOffset(Texture tex, uint mipLevel)
        {
            uint blockSize = FormatHelpers.IsCompressedFormat(tex.Format) ? 4u : 1u;
            uint offset = 0;
            for (uint level = 0; level < mipLevel; level++)
            {
                GetMipDimensions(tex, level, out uint mipWidth, out uint mipHeight, out uint mipDepth);
                uint storageWidth = Math.Max(mipWidth, blockSize);
                uint storageHeight = Math.Max(mipHeight, blockSize);
                offset += FormatHelpers.GetRegionSize(storageWidth, storageHeight, mipDepth, tex.Format);
            }

            return offset;
        }

        internal static uint ComputeArrayLayerOffset(Texture tex, uint arrayLayer)
        {
            if (arrayLayer == 0)
            {
                return 0;
            }

            uint blockSize = FormatHelpers.IsCompressedFormat(tex.Format) ? 4u : 1u;
            uint layerPitch = 0;
            for (uint level = 0; level < tex.MipLevels; level++)
            {
                GetMipDimensions(tex, level, out uint mipWidth, out uint mipHeight, out uint mipDepth);
                uint storageWidth = Math.Max(mipWidth, blockSize);
                uint storageHeight = Math.Max(mipHeight, blockSize);
                layerPitch += FormatHelpers.GetRegionSize(storageWidth, storageHeight, mipDepth, tex.Format);
            }

            return layerPitch * arrayLayer;
        }

        public static unsafe void CopyTextureRegion(
            void* src,
            uint srcX, uint srcY, uint srcZ,
            uint srcRowPitch,
            uint srcDepthPitch,
            void* dst,
            uint dstX, uint dstY, uint dstZ,
            uint dstRowPitch,
            uint dstDepthPitch,
            uint width,
            uint height,
            uint depth,
            PixelFormat format)
        {
            uint blockSize = FormatHelpers.IsCompressedFormat(format) ? 4u : 1u;
            uint blockSizeInBytes = blockSize > 1 ? FormatHelpers.GetBlockSizeInBytes(format) : FormatSizeHelpers.GetSizeInBytes(format);
            uint compressedSrcX = srcX / blockSize;
            uint compressedSrcY = srcY / blockSize;
            uint compressedDstX = dstX / blockSize;
            uint compressedDstY = dstY / blockSize;
            uint numRows = FormatHelpers.GetNumRows(height, format);
            uint rowSize = width / blockSize * blockSizeInBytes;

            if (srcRowPitch == dstRowPitch && srcDepthPitch == dstDepthPitch)
            {
                uint totalCopySize = depth * srcDepthPitch;
                Buffer.MemoryCopy(
                    src,
                    dst,
                    totalCopySize,
                    totalCopySize);
            }
            else
            {
                for (uint zz = 0; zz < depth; zz++)
                    for (uint yy = 0; yy < numRows; yy++)
                    {
                        byte* rowCopyDst = (byte*)dst
                            + dstDepthPitch * (zz + dstZ)
                            + dstRowPitch * (yy + compressedDstY)
                            + blockSizeInBytes * compressedDstX;

                        byte* rowCopySrc = (byte*)src
                            + srcDepthPitch * (zz + srcZ)
                            + srcRowPitch * (yy + compressedSrcY)
                            + blockSizeInBytes * compressedSrcX;

                        Unsafe.CopyBlock(rowCopyDst, rowCopySrc, rowSize);
                    }
            }
        }

        internal static T[] ShallowClone<T>(T[]? array)
        {
            if (array == null)
            {
                return Array.Empty<T>();
            }
            return (T[])array.Clone();
        }

        public static DeviceBufferRange GetBufferRange(BindableResource resource, uint additionalOffset)
        {
            if (resource.Kind == BindableResourceKind.DeviceBufferRange)
            {
                DeviceBufferRange range = resource.GetDeviceBufferRange();
                return new DeviceBufferRange(range.Buffer, range.Offset + additionalOffset, range.SizeInBytes);
            }
            else if (resource.Kind == BindableResourceKind.DeviceBuffer)
            {
                DeviceBuffer buffer = resource.GetDeviceBuffer();
                return new DeviceBufferRange(buffer, additionalOffset, buffer.SizeInBytes);
            }
            else
            {
                static DeviceBufferRange Throw(BindableResourceKind resourceKind)
                {
                    throw new VeldridException($"Unexpected resource type used in a buffer type slot: {resourceKind}");
                }
                return Throw(resource.Kind);
            }
        }

        public static bool GetDeviceBuffer(BindableResource resource, [MaybeNullWhen(false)] out DeviceBuffer buffer)
        {
            if (resource.Kind == BindableResourceKind.DeviceBuffer)
            {
                buffer = resource.GetDeviceBuffer();
                return true;
            }
            else if (resource.Kind == BindableResourceKind.DeviceBufferRange)
            {
                buffer = resource.GetDeviceBufferRange().Buffer;
                return true;
            }

            buffer = null;
            return false;
        }

        internal static TextureView GetTextureView(GraphicsDevice gd, BindableResource resource)
        {
            if (resource.Kind == BindableResourceKind.TextureView)
            {
                return resource.GetTextureView();
            }
            else if (resource.Kind == BindableResourceKind.Texture)
            {
                return resource.GetTexture().GetFullTextureView(gd);
            }
            else
            {
                static TextureView Throw(BindableResourceKind resourceKind)
                {
                    throw new VeldridException($"Unexpected resource type used in a texture type slot: {resourceKind}.");
                }
                return Throw(resource.Kind);
            }
        }

        internal static void PackIntPtr(IntPtr sourcePtr, out uint low, out uint high)
        {
            ulong src64 = (ulong)sourcePtr;
            low = (uint)(src64 & 0x00000000FFFFFFFF);
            high = (uint)((src64 & 0xFFFFFFFF00000000u) >> 32);
        }

        internal static IntPtr UnpackIntPtr(uint low, uint high)
        {
            ulong src64 = low | ((ulong)high << 32);
            return (IntPtr)src64;
        }

        internal static int GetNullTerminatedUtf8(ReadOnlySpan<char> text, ref Span<byte> byteBuffer)
        {
            int byteCount = UTF8.GetByteCount(text) + 1;
            if (byteBuffer.Length < byteCount)
                byteBuffer = new byte[byteCount];

            int bytesWritten = UTF8.GetBytes(text, byteBuffer);
            Debug.Assert(bytesWritten == byteCount - 1);

            byteBuffer[byteCount - 1] = 0; // Add null terminator.
            return bytesWritten;
        }
    }
}
