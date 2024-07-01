using ImageMagick;
using LibI2A.Converter;

namespace LibI2A.Common.Extensions;
public static class MagickExtensions
{
    public static double GetLuminance(this IMagickColor<ushort> color,
        (double H, double S, double V) HSVWeights)
    {
        //Convert to HSV
        var hsv = InternalUtils.ARGBToAHSV((
            InternalUtils.ScaleUShort(color.A),
            InternalUtils.ScaleUShort(color.R),
            InternalUtils.ScaleUShort(color.G),
            InternalUtils.ScaleUShort(color.B)));

        //Weight HSV components
        var weighted = (hsv.a, hsv.h * HSVWeights.H, hsv.s * HSVWeights.S, hsv.v * HSVWeights.V);

        //Convert back to rgb
        var rgb = InternalUtils.AHSVToARGB(weighted);

        return InternalUtils.GetLuminance(rgb);
    }
}