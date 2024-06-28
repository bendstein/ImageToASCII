using Pastel;
using System.Text;

namespace LibI2A.GlyphWriter;
public class ConsoleGlyphWriter : IGlyphWriter
{
    public bool SeekEnabled => ANSIEscapesEnabled;

    public bool SeekLinearEnabled => false;

    /// <summary>
    /// If <see langword="true"/>, will write colored glyphs using ANSI escape sequences
    /// </summary>
    public bool ANSIEscapesEnabled { get; set; } = false;

    /// <summary>
    /// The number of times to repeat the glyph.
    /// </summary>
    public int Repeat { get; set; } = 1;

    public ConsoleGlyphWriter()
    {

    }

    public void Write(string s, uint? color = null)
    {
        Console.Write(Color(s, color));
    }

    public void WriteLine(string s, uint? color = null)
    {
        Console.WriteLine(Color(s, color));
    }

    public void WriteLine()
    {
        Console.WriteLine();
    }

    private string Color(string s, uint? color = null)
    {
        System.Drawing.Color? c = null;

        if (color != null)
        {
            c = System.Drawing.Color.FromArgb(
                (int)(color >> 24) & 0xFF,
                (int)(color >> 16) & 0xFF,
                (int)(color >> 8) & 0xFF,
                (int)color & 0xFF);
        }

        StringBuilder sb = new();

        for (int i = 0; i < Math.Max(1, Repeat); i++)
        {
            string cs = s;

            if (ANSIEscapesEnabled && c.HasValue)
                cs = cs.Pastel(c.Value);

            sb.Append(cs);
        }

        return sb.ToString();
    }

    public void Flush() { }

    public void Seek(int i, int j)
    {
        if (SeekEnabled)
        {
            Console.Write($"\x1b[{i};{j}H");
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    public void SeekLinear(long offset)
    {
        throw new NotImplementedException();
    }
}