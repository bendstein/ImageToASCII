namespace LibI2A;

public struct SSIM : IEquatable<SSIM>
{
    public double?[] Luminances { get; set; } = Array.Empty<double?>();

    public double MeanLuminance { get; set; } = 0;

    public double StdDevLuminance { get; set; } = 0;

    public SSIM() { }

    public override bool Equals(object? obj)
    {
        return obj is SSIM sSIM && Equals(sSIM);
    }

    public bool Equals(SSIM other)
    {
        if(this.Luminances.Length != other.Luminances.Length)
            return false;

        for(int i = 0; i < Luminances.Length; i++)
        {
            var this_lum = this.Luminances[i];
            var other_lum = other.Luminances[i];
            if(this_lum != other_lum) 
                return false;
        }

        return MeanLuminance == other.MeanLuminance &&
               StdDevLuminance == other.StdDevLuminance;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Luminances.Select(l => l?.GetHashCode()?? 0)
                .Aggregate(0, (a, b) => HashCode.Combine(a, b)),
            MeanLuminance, StdDevLuminance);
    }

    public static bool operator ==(SSIM left, SSIM right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SSIM left, SSIM right)
    {
        return !(left == right);
    }
}

public struct SSIMComparison
{
    public double Luminance { get; set; } = 0d;

    public double Contrast { get; set; } = 0d;

    public double Structure { get; set; } = 0d;

    public SSIMComparison() { }

    public double Index => Luminance * Contrast * Structure;
}