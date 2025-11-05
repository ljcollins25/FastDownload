// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Download;

/// <summary>
/// Allows tests to simulate delays in time
/// </summary>
public class AsyncTimer : IDisposable
{
    public static readonly AsyncTimer Default = new AsyncTimer();

    /// <summary>
    /// The offset from actual time (defaults to zero). This is added to
    /// to the current time (i.e. Now) to get the virtual Now.
    /// </summary>
    public virtual TimeSpan VirtualTimeOffset { get; } = default;

    public virtual Task Delay(TimeSpan delay, CancellationToken token)
    {
        return Task.Delay(delay, token);
    }

    /// <summary>
    /// Timer is disposed when no further events are needed
    /// </summary>
    public virtual void Dispose()
    {
        // Do nothing. Derived types may override.
    }
}
