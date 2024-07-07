using ImageMagick;

namespace LibI2A.Common.Extensions;

public static class MagickExtensions
{
    public static double GetIntensity(this IMagickColor<ushort> color,
        (double H, double S, double V) HSVWeights)
    {
        //Convert to HSV
        (uint a, double h, double s, double v) hsv = Utils.ARGBToAHSV((
            Utils.ScaleUShort(color.A),
            Utils.ScaleUShort(color.R),
            Utils.ScaleUShort(color.G),
            Utils.ScaleUShort(color.B)));

        //Weight HSV components
        (uint a, double, double, double) weighted = (hsv.a, hsv.h * HSVWeights.H, hsv.s * HSVWeights.S, hsv.v * HSVWeights.V);

        //Convert back to rgb
        (uint a, uint r, uint g, uint b) rgb = Utils.AHSVToARGB(weighted);

        return Utils.GetIntensity(rgb);
    }
}