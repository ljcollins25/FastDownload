// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Collections.Concurrent;
using BuildXL.Utilities.Core;

namespace FastDownload.Utilities;

internal class TrackingObjectPool<T>
    where T : class
{
    private ConcurrentQueue<T> allItems { get; } = new();

    private Func<T> _creator;

    private ObjectPool<T> pool { get; }

    public long AllocatedCount => pool.FactoryCalls;

    public IEnumerable<T> AllItems => allItems;

    public TrackingObjectPool(Func<T> creator, Action<T> cleanup)
    {
        _creator = creator;
        pool = new(
            () =>
            {
                var item = creator();
                allItems.Enqueue(item);
                return item;
            },
            cleanup);
    }

    public T GetOrCreateInstance(bool forceCreate = false)
    {
        return forceCreate ? _creator.Invoke() : GetInstance().Instance;
    }

    public PooledObjectWrapper<T> GetInstance()
    {
        var lease = pool.GetInstance();
        return lease;
    }

    public void PutInstance(T instance)
    {
        pool.PutInstance(instance);
    }
}