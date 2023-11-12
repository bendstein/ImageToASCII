using ImageMagick;
using LibI2A.Converter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LibI2A.Common.Extensions;
public static class MagickExtensions
{
    public static double GetLuminance(this IMagickColor<ushort> color)
        => InternalUtils.GetLuminance(((uint)color.A / ushort.MaxValue, (uint)color.R / ushort.MaxValue, (uint)color.G / ushort.MaxValue, (uint)color.B / ushort.MaxValue));
}