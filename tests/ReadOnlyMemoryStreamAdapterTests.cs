// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Utilities;

namespace FastDownload.Tests
{
    [TestClass]
    public class ReadOnlyMemoryStreamAdapterTests
    {
        [TestMethod]
        public void Constructor_ShouldInitializeCorrectly()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            Assert.AreEqual(0, stream.Position);
            Assert.AreEqual(3, stream.Length);
            Assert.IsTrue(stream.CanRead);
            Assert.IsTrue(stream.CanSeek);
            Assert.IsFalse(stream.CanWrite);
        }

        [TestMethod]
        public void Read_ShouldReturnCorrectData()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3, 4, 5]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            var buffer = new byte[3];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, buffer);
            Assert.AreEqual(3, bytesRead);
            Assert.AreEqual(3, stream.Position);
        }

        [TestMethod]
        public void Read_WithInvalidOffset_ShouldThrowException()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            var buffer = new byte[2];

            // Invalid offset
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => stream.ReadExactly(buffer, 2, 2));
        }

        [TestMethod]
        public void Read_WithNullBuffer_ShouldThrowException()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            Assert.ThrowsExactly<ArgumentNullException>(() => stream.ReadExactly(null, 0, 2));
        }

        [TestMethod]
        public void Seek_ShouldUpdatePosition()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3, 4, 5]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            stream.Seek(2, SeekOrigin.Begin);
            Assert.AreEqual(2, stream.Position);

            stream.Seek(1, SeekOrigin.Current);
            Assert.AreEqual(3, stream.Position);

            stream.Seek(-2, SeekOrigin.End);
            Assert.AreEqual(3, stream.Position);
        }

        [TestMethod]
        public void Seek_WithInvalidPosition_ShouldThrowException()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
        }

        [TestMethod]
        public void Position_SetValidValue_ShouldUpdatePosition()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            stream.Position = 2;
            Assert.AreEqual(2, stream.Position);
        }

        [TestMethod]
        public void Position_SetInvalidValue_ShouldThrowException()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => stream.Position = 5);
        }

        [TestMethod]
        public void Write_ShouldThrowNotSupportedException()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            Assert.ThrowsExactly<NotSupportedException>(() => stream.Write([4, 5], 0, 2));
        }

        [TestMethod]
        public void SetLength_ShouldThrowNotSupportedException()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(5));
        }

        [TestMethod]
        public void Flush_ShouldNotThrow()
        {
            var memory = new ReadOnlyMemory<byte>([1, 2, 3]);
            var stream = new ReadOnlyMemoryStreamAdapter(memory);

            stream.Flush();
        }
    }
}
