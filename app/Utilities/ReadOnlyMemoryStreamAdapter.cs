// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Utilities
{
    public class ReadOnlyMemoryStreamAdapter : Stream
    {
        private readonly ReadOnlyMemory<byte> _memory;
        private int _position;

        public ReadOnlyMemoryStreamAdapter(ReadOnlyMemory<byte> memory)
        {
            _memory = memory;
            _position = 0;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _memory.Length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = (int)value;
            }
        }

        public override void Flush()
        {
            // No-op for a read-only stream
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            int remaining = _memory.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = Math.Min(count, remaining);
            _memory.Slice(_position, toRead).Span.CopyTo(buffer.AsSpan(offset, toRead));
            _position += toRead;
            return toRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentException("Invalid seek origin.", nameof(origin))
            };

            if (newPosition < 0 || newPosition > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            _position = (int)newPosition;
            return _position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("Cannot set length of a read-only stream.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Cannot write to a read-only stream.");
        }
    }
}
