using FontStashSharp.Interfaces;

namespace Rampastring.XNAUI.FontManagement;

/// <summary>
/// FreeType-based font loader that correctly handles embedded bitmap fonts in TTF files.
/// Replaces FontStashSharp.Rasterizers.FreeType which has broken struct layouts on Windows x64
/// (FreeTypeSharp maps C 'long' to IntPtr instead of int, causing struct field misalignment).
/// </summary>
internal sealed class FreeTypeFontLoader : IFontLoader
{
    public IFontSource Load(byte[] data) => new FreeTypeFontSource(data);
}
