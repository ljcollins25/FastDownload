// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System;
using System.Threading;

namespace FastDownload.Tests;

/// <summary>
/// Helper class that deletes the temp directory on dispose.
/// </summary>
/// <remarks>
/// Use instead of <see cref="DisposableTempDirectory"/> when you need to avoid depending on Windows-specific APIs.
/// </remarks>
public class DisposableTempDirectorySlim : IDisposable
{
    /// <summary>
    /// Path to the temp directory
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Create a directory under a base path
    /// </summary>
    /// <param name="basePath">The base path to create the directory under.</param>
    public DisposableTempDirectorySlim(string? basePath = null, string? name = null)
    {
        basePath ??= System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(basePath, name ?? Guid.NewGuid().ToString());
        _ = System.IO.Directory.CreateDirectory(Path);
    }

    ~DisposableTempDirectorySlim()
    {
        Dispose();
    }

    /// <nodoc />
    private int _isDisposed;

    /// <summary>
    /// Deletes the temporary directory.
    /// </summary>
    public void Dispose()
    {
        // Thread-safe non-reentrant check
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        System.IO.Directory.Delete(Path, recursive: true);
    }
}
