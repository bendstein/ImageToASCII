using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LibI2A;
public interface IGlyphWriter
{
    public bool SeekEnabled { get; }

    public bool SeekLinearEnabled { get; }

    public void Write(Glyph glyph, uint? color = null);

    public void Write(string s);

    public void WriteLine();

    public void Flush();

    public void Seek(int i, int j);

    public void SeekLinear(long offset);
}