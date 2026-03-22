using System;
using System.Runtime.InteropServices;
using FontStashSharp.Interfaces;

namespace Rampastring.XNAUI.FontManagement;

/// <summary>
/// IFontSource implementation backed by FreeType with correct Windows P/Invoke struct layouts.
/// Unlike FreeTypeSharp, this correctly maps FT_Pos/FT_Fixed (C 'long') to 'int' on Windows,
/// where C 'long' is always 4 bytes regardless of 32/64-bit.
/// </summary>
internal sealed class FreeTypeFontSource : IFontSource
{
    private static IntPtr _library;
    private GCHandle _memoryHandle;
    private IntPtr _face;

    public FreeTypeFontSource(byte[] data)
    {
        if (_library == IntPtr.Zero)
        {
            int err = FT_Init_FreeType(out _library);
            if (err != 0)
                throw new InvalidOperationException($"FT_Init_FreeType failed with error {err}");
        }

        // Pin the font data in memory so FreeType can read from it.
        _memoryHandle = GCHandle.Alloc(data, GCHandleType.Pinned);

        int error = FT_New_Memory_Face(
            _library,
            _memoryHandle.AddrOfPinnedObject(),
            data.Length,
            0,
            out _face);

        if (error != 0)
            throw new InvalidOperationException($"FT_New_Memory_Face failed with error {error}");
    }

    ~FreeTypeFontSource()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_face != IntPtr.Zero)
        {
            FT_Done_Face(_face);
            _face = IntPtr.Zero;
        }

        if (_memoryHandle.IsAllocated)
            _memoryHandle.Free();
    }

    public int? GetGlyphId(int codepoint)
    {
        uint index = FT_Get_Char_Index(_face, (uint)codepoint);
        return index == 0 ? null : (int?)index;
    }

    public void GetMetricsForSize(float fontSize, out int ascent, out int descent, out int lineHeight)
    {
        SetPixelSizes(0, fontSize);

        IntPtr facePtr = _face;
        IntPtr sizePtr = ReadFaceSize(facePtr);
        FT_Size_Metrics metrics = ReadSizeMetrics(sizePtr);

        ascent = metrics.ascender >> 6;
        descent = metrics.descender >> 6;
        lineHeight = metrics.height >> 6;
    }

    public void GetGlyphMetrics(int glyphId, float fontSize, out int advance, out int x0, out int y0, out int x1, out int y1)
    {
        SetPixelSizes(0, fontSize);
        LoadGlyph(glyphId);

        IntPtr slotPtr = ReadFaceGlyphSlot(_face);
        FT_Glyph_Metrics glyphMetrics = ReadGlyphSlotMetrics(slotPtr);

        advance = glyphMetrics.horiAdvance >> 6;
        x0 = glyphMetrics.horiBearingX >> 6;
        y0 = -(glyphMetrics.horiBearingY >> 6);
        x1 = x0 + (glyphMetrics.width >> 6);
        y1 = y0 + (glyphMetrics.height >> 6);
    }

    public void RasterizeGlyphBitmap(int glyphId, float fontSize, byte[] buffer, int startIndex, int outWidth, int outHeight, int outStride)
    {
        SetPixelSizes(0, fontSize);
        LoadGlyph(glyphId);

        IntPtr slotPtr = ReadFaceGlyphSlot(_face);
        FT_Render_Glyph(slotPtr, FT_RENDER_MODE_NORMAL);

        FT_Bitmap bitmap = ReadGlyphSlotBitmap(slotPtr);

        for (int y = 0; y < outHeight; y++)
        {
            int dstPos = (y * outStride) + startIndex;
            IntPtr srcRow = bitmap.buffer + (y * bitmap.pitch);

            if (bitmap.pixel_mode == FT_PIXEL_MODE_GRAY)
            {
                Marshal.Copy(srcRow, buffer, dstPos, Math.Min(outWidth, Math.Abs(bitmap.pitch)));
            }
            else if (bitmap.pixel_mode == FT_PIXEL_MODE_MONO)
            {
                for (int x = 0; x < outWidth; x += 8)
                {
                    byte bits = Marshal.ReadByte(srcRow, x / 8);
                    int count = Math.Min(8, outWidth - x);
                    for (int b = 0; b < count; b++)
                    {
                        buffer[dstPos + x + b] = ((bits >> (7 - b)) & 1) != 0 ? (byte)255 : (byte)0;
                    }
                }
            }
        }
    }

    public int GetGlyphKernAdvance(int previousGlyphId, int glyphId, float fontSize)
    {
        int err = FT_Get_Kerning(_face, (uint)previousGlyphId, (uint)glyphId, FT_KERNING_DEFAULT, out FT_Vector kerning);
        if (err != 0)
            return 0;

        return kerning.x >> 6;
    }

    public float CalculateScaleForTextShaper(float fontSize)
    {
        ushort unitsPerEM = ReadFaceUnitsPerEM(_face);
        return fontSize / unitsPerEM;
    }

    private void SetPixelSizes(float width, float height)
    {
        int err = FT_Set_Pixel_Sizes(_face, (uint)width, (uint)height);
        if (err != 0)
            throw new InvalidOperationException($"FT_Set_Pixel_Sizes failed with error {err}");
    }

    private void LoadGlyph(int glyphId)
    {
        int err = FT_Load_Glyph(_face, (uint)glyphId, FT_LOAD_DEFAULT | FT_LOAD_COLOR);
        if (err != 0)
            throw new InvalidOperationException($"FT_Load_Glyph failed with error {err}");
    }

    #region Struct field reading via Marshal (correct Windows ABI offsets)

    // On Windows, C 'long' is always 4 bytes (both x86 and x64).
    // FreeType's FT_Pos and FT_Fixed are typedef'd as 'signed long'.
    // Pointers are 4 bytes on x86 and 8 bytes on x64.
    // We compute offsets dynamically based on IntPtr.Size to support both.

    private static readonly int PtrSize = IntPtr.Size;

    // FT_FaceRec field offsets (computed for Windows x86 and x64)
    // Fields: num_faces(4), face_index(4), face_flags(4), style_flags(4), num_glyphs(4),
    //         [pad to ptr], family_name(ptr), style_name(ptr), num_fixed_sizes(4),
    //         [pad to ptr], available_sizes(ptr), num_charmaps(4), [pad to ptr], charmaps(ptr),
    //         generic(2*ptr), bbox(16), units_per_EM(2), ascender(2), descender(2), height(2),
    //         max_advance_width(2), max_advance_height(2), underline_position(2), underline_thickness(2),
    //         glyph(ptr), size(ptr)

    private static int AlignTo(int offset, int alignment) => (offset + alignment - 1) & ~(alignment - 1);

    private static int ComputeFaceFieldOffset(string field)
    {
        int p = PtrSize;
        int offset = 0;

        // 5 x int (FT_Long = 4 on Windows)
        offset += 5 * 4; // num_faces, face_index, face_flags, style_flags, num_glyphs = 20

        offset = AlignTo(offset, p);
        // family_name (ptr)
        offset += p;
        // style_name (ptr)
        offset += p;
        // num_fixed_sizes (int)
        offset += 4;
        offset = AlignTo(offset, p);
        // available_sizes (ptr)
        offset += p;
        // num_charmaps (int)
        offset += 4;
        offset = AlignTo(offset, p);
        // charmaps (ptr)
        offset += p;
        // generic: FT_Generic = data(ptr) + finalizer(ptr)
        offset += 2 * p;
        // bbox: FT_BBox = 4 x FT_Pos(4) = 16
        offset += 16;

        if (field == "units_per_EM")
            return offset;

        // units_per_EM(2) + ascender(2) + descender(2) + height(2)
        // + max_advance_width(2) + max_advance_height(2) + underline_position(2) + underline_thickness(2)
        offset += 16;
        offset = AlignTo(offset, p);

        if (field == "glyph")
            return offset;

        offset += p; // glyph

        if (field == "size")
            return offset;

        throw new ArgumentException($"Unknown FT_FaceRec field: {field}");
    }

    private static readonly int FaceGlyphOffset = ComputeFaceFieldOffset("glyph");
    private static readonly int FaceSizeOffset = ComputeFaceFieldOffset("size");
    private static readonly int FaceUnitsPerEMOffset = ComputeFaceFieldOffset("units_per_EM");

    private static IntPtr ReadFaceGlyphSlot(IntPtr face) => Marshal.ReadIntPtr(face, FaceGlyphOffset);
    private static IntPtr ReadFaceSize(IntPtr face) => Marshal.ReadIntPtr(face, FaceSizeOffset);
    private static ushort ReadFaceUnitsPerEM(IntPtr face) => (ushort)Marshal.ReadInt16(face, FaceUnitsPerEMOffset);

    // FT_SizeRec: face(ptr) + generic(2*ptr) + FT_Size_Metrics
    // FT_Size_Metrics: x_ppem(2) + y_ppem(2) + x_scale(4) + y_scale(4) + ascender(4) + descender(4) + height(4) + max_advance(4)
    private static readonly int SizeMetricsOffset = PtrSize + 2 * PtrSize; // face + generic

    private static FT_Size_Metrics ReadSizeMetrics(IntPtr sizePtr)
    {
        return Marshal.PtrToStructure<FT_Size_Metrics>(sizePtr + SizeMetricsOffset);
    }

    // FT_GlyphSlotRec: library(ptr) + face(ptr) + next(ptr) + glyph_index(4) + [pad to ptr] + generic(2*ptr) + FT_Glyph_Metrics
    private static readonly int GlyphSlotMetricsOffset = 3 * PtrSize + AlignTo(4, PtrSize) + 2 * PtrSize;

    private static FT_Glyph_Metrics ReadGlyphSlotMetrics(IntPtr slotPtr)
    {
        return Marshal.PtrToStructure<FT_Glyph_Metrics>(slotPtr + GlyphSlotMetricsOffset);
    }

    // After FT_Glyph_Metrics(32): linearHoriAdvance(4) + linearVertAdvance(4) + advance(8) + format(4) + [pad to bitmap alignment]
    // FT_Bitmap starts after format, aligned to pointer size (for buffer field)
    private static readonly int GlyphSlotBitmapOffset = GlyphSlotMetricsOffset + 32 + 4 + 4 + 8 + AlignTo(4, PtrSize);

    private static FT_Bitmap ReadGlyphSlotBitmap(IntPtr slotPtr)
    {
        return Marshal.PtrToStructure<FT_Bitmap>(slotPtr + GlyphSlotBitmapOffset);
    }

    #endregion

    #region Native structs (correct for Windows where C 'long' = 4 bytes)

    [StructLayout(LayoutKind.Sequential)]
    private struct FT_Vector
    {
        public int x; // FT_Pos = C long = 4 bytes on Windows
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FT_Size_Metrics
    {
        public ushort x_ppem;
        public ushort y_ppem;
        public int x_scale;      // FT_Fixed = C long = 4 bytes on Windows
        public int y_scale;
        public int ascender;     // FT_Pos
        public int descender;
        public int height;
        public int max_advance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FT_Glyph_Metrics
    {
        public int width;        // FT_Pos
        public int height;
        public int horiBearingX;
        public int horiBearingY;
        public int horiAdvance;
        public int vertBearingX;
        public int vertBearingY;
        public int vertAdvance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FT_Bitmap
    {
        public uint rows;
        public uint width;
        public int pitch;
        public IntPtr buffer;
        public ushort num_grays;
        public byte pixel_mode;
        public byte palette_mode;
        public IntPtr palette;
    }

    #endregion

    #region FreeType constants

    private const int FT_LOAD_DEFAULT = 0x0;
    private const int FT_LOAD_COLOR = 0x20;
    private const int FT_RENDER_MODE_NORMAL = 0;
    private const uint FT_KERNING_DEFAULT = 0;
    private const byte FT_PIXEL_MODE_MONO = 1;
    private const byte FT_PIXEL_MODE_GRAY = 2;

    #endregion

    #region P/Invoke declarations

    private const string FreeTypeLib = "freetype";

    [DllImport(FreeTypeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FT_Init_FreeType(out IntPtr library);

    [DllImport(FreeTypeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FT_New_Memory_Face(IntPtr library, IntPtr file_base, int file_size, int face_index, out IntPtr face);

    [DllImport(FreeTypeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FT_Done_Face(IntPtr face);

    [DllImport(FreeTypeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FT_Set_Pixel_Sizes(IntPtr face, uint pixel_width, uint pixel_height);

    [DllImport(FreeTypeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint FT_Get_Char_Index(IntPtr face, uint charcode);

    [DllImport(FreeTypeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FT_Load_Glyph(IntPtr face, uint glyph_index, int load_flags);

    [DllImport(FreeTypeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FT_Render_Glyph(IntPtr slot, int render_mode);

    [DllImport(FreeTypeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FT_Get_Kerning(IntPtr face, uint left_glyph, uint right_glyph, uint kern_mode, out FT_Vector kerning);

    #endregion
}
