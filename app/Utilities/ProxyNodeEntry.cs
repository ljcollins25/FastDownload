// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Utilities;

/// <summary>
/// Information about a proxy peer node
/// </summary>
/// <param name="Uri">the uri for querying chunks</param>
public record ProxyNodeEntry(Uri Uri)
{
    /// <summary>
    /// The Uri for the querying the chunks
    /// </summary>
    public Uri Uri { get; internal set; } = Uri;

    /// <summary>
    /// The unique identifier of the machine
    /// </summary>
    public Guid? Id { get; init; }

    /// <summary>
    /// The azure zone for the machine if known
    /// </summary>
    public required string? Zone { get; init; }

    /// <summary>
    /// The hash of the TLS certificate used by the machine's server
    /// </summary>
    public string? CertHash { get; set; }

    /// <summary>
    /// The unique identifier of the file content served by the machine
    /// </summary>
    public required string FileChecksum { get; init; }

    /// <summary>
    /// Applies data to a semaphore entry
    /// </summary>
    public void ApplyTo(SemaphoreEntry entry)
    {
        entry.MachineUri = Uri;
        entry.Zone = Zone ?? string.Empty;
        entry.CertHash = CertHash ?? string.Empty;
        entry.FileChecksum = FileChecksum ?? string.Empty;
    }

    /// <summary>
    /// Gets data from a semaphore entry
    /// </summary>
    public static ProxyNodeEntry From(SemaphoreEntry entry)
    {
        return new ProxyNodeEntry(entry.MachineUri)
        {
            Id = entry.Id,
            Zone = entry.Zone,
            CertHash = entry.CertHash,
            FileChecksum = entry.FileChecksum
        };
    }
}
