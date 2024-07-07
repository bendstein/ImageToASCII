namespace LibI2A;

/// <summary>
/// An interface allowing for an image to be converted to text data
/// </summary>
public interface IImageToASCIIConverter
{
    /// <summary>
    /// Convert the image to ASCII glyphs
    /// </summary>
    /// <param name="stream">A stream containing the image to convert</param>
    /// <returns>An IEnumerable of ASCII glyphs and their colors.</returns>
    public IEnumerable<(string glyph, uint? color)> ConvertImage(Stream stream);

    /// <summary>
    /// Convert the image to ASCII glyphs
    /// </summary>
    /// <param name="path">The path of an image to convert</param>
    /// <returns>An IEnumerable of ASCII glyphs and their colors.</returns>
    public IEnumerable<(string glyph, uint? color)> ConvertImage(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.ReadWrite);
        return ConvertImage(fs);
    }

    /// <summary>
    /// Convert the image to ASCII glyphs
    /// </summary>
    /// <param name="data">The bytes of an image to convert</param>
    /// <returns>An IEnumerable of ASCII glyphs and their colors.</returns>
    public IEnumerable<(string glyph, uint? color)> ConvertImage(byte[] data)
    {
        using MemoryStream ms = new(data);
        return ConvertImage(ms);
    }

    /// <summary>
    /// Convert the image to ASCII glyphs
    /// </summary>
    /// <param name="data">A byte[] containing the image to convert</param>
    /// <param name="offset">The offset into the byte[] where the image starts</param>
    /// <param name="count">The length of the image data in the byte[]</param>
    /// <returns>An IEnumerable of ASCII glyphs and their colors.</returns>
    public IEnumerable<(string glyph, uint? color)> ConvertImage(byte[] data, int offset, int count)
    {
        using MemoryStream ms = new(data, offset, count);
        return ConvertImage(ms);
    }
}