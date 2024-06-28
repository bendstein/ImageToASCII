using System.Diagnostics.CodeAnalysis;

namespace LibI2A.SSIM;

public interface ISSIMStore
{
    public double GetOrCalculateAndStore(double[] intensities_a, double[] intensities_b,
        Func<double[], double[], double> calculate);

    public double GetOrCalculateAndStore(double[] intensities_a, double[] intensities_b,
        Func<double> calculate);

    public string GetOrCalculateAndStoreSoln(double[] intensities_a,
        Func<double[], string> calculate);

    public string GetOrCalculateAndStoreSoln(double[] intensities_a,
        Func<string> calculate);

    public bool TryGet((double[] a, double[] b) key, out double value);

    public bool TryGetSoln(double[] key, [NotNullWhen(true)] out string? solution);
}