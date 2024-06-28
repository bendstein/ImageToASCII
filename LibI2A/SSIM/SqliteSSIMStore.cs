using LibI2A.Common;
using LibI2A.Converter;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;

namespace LibI2A.SSIM;
public class SqliteSSIMStore : ISSIMStore, IDisposable
{
    private const int
        PRECISION = 8,
        MAX_KEYS = ushort.MaxValue;

    private const float
        CULL_RATIO = 0.5f;

    private readonly Dictionary<(double[] a, double[] b), double> dict
        = new(StructuralEqualityComparer<(double[] a, double[] b)>.Default);

    private readonly Dictionary<double[], string> soln_dict
        = new(StructuralEqualityComparer<double[]>.Default);

    private readonly SqliteConnection connection;
    private readonly List<SqliteCommand> commands = [];

    private readonly List<Thread> sql_write_threads = [];

    private readonly ReaderWriterLockSlim dict_mutex = new();
    private readonly ReaderWriterLockSlim soln_mutex = new();
    private readonly ReaderWriterLockSlim sqlite_mutex = new(LockRecursionPolicy.SupportsRecursion);
    
    private bool disposed;

    public SqliteSSIMStore(SqliteConnection connection)
    {
        this.connection = connection;
        Init();
    }

    private void Init()
    {
        sqlite_mutex.EnterWriteLock();
        try
        {
            var comm = connection.CreateCommand();
            commands.Add(comm);

            comm.CommandText = """

                create table if not exists SSIMMemo (
                    SSIMKey nvarchar primary key,
                    SSIM double
                );

                create table if not exists SSIMSolns (
                    SSIMKey nvarchar primary key,
                    Glyph nvarchar
                );

            """;

            comm.ExecuteNonQuery();
        }
        finally
        {
            sqlite_mutex.ExitWriteLock();
        }
    }

    public double GetOrCalculateAndStore(double[] intensities_a, double[] intensities_b, Func<double[], double[], double> calculate)
    {
        var rounded_a = intensities_a.Select(d => InternalUtils.Round(d, PRECISION)).ToArray();
        var rounded_b = intensities_b.Select(d => InternalUtils.Round(d, PRECISION)).ToArray();

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
        var rounded_a = intensities_a.Select(d => InternalUtils.Round(d, PRECISION)).ToArray();

        if (TryGetSoln(rounded_a, out var value))
            return value;

        value = calculate(rounded_a);

        SetSoln(rounded_a, value);

        return value;
    }

    public string GetOrCalculateAndStoreSoln(double[] intensities_a, Func<string> calculate)
    {
        var rounded_a = intensities_a.Select(d => InternalUtils.Round(d, PRECISION)).ToArray();

        if (TryGetSoln(rounded_a, out var value))
            return value;

        value = calculate();

        SetSoln(rounded_a, value);

        return value;
    }

    public bool TryGet((double[] a, double[] b) key, out double value)
    {
        dict_mutex.EnterReadLock();
        try
        {
            //Check if in dictionary
            if (dict.TryGetValue(key, out value))
                return true;

            //Check if in db
            sqlite_mutex.EnterReadLock();
            try
            {
                var comm = connection.CreateCommand();
                commands.Add(comm);

                comm.CommandText = """

                    select
                        SSIM
                    from
                        SSIMMemo
                    where
                        SSIMKey = @key;

                """;
                comm.Parameters.AddWithValue("@key", Stringify(key));

                var maybe_value = comm.ExecuteScalar();

                if (maybe_value == null)
                    return false;

                value = (double)Convert.ChangeType(maybe_value, typeof(double));
            }
            finally
            {
                sqlite_mutex.ExitReadLock();
            }
        }
        finally
        {
            dict_mutex.ExitReadLock();
        }

        //Pull value up into dictionary
        dict_mutex.EnterUpgradeableReadLock();
        try
        {
            if (!dict.ContainsKey(key))
            {
                dict_mutex.EnterWriteLock();

                try
                {
                    if (!dict.ContainsKey(key))
                    {
                        if (dict.Count + 1 >= MAX_KEYS)
                            Cull(dict);

                        dict[key] = value;
                    }
                }
                finally
                {
                    dict_mutex.ExitWriteLock();
                }
            }
        }
        finally
        {
            dict_mutex.ExitUpgradeableReadLock();
        }

        return true;
    }

    private void Set((double[] a, double[] b) key, double value)
    {
        dict_mutex.EnterUpgradeableReadLock();
        try
        {
            //Value already present
            if (dict.ContainsKey(key))
                return;

            dict_mutex.EnterWriteLock();

            try
            {
                if (dict.ContainsKey(key))
                    return;

                if (dict.Count + 1 >= MAX_KEYS)
                    Cull(dict);

                //Set value in dictionary
                dict[key] = value;

                Thread sql_thread = new(() =>
                {
                    //Set value in db
                    sqlite_mutex.EnterWriteLock();
                    try
                    {
                        var comm = connection.CreateCommand();
                        commands.Add(comm);
                        comm.CommandText = """
                            insert or ignore into
                                SSIMMemo
                                (SSIMKey, SSIM)
                            values
                                (@key, @value);

                        """;
                        comm.Parameters.AddWithValue("@key", Stringify(key));
                        comm.Parameters.AddWithValue("@value", value);

                        comm.ExecuteNonQuery();
                    }
                    finally
                    {
                        sqlite_mutex.ExitWriteLock();
                    }
                })
                {
                    IsBackground = true
                };

                sql_thread.Start();
                sql_write_threads.Add(sql_thread);
            }
            finally
            {
                dict_mutex.ExitWriteLock();
            }
        }
        finally
        {
            dict_mutex.ExitUpgradeableReadLock();
        }
    }

    public bool TryGetSoln(double[] key, [NotNullWhen(true)] out string? value)
    {
        soln_mutex.EnterReadLock();
        try
        {
            //Check if in dictionary
            if (soln_dict.TryGetValue(key, out value))
                return true;

            //Check if in db
            sqlite_mutex.EnterReadLock();
            try
            {
                var comm = connection.CreateCommand();
                commands.Add(comm);
                comm.CommandText = """

                        select
                            Glyph
                        from
                            SSIMSolns
                        where
                            SSIMKey = @key;

                    """;
                comm.Parameters.AddWithValue("@key", Stringify(key));

                var maybe_value = comm.ExecuteScalar();

                if (maybe_value == null)
                    return false;

                value = maybe_value.ToString();
            }
            finally
            {
                sqlite_mutex.ExitReadLock();
            }
        }
        finally
        {
            soln_mutex.ExitReadLock();
        }

        //Pull value up into dictionary
        soln_mutex.EnterUpgradeableReadLock();
        try
        {
            if (!soln_dict.ContainsKey(key) && value != null)
            {
                soln_mutex.EnterWriteLock();

                try
                {
                    if (!soln_dict.ContainsKey(key))
                    {
                        if (soln_dict.Count + 1 >= MAX_KEYS)
                            Cull(soln_dict);

                        soln_dict[key] = value;
                    }
                }
                finally
                {
                    soln_mutex.ExitWriteLock();
                }
            }
        }
        finally
        {
            soln_mutex.ExitUpgradeableReadLock();
        }

        return value != null;
    }

    private void SetSoln(double[] key, string value)
    {
        soln_mutex.EnterUpgradeableReadLock();
        try
        {
            //Value already present
            if (soln_dict.ContainsKey(key))
                return;

            soln_mutex.EnterWriteLock();

            try
            {
                if (soln_dict.ContainsKey(key))
                    return;

                if (soln_dict.Count + 1 >= MAX_KEYS)
                    Cull(soln_dict);

                //Set value in dictionary
                soln_dict[key] = value;

                Thread sql_thread = new(() =>
                {
                    //Set value in db
                    sqlite_mutex.EnterWriteLock();
                    try
                    {
                        var comm = connection.CreateCommand();
                        commands.Add(comm);
                        comm.CommandText = """
                            insert or ignore into
                                SSIMSolns
                                (SSIMKey, Glyph)
                            values
                                (@key, @value);

                        """;
                        comm.Parameters.AddWithValue("@key", Stringify(key));
                        comm.Parameters.AddWithValue("@value", value);

                        comm.ExecuteNonQuery();
                    }
                    finally
                    {
                        sqlite_mutex.ExitWriteLock();
                    }
                })
                {
                    IsBackground = true
                };

                sql_thread.Start();
                sql_write_threads.Add(sql_thread);
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

    private string Stringify((double[] a, double[] b) key)
        => string.Join(';', [
            string.Join(',', key.a.Select(v => InternalUtils.Round(v, PRECISION).ToString())),
            string.Join(',', key.b.Select(v => InternalUtils.Round(v, PRECISION).ToString()))
        ]);

    private string Stringify(double[] key)
        => string.Join(',', key.Select(v => InternalUtils.Round(v, PRECISION).ToString()));

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                connection.Dispose();

                foreach (var command in commands)
                    command.Dispose();
            }

            disposed = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Cull<K, V>(Dictionary<K, V> dict)
        where K : notnull
    {
        HashSet<K> to_remove = dict.Keys
            .Where(k => Random.Shared.NextSingle() < CULL_RATIO)
            .ToHashSet();

        foreach (var key in to_remove)
            dict.Remove(key);

        //Cull any completed commands
        //(Was seeing strange behavior when 'using' commands in multiple threads)
        sqlite_mutex.EnterWriteLock();
        try
        {
            foreach (var command in commands)
                command.Dispose();
            commands.Clear();
        }
        finally
        {
            sqlite_mutex.ExitWriteLock();
        }
    }
}
