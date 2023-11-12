using Pastel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace LibI2A.GlyphWriter;
public class ConsoleGlyphWriter : IGlyphWriter
{
    public bool SeekEnabled => ANSIEscapesEnabled;

    public bool SeekLinearEnabled => false;

    /// <summary>
    /// Whether object has been disposed
    /// </summary>
    private bool disposed = false;

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

    public void Write(Glyph glyph, uint? color = null)
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));

        System.Drawing.Color? c = null;

        if(color != null)
        {
            c = System.Drawing.Color.FromArgb(
                (int)(color >> 24) & 0xFF,
                (int)(color >> 16) & 0xFF,
                (int)(color >> 8) & 0xFF,
                (int)color & 0xFF);
        }

        for (int i = 0; i < Math.Max(1, Repeat); i++)
        {
            string s = glyph.Symbol.ToString();

            if(ANSIEscapesEnabled && c.HasValue)
            {
                s = s.Pastel(c.Value);
            }

            Console.Write(s);
        }
    }

    public void Write(string s)
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
        Console.Write(s);
    }

    public void WriteLine()
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
        Console.WriteLine();
    }

    public void Flush() 
    { 
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
    }

    public void Seek(int i, int j)
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));

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
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
        throw new NotImplementedException();
    }
}