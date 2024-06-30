using ImageMagick;
using LibI2A.Converter;

namespace LibI2A.Common.Extensions;
public static class MagickExtensions
{
    public static double GetLuminance(this IMagickColor<ushort> color)
        => InternalUtils.GetLuminance((
            InternalUtils.ScaleUShort(color.A), 
            InternalUtils.ScaleUShort(color.R), 
            InternalUtils.ScaleUShort(color.G), 
            InternalUtils.ScaleUShort(color.B)));
}