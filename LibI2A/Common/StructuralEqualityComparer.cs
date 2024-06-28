using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LibI2A.Common;
public class StructuralEqualityComparer<T> : IEqualityComparer<T>
{
    public bool Equals(T? x, T? y)
    {
        return StructuralComparisons.StructuralEqualityComparer.Equals(x, y);
    }

    public int GetHashCode(T? obj)
    {
        return obj == null? 0 : StructuralComparisons.StructuralEqualityComparer.GetHashCode(obj);
    }

    private static StructuralEqualityComparer<T>? defaultComparer;

    public static StructuralEqualityComparer<T> Default
    {
        get
        {
            var comparer = defaultComparer;
            if (comparer == null)
            {
                comparer = new StructuralEqualityComparer<T>();
                defaultComparer = comparer;
            }
            return comparer;
        }
    }
}