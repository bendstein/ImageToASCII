using LibI2A.Converter;
using System.Diagnostics.CodeAnalysis;
namespace LibI2A.SSIM;

public class DictionarySSIMStore : ISSIMStore
{
    public const int 
        PRECISION = 7,
        MAX_KEYS = short.MaxValue;

    public const double
        CULL_RATIO = 0.5f;

    private readonly Dictionary<string, double> dict = [];

    private readonly Dictionary<string, string> soln_dict = [];

    private readonly ReaderWriterLockSlim mutex = new();
    private readonly ReaderWriterLockSlim soln_mutex = new();

    public int MaxKeys { get; init; } = MAX_KEYS;

    public double GetOrCalculateAndStore(double[] intensities_a, double[] intensities_b, Func<double[], double[], double> calculate)
    {
        var rounded_a = intensities_a.ToArray();
        var rounded_b = intensities_b.ToArray();

        if (TryGet((rounded_a, rounded_b), out var value))
            return value;

        value = calculate(rounded_a, rounded_b);

        Set((rounded_a, rounded_b), value);

        return value;
    }

    public double GetOrCalculateAndStore(double[] intensities_a, double[] intensities_b, Func<double> calculate)
    {
        if (TryGet((intensities_a, intensities_b), out var value))
            return value;

        value = calculate();

        Set((intensities_a, intensities_b), value);

        return value;
    }

    public string GetOrCalculateAndStoreSoln(double[] intensities_a, Func<double[], string> calculate)
    {
        var rounded_a = intensities_a.ToArray();

        if (TryGetSoln(rounded_a, out var value))
            return value;

        value = calculate(rounded_a);

        SetSoln(rounded_a, value);

        return value;
    }

    public string GetOrCalculateAndStoreSoln(double[] intensities_a, Func<string> calculate)
    {
        if (TryGetSoln(intensities_a, out var value))
            return value;

        value = calculate();

        SetSoln(intensities_a, value);

        return value;
    }

    public bool TryGet((double[] a, double[] b) key, out double value)
    {
        string skey = ArraysToKey(key);

        mutex.EnterReadLock();
        try
        {
            return dict.TryGetValue(skey, out value);
        }
        finally
        {
            mutex.ExitReadLock();
        }
    }

    private void Set((double[] a, double[] b) key, double value)
    {
        string skey = ArraysToKey(key);

        mutex.EnterUpgradeableReadLock();
        try
        {
            if (dict.ContainsKey(skey))
                return;

            mutex.EnterWriteLock();

            try
            {
                if (dict.ContainsKey(skey))
                    return;

                if (MaxKeys >= 0 && dict.Count + 1 > MaxKeys)
                    Cull(dict);

                dict[skey] = value;
            }
            finally
            {
                mutex.ExitWriteLock();
            }
        }
        finally
        {
            mutex.ExitUpgradeableReadLock();
        }
    }

    public bool TryGetSoln(double[] key, [NotNullWhen(true)] out string? value)
    {
        string skey = ArrayToKey(key);

        soln_mutex.EnterReadLock();
        try
        {
            return soln_dict.TryGetValue(skey, out value);
        }
        finally
        {
            soln_mutex.ExitReadLock();
        }
    }

    private void SetSoln(double[] key, string value)
    {
        string skey = ArrayToKey(key);

        soln_mutex.EnterUpgradeableReadLock();
        try
        {
            if (soln_dict.ContainsKey(skey))
                return;

            soln_mutex.EnterWriteLock();

            try
            {
                if (soln_dict.ContainsKey(skey))
                    return;

                if (MaxKeys >= 0 && soln_dict.Count + 1 > MaxKeys)
                    Cull(soln_dict);

                soln_dict[skey] = value;
            }
            finally
            {
                soln_mutex.ExitWriteLock();
            }
        }
        finally
        {
            soln_mutex.ExitUpgradeableReadLock();
        }
    }

    private static string ArrayToKey(double[] a)
        => string.Join(',', a.Select(d => InternalUtils.Round(d, PRECISION)));

    private static string ArraysToKey(double[] a, double[] b)
        => $"{ArrayToKey(a)};{ArrayToKey(b)}";

    private static string ArraysToKey((double[] a, double[] b) key)
       => ArraysToKey(key.a, key.b);

    private static void Cull<K, V>(Dictionary<K, V> dict)
        where K : notnull
    {
        HashSet<K> to_remove = dict.Keys
            .Where(k => Random.Shared.NextSingle() < CULL_RATIO)
            .ToHashSet();

        foreach (var key in to_remove)
            dict.Remove(key);
    }
}