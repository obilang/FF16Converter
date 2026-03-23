using AvaloniaToolbox.Core;
using AvaloniaToolbox.Core.Textures;
using FinalFantasy16;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FF16Converter
{
    public class TextureDataUtil
    {
        public const uint D3D12_TEXTURE_DATA_PITCH_ALIGNMENT = 256;
        public const uint D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT = 512;

        internal static uint Align(uint value, uint alignment)
        {
            return (value + (alignment - 1)) & ~(alignment - 1);
        }

        /// <summary>
        /// Gives the output mip dimension given the base size and level.
        /// </summary>
        public static int CalculateMipDimension(uint baseLevelDimension, int mipLevel)
        {
            return Math.Max((int)baseLevelDimension / (int)Math.Pow(2, mipLevel), 1);
        }

        /// <summary>
        /// Returns the aligned byte size of a single mip level for ONE array slice.
        /// Uses D3D12 row pitch alignment (256 bytes) only - no additional placement alignment.
        /// </summary>
        public static int CalculateAlignedMipSize(TexFile.Texture tex, int mipLevel)
        {
            var mipWidth  = CalculateMipDimension(tex.Width,  mipLevel);
            var mipHeight = CalculateMipDimension(tex.Height, mipLevel);
            CalculateFormatSize(tex.Format, mipWidth, mipHeight, out _, out int slice, out int alignedSlice);
            return alignedSlice;
        }

        /// <summary>
        /// Returns the unaligned byte size of a single mip level for ONE array slice.
        /// </summary>
        public static int CalculateUnalignedMipSize(TexFile.Texture tex, int mipLevel)
        {
            var mipWidth  = CalculateMipDimension(tex.Width,  mipLevel);
            var mipHeight = CalculateMipDimension(tex.Height, mipLevel);
            CalculateFormatSize(tex.Format, mipWidth, mipHeight, out _, out int slice, out int alignedSlice);
            return slice;
        }

        /// <summary>
        /// Returns the raw byte size of a single array slice (all mips) without alignment.
        /// </summary>
        public static int CalculateSliceSize(TexFile.Texture tex)
        {
            int total = 0;
            for (int mipLevel = 0; mipLevel < tex.MipCount; mipLevel++)
                total += CalculateUnalignedMipSize(tex, mipLevel);
            return total;
        }

        /// <summary>
        /// Calculates the full texture surface data with alignment, in slice-major order.
        /// FF16 Texture2DArray on-disk layout:
        ///   [slice0_mip0_aligned][slice0_mip1_aligned]...[slice1_mip0_aligned][slice1_mip1_aligned]...
        /// For non-arrays: [mip0_aligned][mip1_aligned]...
        /// This function is a pass-through; the raw chunk data is already in the correct aligned layout.
        /// </summary>
        public static byte[] CalculateSurfacePadding(TexFile.Texture tex, byte[] data, int arrayCount = 1)
        {
            return data;
        }

        /// <summary>
        /// Aligns image data from slice-major unaligned input to slice-major aligned output.
        /// Returns one byte[] per subresource: [slice0_mip0, slice0_mip1, ..., slice1_mip0, ...]
        /// </summary>
        public static List<byte[]> GetAlignedData(TexFile.Texture tex, byte[] data, int arrayCount = 1)
        {
            var formatDecoder = TexFile.FormatList[(int)tex.Format];
            if (formatDecoder is Bcn)
                return GetCompressedAlignedData(tex, data, arrayCount);
            else
                return GetUncompressedAlignedData(tex, data, arrayCount);
        }

        /// <summary>
        /// Removes D3D12 row pitch alignment from image data.
        /// On-disk layout is SLICE-MAJOR:
        ///   [slice0_mip0_aligned][slice0_mip1_aligned]...[slice1_mip0_aligned][slice1_mip1_aligned]...
        /// Returns unaligned data in the same SLICE-MAJOR order.
        /// </summary>
        public static byte[] GetUnalignedData(TexFile.Texture tex, byte[] data, int arrayCount = 1)
        {
            var formatDecoder = TexFile.FormatList[(int)tex.Format];
            if (formatDecoder is Bcn)
                return GetCompressedUnalignedData(tex, data, arrayCount);
            else
                return GetUncompressedUnalignedData(tex, data, arrayCount);
        }

        // -----------------------------------------------------------------------
        //  Unaligned extraction — SLICE-MAJOR layout
        //  Input:  [slice0_mip0_aligned][slice0_mip1_aligned]...[slice1_mip0_aligned]...
        //  Output: [slice0_mip0][slice0_mip1]...[slice1_mip0]... (row padding stripped)
        // -----------------------------------------------------------------------

        private static byte[] GetCompressedUnalignedData(TexFile.Texture tex, byte[] data, int arrayCount)
        {
            var formatDecoder = (Bcn)TexFile.FormatList[(int)tex.Format];
            bool isSingle = formatDecoder.Format == BcnFormats.BC1 || formatDecoder.Format == BcnFormats.BC4;
            int blockSize  = isSingle ? 8 : 16;

            // Pre-compute aligned mip sizes for one slice
            int[] alignedMipSizes = new int[tex.MipCount];
            for (int m = 0; m < tex.MipCount; m++)
            {
                var mw = CalculateMipDimension(tex.Width,  m);
                var mh = CalculateMipDimension(tex.Height, m);
                int bW = (mw + 3) / 4;
                int bH = (mh + 3) / 4;
                int row = bW * blockSize;
                int alignedRow = (int)Align((uint)row, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
                alignedMipSizes[m] = (int)Align((uint)(alignedRow * bH), D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
            }

            var mem = new MemoryStream();
            using (var wr = new BinaryWriter(mem))
            {
                int dataOffset = 0;

                for (int arrayIndex = 0; arrayIndex < arrayCount; arrayIndex++)
                {
                    for (int mipLevel = 0; mipLevel < tex.MipCount; mipLevel++)
                    {
                        var mipWidth  = CalculateMipDimension(tex.Width,  mipLevel);
                        var mipHeight = CalculateMipDimension(tex.Height, mipLevel);
                        int blocksW   = (mipWidth  + 3) / 4;
                        int blocksH   = (mipHeight + 3) / 4;
                        int origRow   = blocksW * blockSize;
                        int alignedRow = (int)Align((uint)origRow, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);

                        for (int row = 0; row < blocksH; row++)
                        {
                            int rowStart = dataOffset + row * alignedRow;
                            wr.Write(data, rowStart, origRow);
                        }

                        dataOffset += alignedMipSizes[mipLevel];
                    }
                }
            }
            return mem.ToArray();
        }

        private static byte[] GetUncompressedUnalignedData(TexFile.Texture tex, byte[] data, int arrayCount)
        {
            var formatDecoder  = (Rgba)TexFile.FormatList[(int)tex.Format];
            var bitsPerPixel   = (uint)(formatDecoder.R + formatDecoder.G + formatDecoder.B + formatDecoder.A);
            int bytesPerPixel  = (int)(bitsPerPixel + 7) / 8;

            // Pre-compute aligned mip sizes for one slice
            int[] alignedMipSizes = new int[tex.MipCount];
            for (int m = 0; m < tex.MipCount; m++)
            {
                var mw = CalculateMipDimension(tex.Width,  m);
                var mh = CalculateMipDimension(tex.Height, m);
                int row = mw * bytesPerPixel;
                int alignedRow = (int)Align((uint)row, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
                alignedMipSizes[m] = (int)Align((uint)(alignedRow * mh), D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
            }

            var mem = new MemoryStream();
            using (var wr = new BinaryWriter(mem))
            {
                int dataOffset = 0;

                for (int arrayIndex = 0; arrayIndex < arrayCount; arrayIndex++)
                {
                    for (int mipLevel = 0; mipLevel < tex.MipCount; mipLevel++)
                    {
                        var mipWidth  = CalculateMipDimension(tex.Width,  mipLevel);
                        var mipHeight = CalculateMipDimension(tex.Height, mipLevel);
                        int origRow   = mipWidth * bytesPerPixel;
                        int alignedRow = (int)Align((uint)origRow, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);

                        for (int row = 0; row < mipHeight; row++)
                        {
                            int rowStart = dataOffset + row * alignedRow;
                            wr.Write(data, rowStart, origRow);
                        }

                        dataOffset += alignedMipSizes[mipLevel];
                    }
                }
            }
            return mem.ToArray();
        }

        // -----------------------------------------------------------------------
        //  Aligned data creation — SLICE-MAJOR layout
        //  Input:  unaligned slice-major [slice0_mip0][slice0_mip1]...[slice1_mip0]...
        //  Output: one byte[] per subresource (slice-major order) with row padding added
        // -----------------------------------------------------------------------

        private static List<byte[]> GetCompressedAlignedData(TexFile.Texture tex, byte[] data, int arrayCount)
        {
            var formatDecoder = (Bcn)TexFile.FormatList[(int)tex.Format];
            bool isSingle = formatDecoder.Format == BcnFormats.BC1 || formatDecoder.Format == BcnFormats.BC4;
            int blockSize  = isSingle ? 8 : 16;

            int[] unalignedMipSizes = new int[tex.MipCount];
            for (int i = 0; i < tex.MipCount; i++)
            {
                var w = CalculateMipDimension(tex.Width,  i);
                var h = CalculateMipDimension(tex.Height, i);
                int bW = (w + 3) / 4;
                int bH = (h + 3) / 4;
                unalignedMipSizes[i] = bW * blockSize * bH;
            }

            List<byte[]> result = new List<byte[]>();
            int dataOffset = 0;

            for (int arrayIndex = 0; arrayIndex < arrayCount; arrayIndex++)
            {
                for (int mipLevel = 0; mipLevel < tex.MipCount; mipLevel++)
                {
                    var mipWidth   = CalculateMipDimension(tex.Width,  mipLevel);
                    var mipHeight  = CalculateMipDimension(tex.Height, mipLevel);
                    int blocksW    = (mipWidth  + 3) / 4;
                    int blocksH    = (mipHeight + 3) / 4;
                    int origRow    = blocksW * blockSize;
                    int alignedRow = (int)Align((uint)origRow, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
                    int alignedMipSize = (int)Align((uint)(alignedRow * blocksH), D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
                    int unalignedMip = unalignedMipSizes[mipLevel];

                    byte[] alignedData = new byte[alignedMipSize];
                    for (int row = 0; row < blocksH; row++)
                    {
                        int src = dataOffset + row * origRow;
                        int dst = row * alignedRow;
                        Array.Copy(data, src, alignedData, dst, origRow);
                    }
                    result.Add(alignedData);

                    dataOffset += unalignedMip;
                }
            }
            return result;
        }

        private static List<byte[]> GetUncompressedAlignedData(TexFile.Texture tex, byte[] data, int arrayCount)
        {
            var formatDecoder  = (Rgba)TexFile.FormatList[(int)tex.Format];
            var bitsPerPixel   = (uint)(formatDecoder.R + formatDecoder.G + formatDecoder.B + formatDecoder.A);
            int bytesPerPixel  = (int)(bitsPerPixel + 7) / 8;

            List<byte[]> result = new List<byte[]>();
            int dataOffset = 0;

            for (int arrayIndex = 0; arrayIndex < arrayCount; arrayIndex++)
            {
                for (int mipLevel = 0; mipLevel < tex.MipCount; mipLevel++)
                {
                    var mipWidth   = CalculateMipDimension(tex.Width,  mipLevel);
                    var mipHeight  = CalculateMipDimension(tex.Height, mipLevel);
                    int origRow    = mipWidth * bytesPerPixel;
                    int alignedRow = (int)Align((uint)origRow, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
                    int alignedMipSize = (int)Align((uint)(alignedRow * mipHeight), D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
                    int unalignedMip = origRow * mipHeight;

                    byte[] alignedData = new byte[alignedMipSize];
                    for (int row = 0; row < mipHeight; row++)
                    {
                        int src = dataOffset + row * origRow;
                        int dst = row * alignedRow;
                        Array.Copy(data, src, alignedData, dst, origRow);
                    }
                    result.Add(alignedData);

                    dataOffset += unalignedMip;
                }
            }
            return result;
        }

        // -----------------------------------------------------------------------

        /// <summary>
        /// Converts unaligned subresource data from mip-major order
        ///   [mip0_slice0][mip0_slice1]...[mip1_slice0][mip1_slice1]...
        /// to slice-major (DDS) order
        ///   [slice0_mip0][slice0_mip1]...[slice1_mip0][slice1_mip1]...
        /// </summary>
        public static byte[] MipMajorToSliceMajor(TexFile.Texture tex, byte[] data, int arrayCount)
        {
            if (arrayCount <= 1 || tex.MipCount <= 1)
                return data;

            // Pre-compute unaligned mip sizes for one slice
            int[] mipSizes = new int[tex.MipCount];
            for (int m = 0; m < tex.MipCount; m++)
                mipSizes[m] = CalculateUnalignedMipSize(tex, m);

            byte[] result = new byte[data.Length];

            // Build source offset table: mip-major
            // srcOffsets[mip][slice] = offset in 'data'
            int srcOff = 0;
            int[][] srcOffsets = new int[tex.MipCount][];
            for (int m = 0; m < tex.MipCount; m++)
            {
                srcOffsets[m] = new int[arrayCount];
                for (int s = 0; s < arrayCount; s++)
                {
                    srcOffsets[m][s] = srcOff;
                    srcOff += mipSizes[m];
                }
            }

            // Write in slice-major order
            int dstOff = 0;
            for (int s = 0; s < arrayCount; s++)
            {
                for (int m = 0; m < tex.MipCount; m++)
                {
                    int size = mipSizes[m];
                    int copyLen = Math.Min(size, data.Length - srcOffsets[m][s]);
                    if (copyLen > 0)
                        Array.Copy(data, srcOffsets[m][s], result, dstOff, copyLen);
                    dstOff += size;
                }
            }

            return result;
        }

        /// <summary>
        /// Converts unaligned subresource data from slice-major (DDS) order
        ///   [slice0_mip0][slice0_mip1]...[slice1_mip0][slice1_mip1]...
        /// to mip-major order
        ///   [mip0_slice0][mip0_slice1]...[mip1_slice0][mip1_slice1]...
        /// </summary>
        public static byte[] SliceMajorToMipMajor(TexFile.Texture tex, byte[] data, int arrayCount)
        {
            if (arrayCount <= 1 || tex.MipCount <= 1)
                return data;

            // Pre-compute unaligned mip sizes for one slice
            int[] mipSizes = new int[tex.MipCount];
            for (int m = 0; m < tex.MipCount; m++)
                mipSizes[m] = CalculateUnalignedMipSize(tex, m);

            byte[] result = new byte[data.Length];

            // Build source offset table: slice-major
            // srcOffsets[slice][mip] = offset in 'data'
            int srcOff = 0;
            int[][] srcOffsets = new int[arrayCount][];
            for (int s = 0; s < arrayCount; s++)
            {
                srcOffsets[s] = new int[tex.MipCount];
                for (int m = 0; m < tex.MipCount; m++)
                {
                    srcOffsets[s][m] = srcOff;
                    srcOff += mipSizes[m];
                }
            }

            // Write in mip-major order
            int dstOff = 0;
            for (int m = 0; m < tex.MipCount; m++)
            {
                for (int s = 0; s < arrayCount; s++)
                {
                    int size = mipSizes[m];
                    int copyLen = Math.Min(size, data.Length - srcOffsets[s][m]);
                    if (copyLen > 0)
                        Array.Copy(data, srcOffsets[s][m], result, dstOff, copyLen);
                    dstOff += size;
                }
            }

            return result;
        }

        // -----------------------------------------------------------------------

        static int GetBPP(TexFile.TextureFormat format)
        {
            switch (format)
            {
                case TexFile.TextureFormat.R32G32B32A32_FLOAT: return 32;
                case TexFile.TextureFormat.R32G32B32_FLOAT:    return 24;
                case TexFile.TextureFormat.R32G32_FLOAT:       return 16;
                case TexFile.TextureFormat.R8G8_UNORM:
                case TexFile.TextureFormat.R8G8_SNORM:
                case TexFile.TextureFormat.R8G8_SINT:
                case TexFile.TextureFormat.R8G8_UINT:          return 2;
                case TexFile.TextureFormat.R8_UNORM:
                case TexFile.TextureFormat.R8_SNORM:
                case TexFile.TextureFormat.R8_SINT:
                case TexFile.TextureFormat.R8_UINT:            return 1;
                case TexFile.TextureFormat.R32_FLOAT:
                case TexFile.TextureFormat.R8G8B8A8_UNORM:     return 4;
                default:                                        return 4;
            }
        }

        public static void CalculateFormatSize(TexFile.TextureFormat format, int width, int height,
             out int pitch, out int slice, out int alignedSlice)
        {
            switch (format)
            {
                case TexFile.TextureFormat.BC1_UNORM:
                case TexFile.TextureFormat.BC1_UNORM_SRGB:
                case TexFile.TextureFormat.BC4_UNORM:
                case TexFile.TextureFormat.BC4_SNORM:
                    {
                        int blockWidth  = (width  + 3) / 4;
                        int blockHeight = (height + 3) / 4;
                        pitch        = blockWidth * 8;
                        slice        = pitch * blockHeight;
                        alignedSlice = (int)Align((uint)(Align((uint)pitch, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT) * blockHeight), D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
                        break;
                    }
                case TexFile.TextureFormat.BC2_UNORM:
                case TexFile.TextureFormat.BC2_UNORM_SRGB:
                case TexFile.TextureFormat.BC3_UNORM:
                case TexFile.TextureFormat.BC3_UNORM_SRGB:
                case TexFile.TextureFormat.BC5_SNORM:
                case TexFile.TextureFormat.BC5_UNORM:
                case TexFile.TextureFormat.BC6H_UF16:
                case TexFile.TextureFormat.BC6H_SF16:
                case TexFile.TextureFormat.BC7_UNORM:
                case TexFile.TextureFormat.BC7_UNORM_SRGB:
                    {
                        int blockWidth  = (width  + 3) / 4;
                        int blockHeight = (height + 3) / 4;
                        pitch        = blockWidth * 16;
                        slice        = pitch * blockHeight;
                        alignedSlice = (int)Align((uint)(Align((uint)pitch, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT) * blockHeight), D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
                        break;
                    }
                default:
                    {
                        pitch        = ((width) * GetBPP(format) + 7) / 8;
                        slice        = pitch * height;
                        alignedSlice = (int)Align((uint)(Align((uint)pitch, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT) * height), D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
                        break;
                    }
            }
        }
    }
}
