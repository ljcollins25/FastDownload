// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Shared;
using FastDownload.Utilities;

namespace FastDownload.Upload
{
    public record BlockInfo(string Id, long? Offset, long Length)
    {
        public static BlockInfo Random(long? offset, long length)
        {
            var guid = Guid.NewGuid();
            var blockId = Convert.ToBase64String(guid.ToByteArray());
            if (blockId.Length > 64)
            {
                throw new InvalidOperationException("Generated Block ID is too long for Azure Storage");
            }

            return new(blockId, offset, length);
        }

        public override string ToString()
        {
            if (Offset is null)
            {
                return $"[{Id}](Length = {Length})";
            }
            else
            {
                return $"[{Id}](Offset = {Offset}, Length = {Length})";
            }
        }
    }

    /// <summary>
    /// Information about a block that is being uploaded.
    /// </summary>
    /// <param name="RawMapping">The raw mapping of the chunk to be uploaded.</param>
    /// <param name="CompressionResult">The result of the compression operation for this block.</param>
    internal record BlockUploadData(ChunkMapping RawMapping, CompressionResult CompressionResult)
    {
        /// <summary>
        /// Gets the range in the raw file which is being uploaded.
        /// </summary>
        public Chunk RawBoundary => RawMapping.Chunk;
    }

    /// <summary>
    /// This interface is composed of the basic methods for us to be able to upload a compressed file into a storage
    /// medium.
    /// </summary>
    internal interface IUploadMechanism : IAsyncDisposable
    {
        /// <summary>
        /// Deletes the file that is being uploaded.
        /// </summary>
        /// <remarks>
        /// Called in case of an error during the upload process.
        /// </remarks>
        Task DeleteAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Uploads a block of the file.
        /// </summary>
        /// <param name="blockData">Information about the chunk from the raw file being uploaded (null for the manifest)</param>
        /// <param name="content">the content being uploaded</param>
        /// <param name="cancellationToken">the cancellation token</param>
        Task<BlockInfo> UploadBlockAsync(BlockUploadData? blockData, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

        /// <summary>
        /// Commits the uploaded blocks. The file is considered uploaded after this method is called, and it's expected
        /// that the file is available for download immediatelly afterwards.
        ///
        /// The file is supposed to have it's data in the order specified by <paramref name="blockIds"/>.
        /// </summary>
        /// <remarks>
        /// Not all storage mediums support arbitrary block orderings. For example, Azure Storage allows any arbitrary
        /// ordering, but a file on disk doesn't (as it's already been written). This is dealt with when uploading
        /// individual blocks in <see cref="UploadBlockAsync(ReadOnlyMemory{byte}, CancellationToken)"/>. The returned
        /// <see cref="BlockInfo"/> has to be consistent in order to generate the manifest, but that's it.
        /// </remarks>
        Task CommitAsync(IEnumerable<BlockInfo> blockIds, CancellationToken cancellationToken);

        ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
    }

    internal class NullUploadMechanism : IUploadMechanism, IAsyncDisposable
    {
        private long _offset = 0;

        public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BlockInfo> UploadBlockAsync(BlockUploadData? blockData, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var offset = Interlocked.Add(ref _offset, content.Length);
            offset -= content.Length;
            return Task.FromResult(new BlockInfo(Id: string.Empty, offset, content.Length));
        }

        public Task CommitAsync(IEnumerable<BlockInfo> blockIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
