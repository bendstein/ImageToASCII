using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LibI2A;

public struct Glyph
{
    public char Symbol { get; set; } = default;

    public SSIM? SSIM { get; set; } = null;

    public Glyph() { }

    public Glyph(char symbol)
    {
        Symbol = symbol;
    }

    public Glyph WithSSIM(SSIM? value)
    {
        return new Glyph(Symbol) { SSIM = value };
    }
}