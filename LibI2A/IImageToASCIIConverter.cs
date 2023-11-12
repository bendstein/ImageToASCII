using System.Text;

namespace LibI2A;

/// <summary>
/// An interface allowing for an image to be converted to text data
/// </summary>
public interface IImageToASCIIConverter
{
    /// <summary>
    /// Write the image given in the <paramref name="input"/> stream to the <paramref name="output"/> writer
    /// </summary>
    /// <param name="input">The input stream containing the image data</param>
    /// <param name="output">The text writer to write the converted image to</param>
    /// <param name="before_write">A function taking the glyph to be written, and its color, and and returning a transformed glyph</param>
    /// <param name="after_write">An action that should be performed after the glpyh is written</param>
    public void ConvertImage(Stream input, Stream output,
        Func<string, uint, string>? before_write = null, Action<string, uint>? after_write = null);

    /// <summary>
    /// Write the image given in the <paramref name="data"/> byte array to the <paramref name="output"/> writer
    /// </summary>
    /// <param name="data">The raw image data</param>
    /// <param name="offset">The offset in <paramref name="data"/> where the image data starts</param>
    /// <param name="count">The length of the image data in <paramref name="data"/></param>
    /// <param name="output">The text writer to write the converted image to</param>
    /// <param name="before_write">A function taking the glyph to be written, and its color, and and returning a transformed glyph</param>
    /// <param name="after_write">An action that should be performed after the glpyh is written</param>
    public void ConvertImage(byte[] data, int offset, int count, Stream output,
        Func<string, uint, string>? before_write = null, Action<string, uint>? after_write = null)
    {
        using var ms = new MemoryStream(data, offset, count);
        ConvertImage(ms, output, before_write, after_write);
    }

    /// <summary>
    /// Write the image at the given file path <paramref name="path"/> to the <paramref name="output"/> writer
    /// </summary>
    /// <param name="path">The path to the image file</param>
    /// <param name="output">The text writer to write the converted image to</param>
    /// <param name="before_write">A function taking the glyph to be written, and its color, and and returning a transformed glyph</param>
    /// <param name="after_write">An action that should be performed after the glpyh is written</param>
    public void ConvertImage(string path, Stream output,
        Func<string, uint, string>? before_write = null, Action<string, uint>? after_write = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        ConvertImage(fs, output, before_write, after_write);
    }

    /// <summary>
    /// Write the image given in the <paramref name="input"/> stream to a string
    /// </summary>
    /// <param name="input">The input stream containing the image data</param>
    /// <param name="before_write">A function taking the glyph to be written, and its color, and and returning a transformed glyph</param>
    /// <param name="after_write">An action that should be performed after the glpyh is written</param>
    /// <returns>The converted image</returns>
    public string ConvertImage(Stream input,
        Func<string, uint, string>? before_write = null, Action<string, uint>? after_write = null)
    {
        using var ms = new MemoryStream();

        ConvertImage(input, ms, before_write, after_write);

        using var sr = new StreamReader(ms);

        return sr.ReadToEnd();
    }

    /// <summary>
    /// Write the image given in the <paramref name="data"/> byte array to a string
    /// </summary>
    /// <param name="data">The raw image data</param>
    /// <param name="offset">The offset in <paramref name="data"/> where the image data starts</param>
    /// <param name="count">The length of the image data in <paramref name="data"/></param>
    /// <param name="before_write">A function taking the glyph to be written, and its color, and and returning a transformed glyph</param>
    /// <param name="after_write">An action that should be performed after the glpyh is written</param>
    /// <returns>The converted image</returns>
    public string ConvertImage(byte[] data, int offset, int count,
        Func<string, uint, string>? before_write = null, Action<string, uint>? after_write = null)
    {
        using var ms = new MemoryStream(data, offset, count);
        return ConvertImage(ms, before_write, after_write);
    }

    /// <summary>
    /// Write the image at the given file path <paramref name="path"/> to a string
    /// </summary>
    /// <param name="path">The path to the image file</param>
    /// <param name="before_write">A function taking the glyph to be written, and its color, and and returning a transformed glyph</param>
    /// <param name="after_write">An action that should be performed after the glpyh is written</param>
    /// <returns>The converted image</returns>
    public string ConvertImage(string path,
        Func<string, uint, string>? before_write = null, Action<string, uint>? after_write = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return ConvertImage(fs, before_write, after_write);
    }
}