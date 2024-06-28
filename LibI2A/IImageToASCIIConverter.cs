namespace LibI2A;

/// <summary>
/// An interface allowing for an image to be converted to text data
/// </summary>
public interface IImageToASCIIConverter
{
    public IEnumerable<(string glyph, uint? color)> ConvertImage(Stream stream);

    public IEnumerable<(string glyph, uint? color)> ConvertImage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return ConvertImage(fs);
    }

    public IEnumerable<(string glyph, uint? color)> ConvertImage(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return ConvertImage(ms);
    }

    public IEnumerable<(string glyph, uint? color)> ConvertImage(byte[] data, int offset, int count)
    {
        using var ms = new MemoryStream(data, offset, count);
        return ConvertImage(ms);
    }
}