using ImageMagick;
using LibI2A.Converter;

namespace LibI2A.Common.Extensions;
public static class MagickExtensions
{
    public static double GetLuminance(this IMagickColor<ushort> color)
        => InternalUtils.GetLuminance((color.A, color.R, color.G, color.B));
}