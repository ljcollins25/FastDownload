// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Shared;
using Shouldly;

namespace FastDownload.Tests
{
    [TestClass]
    public class ChunkTests
    {
        [TestMethod]
        public void ChunkComparison()
        {
            var chunk1 = new Chunk(0, 10);
            var chunk2 = new Chunk(5, 15);
            var chunk3 = new Chunk(10, 20);

            Assert.IsTrue(chunk1.CompareTo(chunk2) < 0);
            Assert.IsTrue(chunk2.CompareTo(chunk3) < 0);
            Assert.IsTrue(chunk1.CompareTo(chunk3) < 0);
        }

        [TestMethod]
        public void ChunkSplitSimple()
        {
            var fileSize = 100;
            var chunks = 10;
            uint chunkSize = 10;
            uint alignment = 1;

            var chunkList = Chunk.Split(fileSize, chunks, chunkSize, alignment).ToList();

            Assert.AreEqual(chunks, chunkList.Count);
            Assert.AreEqual(0, chunkList[0].Start);
            Assert.AreEqual(10, chunkList[0].End);
            Assert.AreEqual(90, chunkList[9].Start);
            Assert.AreEqual(100, chunkList[9].End);
        }

        [TestMethod]
        public void ChunkSplitExactAlignment()
        {
            var fileSize = 100;
            var chunks = 4;
            uint chunkSize = 25;
            uint alignment = 5;

            var chunkList = Chunk.Split(fileSize, chunks, chunkSize, alignment).ToList();

            Assert.AreEqual(chunks, chunkList.Count);
            Assert.AreEqual(0, chunkList[0].Start);
            Assert.AreEqual(25, chunkList[0].End);
            Assert.AreEqual(75, chunkList[3].Start);
            Assert.AreEqual(100, chunkList[3].End);

            foreach (var chunk in chunkList)
            {
                Assert.IsTrue(chunk.Start % alignment == 0);
                Assert.IsTrue(chunk.Length % alignment == 0);
            }
        }

        [TestMethod]
        public void ChunkSplitLargeFileSmallChunks()
        {
            var fileSize = 1000;
            var chunks = 100;
            uint chunkSize = 10;
            uint alignment = 2;

            var chunkList = Chunk.Split(fileSize, chunks, chunkSize, alignment).ToList();

            Assert.AreEqual(chunks, chunkList.Count);

            foreach (var chunk in chunkList)
            {
                Assert.IsTrue(chunk.Start % alignment == 0);
                Assert.IsTrue(chunk.Length % alignment == 0);
            }

            Assert.AreEqual(0, chunkList[0].Start);
            Assert.AreEqual(10, chunkList[0].End);
            Assert.AreEqual(990, chunkList[99].Start);
            Assert.AreEqual(1000, chunkList[99].End);
        }

        [TestMethod]
        public void ChunkSplitNonAlignedChunkStart()
        {
            var fileSize = 90;
            var chunks = 3;
            uint chunkSize = 30;
            uint alignment = 20;

            Assert.ThrowsExactly<ArgumentException>(() =>
            {
                _ = Chunk.Split(fileSize, chunks, chunkSize, alignment).ToList();
            });
        }

        [TestMethod]
        public void ChunkSplitChunkSizeExceedsFileSize()
        {
            var fileSize = 100;
            var chunks = 5;
            uint chunkSize = 30;
            uint alignment = 1;

            Assert.ThrowsExactly<ArgumentException>(() =>
            {
                _ = Chunk.Split(fileSize, chunks, chunkSize, alignment).ToList();
            });
        }

        [TestMethod]
        public void ChunkSplitAlignmentZero()
        {
            var fileSize = 100;
            var chunks = 4;
            uint chunkSize = 25;
            uint alignment = 0;

            Assert.ThrowsExactly<ArgumentException>(() =>
            {
                _ = Chunk.Split(fileSize, chunks, chunkSize, alignment).ToList();
            });
        }

        [TestMethod]
        public void ChunkSplitNegativeFileSize()
        {
            var fileSize = -10;
            var chunks = 5;
            uint chunkSize = 2;
            uint alignment = 1;

            Assert.ThrowsExactly<ArgumentException>(() =>
            {
                _ = Chunk.Split(fileSize, chunks, chunkSize, alignment).ToList();
            });
        }

        [TestMethod]
        public void Chunk_AlignTo()
        {
            new Chunk(0, 14).AlignTo(4).ShouldBe(new Chunk(0, 16));
            new Chunk(3, 14).AlignTo(4).ShouldBe(new Chunk(0, 16));
            new Chunk(4, 14).AlignTo(4).ShouldBe(new Chunk(4, 16));
            new Chunk(2, 15).AlignTo(4).ShouldBe(new Chunk(0, 16));
            new Chunk(1000, 65000).AlignTo(4096).ShouldBe(new Chunk(0, 1 << 16));
        }

        [TestMethod]
        public void Chunk_Align()
        {
            Chunk.Align(0, 4, roundUp: true).ShouldBe(0);
            Chunk.Align(3, 4, roundUp: true).ShouldBe(4);
            Chunk.Align(4, 4, roundUp: true).ShouldBe(4);
            Chunk.Align(2, 4, roundUp: true).ShouldBe(4);
            Chunk.Align(1000, 4096, roundUp: true).ShouldBe(4096);
            Chunk.Align(1000, 4096, roundUp: false).ShouldBe(0);
            Chunk.Align(0, 4, roundUp: false).ShouldBe(0);
            Chunk.Align(3, 4, roundUp: false).ShouldBe(0);
        }

        [TestMethod]
        public void Chunk_Count()
        {
            new Chunk(0, 14).AlignTo(4).ShouldBe(new Chunk(0, 16));
            new Chunk(3, 14).AlignTo(4).ShouldBe(new Chunk(0, 16));
            new Chunk(4, 14).AlignTo(4).ShouldBe(new Chunk(4, 16));
            new Chunk(2, 15).AlignTo(4).ShouldBe(new Chunk(0, 16));
            new Chunk(1000, 65000).AlignTo(4096).ShouldBe(new Chunk(0, 1 << 16));
        }

        [TestMethod]
        public void ChunkIntersect_IdenticalChunks()
        {
            var chunkA = new Chunk(10, 20);
            var chunkB = new Chunk(10, 20);

            var intersection = chunkA.Intersect(chunkB);

            intersection.ShouldBe(new Chunk(10, 20));
        }

        [TestMethod]
        public void ChunkIntersect_OverlappingChunks()
        {
            var chunkA = new Chunk(10, 25);
            var chunkB = new Chunk(20, 30);

            var intersection = chunkA.Intersect(chunkB);

            intersection.ShouldBe(new Chunk(20, 25));
        }

        [TestMethod]
        public void ChunkIntersect_AdjacentChunks()
        {
            var chunkA = new Chunk(0, 10);
            var chunkB = new Chunk(10, 20);

            var intersection = chunkA.Intersect(chunkB);

            intersection.ShouldBe(new Chunk(10, 10));
        }

        [TestMethod]
        public void ChunkIntersect_NonOverlappingChunks()
        {
            var chunkA = new Chunk(0, 5);
            var chunkB = new Chunk(10, 15);

            var intersection = chunkA.Intersect(chunkB);

            intersection.ShouldBe(null);
        }

        [TestMethod]
        public void ChunkIntersect_EnclosedChunk()
        {
            var chunkA = new Chunk(0, 20);
            var chunkB = new Chunk(5, 15);

            var intersection = chunkA.Intersect(chunkB);

            intersection.ShouldBe(new Chunk(5, 15));
        }

        [TestMethod]
        public void ChunkIntersect_ReverseOrder()
        {
            var chunkA = new Chunk(30, 40);
            var chunkB = new Chunk(10, 35);

            var intersection = chunkA.Intersect(chunkB);

            intersection.ShouldBe(new Chunk(30, 35));
        }

        [TestMethod]
        public void ChunkUnion_IdenticalChunks()
        {
            var chunkA = new Chunk(10, 20);
            var chunkB = new Chunk(10, 20);

            var union = chunkA.Union(chunkB);

            union.ShouldBe(new Chunk(10, 20));
        }

        [TestMethod]
        public void ChunkUnion_OverlappingChunks()
        {
            var chunkA = new Chunk(10, 20);
            var chunkB = new Chunk(15, 25);

            var union = chunkA.Union(chunkB);

            union.ShouldBe(new Chunk(10, 25));
        }

        [TestMethod]
        public void ChunkUnion_AdjacentChunks()
        {
            var chunkA = new Chunk(0, 10);
            var chunkB = new Chunk(10, 20);

            var union = chunkA.Union(chunkB);

            union.ShouldBe(new Chunk(0, 20));
        }

        [TestMethod]
        public void ChunkUnion_NonOverlappingChunks()
        {
            var chunkA = new Chunk(0, 5);
            var chunkB = new Chunk(10, 15);

            var union = chunkA.Union(chunkB);

            union.ShouldBe(new Chunk(0, 15));
        }

        [TestMethod]
        public void ChunkUnion_EnclosedChunk()
        {
            var chunkA = new Chunk(0, 20);
            var chunkB = new Chunk(5, 15);

            var union = chunkA.Union(chunkB);

            union.ShouldBe(new Chunk(0, 20));
        }

        [TestMethod]
        public void ChunkUnion_ReverseOrder()
        {
            var chunkA = new Chunk(30, 40);
            var chunkB = new Chunk(10, 35);

            var union = chunkA.Union(chunkB);

            union.ShouldBe(new Chunk(10, 40));
        }
    }
}
