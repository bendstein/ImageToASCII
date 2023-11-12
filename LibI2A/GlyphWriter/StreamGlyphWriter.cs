using Pastel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LibI2A.GlyphWriter;
public class StreamGlyphWriter : IGlyphWriter, IDisposable
{
    public bool SeekEnabled => false;

    public bool SeekLinearEnabled => output.CanSeek;

    /// <summary>
    /// The stream to write the glyph to
    /// </summary>
    private readonly Stream output;

    /// <summary>
    /// The writer for the output stream
    /// </summary>
    private readonly StreamWriter writer;

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

    private StreamGlyphWriter() => throw new NotImplementedException();

    public StreamGlyphWriter(Stream output, bool dispose_stream_on_close = true)
    {
        this.output = output;
        writer = new StreamWriter(output, leaveOpen: dispose_stream_on_close);
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

            writer.Write(s);
        }
    }

    public void Write(string s)
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
        writer.Write(s);
    }

    public void WriteLine()
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
        writer.WriteLine();
    }

    public void Flush()
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
        writer.Flush();
    }

    public void Seek(int i, int j)
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
        throw new NotImplementedException();
    }

    public void SeekLinear(long offset)
    {
        if (disposed) throw new ObjectDisposedException(nameof(StreamGlyphWriter));
        
        if(SeekLinearEnabled)
        {
            output.Seek(offset, SeekOrigin.Begin);
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                writer.Dispose();
            }

            disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}