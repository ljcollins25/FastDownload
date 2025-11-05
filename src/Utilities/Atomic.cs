// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities.Core.Tasks;

namespace FastDownload.Utilities;

public static class Atomic
{
    /// <summary>
    /// Runs the given async function once using <paramref name="taskSlot"/> to reserve the completion and task result
    /// <returns></returns>
    public static Task<T> RunOnceAsync<T, TData>(ref Task<T>? taskSlot, TData data, Func<TData, Task<T>> runAsync)
    {
        if (TryReserveCompletion(ref taskSlot, out var completion, out var task))
        {
            task = Task.Run(() => runAsync(data));

            completion.LinkToTask(task);
        }

        return task;
    }

    private static bool TryReserveCompletion<TResult>(
            ref Task<TResult>? taskSlot,
            out TaskSourceSlim<TResult> addedTaskCompletionSource,
            out Task<TResult> task)
    {
        task = taskSlot!;
        if (task != null)
        {
            addedTaskCompletionSource = default;
            return false;
        }

        addedTaskCompletionSource = TaskSourceSlim.Create<TResult>();
        if (!TryCompareExchange(ref taskSlot, addedTaskCompletionSource.Task, null, out task!))
        {
            addedTaskCompletionSource = default;
            return false;
        }

        task = addedTaskCompletionSource.Task;
        return true;
    }

    public static bool TryCompareExchange<T>(ref T location, T value, T comparand, out T currentValue)
        where T : class?
    {
        var capturedValue = Interlocked.CompareExchange(ref location, value, comparand);
        currentValue = capturedValue;
        return capturedValue == comparand;
    }

    public static void OptimisticUpdate<T>(ref T location, Func<T, T> update)
        where T : class?
    {
        while (true)
        {
            var comparand = location;
            var value = update(comparand);
            if (TryCompareExchange(ref location, value, comparand: comparand, out _))
            {
                return;
            }
        }
    }
}
