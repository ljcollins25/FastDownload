// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Buffers;
using System.Diagnostics.ContractsLight;
using System.Runtime.InteropServices;
using DotNext.Buffers;
using DotNext.Runtime.InteropServices;
using FastDownload.Download;

#nullable enable

namespace FastDownload.Utilities;

/// <summary>
/// Unbuffered IO has specific alignment requirements for the buffer used to read/write data. Async IO requires
/// that the memory be GC allocated.
///
/// The first requirement means that we need to allocate a buffer with a specific alignment, which is not directly
/// supported by GC. The second requirement means that we can't use unmanaged memory, as there's no way to create a
/// byte[] from a pointer (this means <see cref="NativeMemory.AlignedAlloc(nuint, nuint)"/> can't be used).
///
/// This class's approach is to allocate a buffer with extra space for alignment, pin it to ensure it doesn't get
/// moved around, get it's address and figure out the offset needed to align it.
///
/// This means there's some wastage to each instance of this class (namely, the difference between the .NET
/// allocator's alignment and the one required by the specific instance). This is acceptable, as the buffers are
/// not supposed to be created in large numbers, and that difference should be relatively small.
/// </summary>
internal sealed class AlignedManagedBuffer : IDisposable
{
    /// <summary>
    /// The size of the buffer, in bytes.
    /// </summary>
    public uint Size { get; }

    /// <summary>
    /// The alignment of the buffer's address, in bytes.
    /// </summary>
    public uint Alignment { get; }

    /// <summary>
    /// Memory pointing to the buffer, with the correct alignment.
    /// </summary>
    public Memory<byte> Memory { get; }

    /// <summary>
    /// ReadOnlyMemory pointing to the buffer, with the correct alignment.
    /// </summary>
    public ReadOnlyMemory<byte> ReadOnlyMemory => Memory;

    /// <summary>
    /// GC handle to prevent the <see cref="_buffer"/> from being moved by GC, which would invalidate the alignment
    /// of the <see cref="Memory"/> above.
    /// </summary>
    private MemoryHandle? _handle;

    private byte[]? _buffer;

    private IUnmanagedMemory<byte>? _owner;

    /// <summary>
    /// Offset in the <see cref="_buffer"/> such that the underlying address is aligned to <see cref="Alignment"/>.
    /// </summary>
    private int _offset;

    public RefCountHandle<AlignedManagedBuffer>? RefCountHandle { get; private set; }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <remarks>
    /// Kept private in case it needs to be changed in the future.
    /// </remarks>
    private AlignedManagedBuffer(uint size, uint alignment, bool useNative)
    {
        Contract.Requires(size > 0);
        Contract.Requires(size <= int.MaxValue);
        Contract.Requires(alignment > 0);

        Size = size;
        Alignment = alignment;

        if (useNative)
        {
            _owner = UnmanagedMemory.Allocate<byte>((int)(Size + alignment));
            Memory = _owner.Memory;
        }
        else
        {
            // Allocate a buffer with extra space for alignment, this guarantees that we can always align the buffer's
            // address when representing it as Memory<byte>. We use this specific method to avoid GC heap fragmentation,
            // as GC does not like frequent large allocations, and much less so when they later get pinned. This way, the
            // memory is pre-pinned. We also save a little bit of time by not needing to 0 initialize a buffer that'll get
            // overwritten anyways.
            _buffer = GC.AllocateUninitializedArray<byte>((int)(Size + alignment), pinned: true);

            Memory = _buffer;
        }

        _handle = Memory.Pin();

        unsafe
        {
            var ptr = (nint)_handle!.Value.Pointer;
            var next = (ptr / alignment + 1) * alignment;
            _offset = (int)(next - ptr);

            Contract.Assert((ptr + _offset) % alignment == 0, "Buffer address couldn't be aligned");
            Contract.Assert(_offset + Size <= Memory.Length, "Buffer size is too small");

            Memory = Memory.Slice(_offset, (int)Size);
            Contract.Assert(Memory.Length == Size, "Memory length is incorrect");
        }

        Clear();
    }

    public void ResetRefCount(Action onCleanup)
    {
        Contract.Assert(RefCountHandle == null, "Cannot reset handle when outstanding references exist.");

        RefCountHandle = new(this, () =>
        {
            RefCountHandle = null;
            onCleanup();
        });
    }

    /// <summary>
    /// Dereference the buffer allowing for cleanup when ref count is zero.
    /// </summary>
    public void Release()
    {
        RefCountHandle?.Release();
    }

    /// <summary>
    /// Allocate a new buffer with the given size and alignment.
    /// </summary>
    public static AlignedManagedBuffer Allocate(uint size, uint alignment, bool useNative = false)
    {
        return new AlignedManagedBuffer(size, alignment, useNative);
    }

    /// <summary>
    /// Fill the buffer with zeroes.
    /// </summary>
    private void Clear()
    {
        Memory.Span.Clear();
    }

    /// <summary>
    /// Creates a memory stream pointing to the underlying byte range.
    /// </summary>
    public Stream CreateMemoryStream(bool writable = true, int? length = null)
    {
        if (_buffer != null)
        {
            return new MemoryStream(_buffer, index: _offset, length ?? (int)Size, writable, publiclyVisible: true);
        }
        else
        {
            var ptr = _owner!.Pointer + _offset;
            return ptr.AsStream(length ?? (int)Size, writable ? FileAccess.ReadWrite : FileAccess.Read);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;

        _owner?.Dispose();
        _owner = null;
    }

    /// <inheritdoc />
    ~AlignedManagedBuffer()
    {
        Dispose();
    }
}