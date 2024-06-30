using System.Diagnostics.CodeAnalysis;

namespace LibI2A.SSIM;
public class NoSSIMStore : ISSIMStore
{
    public double GetOrCalculateAndStore(double[] intensities_a, double[] intensities_b, Func<double[], double[], double> calculate)
        => calculate(intensities_a, intensities_b);

    public double GetOrCalculateAndStore(double[] intensities_a, double[] intensities_b, Func<double> calculate)
        => calculate();

    public string GetOrCalculateAndStoreSoln(double[] intensities_a, Func<double[], string> calculate)
        => calculate(intensities_a);

    public string GetOrCalculateAndStoreSoln(double[] intensities_a, Func<string> calculate)
        => calculate();

    public bool TryGet((double[] a, double[] b) key, out double value)
    {
        value = 0;
        return false;
    }

    public bool TryGetSoln(double[] key, [NotNullWhen(true)] out string? solution)
    {
        solution = null;
        return false;
    }
}
