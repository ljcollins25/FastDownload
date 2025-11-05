// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using FastDownload.Shared;
using Polly;

namespace FastDownload.Utilities;

internal static class Extensions
{
    /// <summary>
    /// Gets a region of the span that is trimmed from the start and end by the specified element.
    /// The region is defined by the start index of the first non-trimmed element and the length of the trimmed span.
    /// </summary>
    public static Chunk GetTrimRegion<T>(this ReadOnlySpan<T> span, T trimElement)
        where T : IEquatable<T>
    {
        var trimmed = span.TrimStart(trimElement);
        var start = span.Length - trimmed.Length;
        var length = trimmed.TrimEnd(trimElement).Length;
        return Chunk.FromStartAndLength(start, length);
    }

    /// <summary>
    /// Gets a region of the span that is trimmed from the start and end by zero.
    /// The region is defined by the start index of the first non-zero element and the length of the trimmed span.
    /// </summary>
    public static Chunk GetTrimRegion(this ReadOnlySpan<byte> span)
    {
        var longSpan = MemoryMarshal.Cast<byte, long>(span);
        var divRem = Math.DivRem(span.Length, longSpan.Length);
        if (divRem.Remainder == 0)
        {
            var longRegion = longSpan.GetTrimRegion(0);
            return new Chunk(longRegion.Start * divRem.Quotient, longRegion.End * divRem.Quotient);
        }
        else
        {
            return span.GetTrimRegion<byte>(0);
        }
    }

    public static PooledObjectWrapper<T> GetValue<T>(this PooledObjectWrapper<T> wrapper, out T value)
        where T : class
    {
        value = wrapper.Instance;
        return wrapper;
    }

    public static bool TryGetValue<T>(this Context context, string name, [NotNullWhen(true)] out T? value)
    {
        if (context.TryGetValue(name, out var objValue))
        {
            value = (T)objValue;
            return true;
        }
        else
        {
            value = default(T);
            return false;
        }
    }

    public static TResult Then<T, TResult>(this T input, Func<T, TResult> selector)
    {
        return selector(input);
    }

    public static TValue? ThenOrDefault<T, TValue>(this Result<T> result, Func<T, TValue?> select, TValue? defaultValue = default)
    {
        return result.Succeeded ? select(result.Value) : defaultValue;
    }

    public static ValueTaskAwaiter GetAwaiter(this ValueTask? t)
    {
        return (t ?? ValueTask.CompletedTask).GetAwaiter();
    }

    public static ValueTaskAwaiter<T?> GetAwaiter<T>(this ValueTask<T>? t)
        where T : struct
    {
        async ValueTask<T?> Wrap() => t == null ? null : await t.Value;

#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable CA2012 // Use ValueTasks correctly
        return Wrap().GetAwaiter();
#pragma warning restore CA2012 // Use ValueTasks correctly
#pragma warning restore IDE0079 // Remove unnecessary suppression
    }

    public static async Task GetCompletionTask(this CancellationToken token)
    {
        using var awaitable = token.ToAwaitable();
        await awaitable.CompletionTask;
    }

    public static Task WithCancellationAsync(this Task task, CancellationToken token)
    {
        return TaskUtilities.AwaitWithCancellationAsync(task, token);
    }

    public static async ValueTask<T> WithCancellationAsync<T>(this Task<T> task, CancellationToken token)
    {
        if (!task.IsCompleted && token != default)
        {
            await TaskUtilities.AwaitWithCancellationAsync(task, token);
        }

        token.ThrowIfCancellationRequested();
        return await task;
    }

    public static T? NullIfDefault<T>(this T value)
        where T : struct
    {
        return EqualityComparer<T>.Default.Equals(value, default(T)) ? null : value;
    }

    public static IAsyncEnumerable<T> WithSyncOrAsync<T>(this IAsyncEnumerable<T> items, bool isAsync)
    {
        return isAsync
            ? items
            : items.ToBlockingEnumerable().AsAsync();
    }

    public static async IAsyncEnumerable<T> AsAsync<T>(this IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }

    public static int IndexOfWhere<T>(this IEnumerable<T> items, Func<T, bool> predicate)
    {
        int index = 0;
        foreach (var item in items)
        {
            if (predicate(item))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    public static string GetLocation(this Uri uri)
    {
        return uri.GetComponents(UriComponents.HostAndPort, UriFormat.Unescaped);
    }

    public static string AsNumberString(this bool value)
    {
        return value ? "1" : "0";
    }

    public static string Join(this IEnumerable<string?> values, string separator)
    {
        return string.Join(separator, values);
    }

    public static string Then(this bool condition, string value, string defaultValue = "")
    {
        return condition ? value : defaultValue;
    }

    /// <nodoc />
    public static TimeSpan Multiply(this TimeSpan timespan, double factor)
    {
        return TimeSpan.FromTicks((long)(timespan.Ticks * factor));
    }

    public static double AsMb(this double bytes)
    {
        const double BytesToMb = 1024 * 1024;
        return bytes / BytesToMb;
    }

    public static long AsBytes(this double mb)
    {
        const long BytesInMb = 1024 * 1024;
        return (long)Math.Ceiling(mb * BytesInMb);
    }

    public static double AsMb(this long bytes)
    {
        const long BytesToMb = 1024 * 1024;
        return bytes / BytesToMb;
    }

    public static long RoundUp(this long value, long granularity)
    {
        return ((value + (granularity - 1)) / granularity) * granularity;
    }

    public static TInt DivRoundUp<TInt>(this TInt value, TInt divisor)
        where TInt : unmanaged, INumber<TInt>
    {
        return (value + (divisor - TInt.One)) / divisor;
    }

    public static double Truncate(this double value, int digits)
    {
        return Math.Round(value, digits, MidpointRounding.ToZero);
    }

    public static string ToSortableFileNameString(this DateTimeOffset d) => d.ToString("yyyyMMddThhmmss.fffffff");

    internal static string GetHashString(string s)
    {
        return ContentHashingHelper.CalculateBytesHash(Encoding.UTF8.GetBytes(s), HashType.Murmur).ToShortString(includeHashType: false);
    }
}
