// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using System.Numerics;

namespace FastDownload.Utilities;

internal static class Maths
{
    /// <summary>
    /// Euclidean algorithm.
    ///
    /// See: https://en.wikipedia.org/wiki/Greatest_common_divisor#Euclidean_algorithm
    /// </summary>
    public static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            int temp = b;
            b = a % b;
            a = temp;
        }

        return a;
    }

    /// <summary>
    /// See: https://en.wikipedia.org/wiki/Least_common_multiple#Calculation
    /// </summary>
    public static int LowestCommonMultiple(int a, int b)
    {
        return a / GreatestCommonDivisor(a, b) * b;
    }

    /// <summary>
    /// Aligns a value to the next multiple of the alignment.
    /// </summary>
    public static ulong AlignTo(ulong value, ulong alignment)
    {
        var aligned = (value + alignment - 1) & ~(alignment - 1);
        Contract.Assert(aligned % alignment == 0, $"Expected {value} after alignment ({aligned}) by {alignment} to be divisible by {alignment}. Found {aligned % alignment} instead");
        Contract.Assert(aligned >= value, $"Expected {value} after alignment ({aligned}) by {alignment} to be greater than {value}");
        return aligned;
    }

    /// <summary>
    /// Checks if a value is aligned to the specified alignment.
    /// </summary>
    public static bool IsAligned<TInt>(this TInt value, TInt alignment)
        where TInt : struct, IBinaryNumber<TInt>, IModulusOperators<TInt, TInt, TInt>
    {
        Contract.Requires(!TInt.IsNegative(value));
        Contract.Requires(!TInt.IsPositive(alignment));
        return value >= alignment && (value % alignment) == TInt.Zero;
    }

    /// <summary>
    /// Checks if a value is aligned to the specified alignment.
    /// </summary>
    public static bool IsAlignedOrZero<TInt>(this TInt value, TInt alignment)
        where TInt : struct, IBinaryNumber<TInt>, IModulusOperators<TInt, TInt, TInt>
        => value == TInt.Zero || IsAligned(value, alignment);
}
