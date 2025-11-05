// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Runtime.CompilerServices;
using Azure.Storage.Blobs.Models;
using FastDownload.Utilities.Metadata;

namespace FastDownload.Utilities;

/// <summary>
/// Accesses metadata about a semaphore or proxy peer entry in a strongly-typed manner
/// </summary>
/// <param name="Metadata">the underlying metadata dictionary</param>
/// <param name="Data">the blob info</param>
public record struct SemaphoreEntry(IDictionary<string, string> Metadata, SemaphoreEntry.BlobData Data)
{
    internal BlobData Data = Data;

    public DateTimeOffset LastModifiedTime => TouchTime.NullIfDefault() ?? Data.LastModified;

    /// <summary>
    /// The amount of time allowed before the entry is considered expired
    /// </summary>
    public TimeSpan Timeout { get => GetValueOrDefault(); set => SetValue(value); }

    public Guid Id { get => GetValueOrDefault(); set => SetValue(value); }

    /// <summary>
    /// The initial time for requesting semaphore
    /// </summary>
    public DateTimeOffset EnqueueTime { get => GetEffectiveTime(Enqueued); set => SetValue(value); }

    /// <summary>
    /// The initial time for requesting inclusion in the proxy p2p download chain
    /// </summary>
    public DateTimeOffset EnqueueProxyTime { get => GetEffectiveTime(EnqueuedProxy); set => SetValue(value); }

    /// <summary>
    /// Gets whether the semaphore is enqueued
    /// </summary>
    public bool Enqueued { get => GetValueOrDefault(); set => SetValue(value); }

    /// <summary>
    /// Gets whether the proxy peer is enqueued in the download chain list
    /// </summary>
    public bool EnqueuedProxy { get => GetValueOrDefault(); set => SetValue(value); }

    /// <summary>
    /// Gets zone of the machine
    /// </summary>
    public string Zone { get => GetValueOrDefault(); set => SetValue(value); }

    /// <summary>
    /// Gets checksum which uniquely identifies the file content
    /// </summary>
    public string FileChecksum { get => GetValueOrDefault(); set => SetValue(value); }

    /// <summary>
    /// Gets the hash of the TLS certificate used when communicating with this machine
    /// </summary>
    public string CertHash { get => GetValueOrDefault(); set => SetValue(value); }

    /// <summary>
    /// Gets the machine uri for proxy builds
    /// </summary>
    public Uri MachineUri { get => GetValueOrDefault(); set => SetValue(value); }

    /// <summary>
    /// For testing purposes. This uses the virtual time rather than actual time to avoid unnecessary waits in tests
    /// </summary>
    public DateTimeOffset TouchTime { get => GetValueOrDefault(); set => SetValue(value); }

    /// <summary>
    /// The number of keep alive updates
    /// </summary>
    public long UpdateCount { get => GetValueOrDefault(); set => SetValue(value); }

    public string ToProxyString(Guid? matchId = null)
    {
        var machineUri = EnqueuedProxy ? MachineUri : null;
        return $"{machineUri?.Host} IsProxy={EnqueuedProxy} ProxyTime={EnqueueProxyTime:o} CertHash={CertHash} {(matchId == Id).Then(" ***")}";
    }

    public void SetValue(MetadataValue value, [CallerMemberName] string key = null!)
    {
        if (!string.IsNullOrEmpty(value.StringValue))
        {
            Metadata[key] = value;
        }
    }

    public DateTimeOffset GetEffectiveTime(bool isStarted, [CallerMemberName] string key = null!)
    {
        if (isStarted)
        {
            var value = GetValueOrDefault(key: key);
            DateTimeOffset result;
            if (value.StringValue == null)
            {
                result = LastModifiedTime;
                if (Data.StoreEffectiveTimes)
                {
                    SetValue(result, key: key);
                }
            }
            else
            {
                result = value;
            }

            return result;
        }

        return default;
    }

    public MetadataValue GetValueOrDefault([CallerMemberName] string key = null!)
    {
        if (Metadata.TryGetValue(key, out var stringValue))
        {
            return stringValue;
        }
        else
        {
            return default;
        }
    }

    public record struct BlobData(string Name, DateTimeOffset LastModified, bool StoreEffectiveTimes = false)
    {
        public DateTimeOffset LastModified { get; set; } = LastModified;

        public static implicit operator BlobData(BlobItem value) => new BlobData(Name: value.Name, LastModified: GetLastModifiedTime(value));
    }

    internal static DateTimeOffset? ParseSnapshotTime(string? snapshotId)
    {
        const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        return snapshotId == null
            ? null
            : DateTimeOffset.ParseExact(snapshotId, Format, null);
    }

    internal static DateTimeOffset GetLastModifiedTime(BlobItem blobItem)
    {
        return ParseSnapshotTime(blobItem.Snapshot) ?? blobItem.Properties.LastModified!.Value;
    }

    internal static DateTimeOffset GetLastModifiedTime(BlobSnapshotInfo blobItem)
    {
        return ParseSnapshotTime(blobItem.Snapshot)!.Value;
    }
}