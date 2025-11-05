// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Runtime.CompilerServices;

namespace FastDownload.Download;

/// <summary>
/// Tracks the ref count to an object (initialized at 1) and allows for performing a cleanup
/// action when the ref count reaches zero.
/// </summary>
public class RefCountHandle<T>(T value, Action? onCleanup = null) : IDisposable
{
    public T? Value { get; private set; } = value;

    private Action? _onCleanup = onCleanup;

    // state < 0 = has pending or completed cleanup
    // state & int.MaxValue = number of active references
    private RefCount _state = new RefCount(1); // Initialally there is one reservation for the worker which persists or decompresses the chunk
    private int _isCleanedUp = 0;

    public uint ActiveReferences => _state.ActiveReferences;
    public int PendingCleanups => _state.PendingCleanups;

    public string? LastReferenceCallerName { get; private set; }

    public bool TryReference([CallerMemberName] string? name = null)
    {
        if (ChangeState(1).IsPendingCleanup)
        {
            ChangeState(-1);
            return false;
        }

        LastReferenceCallerName = name;
        return true;
    }

    public void Release()
    {
        ChangeState(-1);
    }

    public void Dispose()
    {
        Release();
    }

    private RefCount ChangeState(int addend)
    {
        var result = _state.Add(addend);
        if (result.ActiveReferences == 0 && !result.IsPendingCleanup)
        {
            // no active references, so mark for cleanup
            result = _state.QueueCleanup();
        }

        if (result.ActiveReferences == 0
            && result.IsPendingCleanup
            && Interlocked.CompareExchange(ref _isCleanedUp, 1, 0) == 0)
        {
            _onCleanup?.Invoke();
            _onCleanup = null;
            Value = default;
        }

        return result;
    }

    private struct RefCount(long state)
    {
        public long State = state;
        public uint ActiveReferences => (uint)(State & uint.MaxValue);
        public int PendingCleanups => (int)(State >> 32);

        public bool IsPendingCleanup => PendingCleanups > 0;

        public RefCount QueueCleanup()
        {
            return new(Interlocked.Add(ref State, 1L << 32));
        }

        public RefCount Add(int value)
        {
            return new(Interlocked.Add(ref State, value));
        }
    }
}
