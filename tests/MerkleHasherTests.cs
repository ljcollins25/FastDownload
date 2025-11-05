// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Shared;
using FastDownload.Utilities;

namespace FastDownload.Tests
{
    [TestClass]
    public class MerkleHasherTests
    {
        private IContentHasher _hasher;

        [TestInitialize]
        public void TestInitialize()
        {
            // Initialize a hasher to use (e.g., SHA256)
            _hasher = HashInfoLookup.GetContentHasher(HashType.SHA256);
        }

        [TestMethod]
        public void SameOrderingProducesSameHash()
        {
            var blocks = new List<MerkleHasher.Block>
            {
                CreateBlock(0),
                CreateBlock(1),
                CreateBlock(2),
            };

            var merkleHasher1 = new MerkleHasher();
            merkleHasher1.AddMany(blocks);
            var hash1 = merkleHasher1.Compute(_hasher);

            // Compute again with the same ordering
            var merkleHasher2 = new MerkleHasher();
            merkleHasher2.AddMany(blocks);
            var hash2 = merkleHasher2.Compute(_hasher);

            Assert.AreEqual(hash1, hash2, "The hashes should match when the same ordering is used.");
        }

        [TestMethod]
        public void DifferentOrderingSameIdProducesSameHash()
        {
            var blocksInOrder = new List<MerkleHasher.Block>
            {
                CreateBlock(0),
                CreateBlock(1),
                CreateBlock(2),
            };

            var blocksReordered = new List<MerkleHasher.Block>
            {
                CreateBlock(2),
                CreateBlock(1),
                CreateBlock(0),
            };

            // Compute hash for the first ordering
            var merkleHasher1 = new MerkleHasher();
            merkleHasher1.AddMany(blocksInOrder);
            var hashInOrder = merkleHasher1.Compute(_hasher);

            // Compute hash for the second ordering
            var merkleHasher2 = new MerkleHasher();
            merkleHasher2.AddMany(blocksReordered);
            var hashReordered = merkleHasher2.Compute(_hasher);

            Assert.AreEqual(hashInOrder, hashReordered, "The hashes should NOT differ when the order of blocks changes.");
        }

        [TestMethod]
        public void EmptyQueueProducesConsistentHash()
        {
            var merkleHasher1 = new MerkleHasher();
            var hash1 = merkleHasher1.Compute(_hasher);

            var merkleHasher2 = new MerkleHasher();
            var hash2 = merkleHasher2.Compute(_hasher);

            Assert.AreEqual(hash1, hash2, "Empty queues should produce consistent hashes.");
        }

        [TestMethod]
        public void SameContentHashDifferentCompressionAlgorithms()
        {
            var block1 = CreateBlock(0, compression: CompressionAlgorithm.None);
            var block2 = CreateBlock(0, compression: CompressionAlgorithm.GZip);

            var merkleHasher1 = new MerkleHasher();
            merkleHasher1.AddMany(new[] { block1 });
            var hash1 = merkleHasher1.Compute(_hasher);

            var merkleHasher2 = new MerkleHasher();
            merkleHasher2.AddMany(new[] { block2 });
            var hash2 = merkleHasher2.Compute(_hasher);

            Assert.AreNotEqual(hash1, hash2, "Blocks with the same content hash but different compression algorithms should produce different hashes.");
        }

        [TestMethod]
        public void CantDuplicateBlocks()
        {
            var block = CreateBlock(0);

            var merkleHasher1 = new MerkleHasher();
            merkleHasher1.AddMany(new[] { block });
            var hash1 = merkleHasher1.Compute(_hasher);

            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                var merkleHasher2 = new MerkleHasher();
                merkleHasher2.AddMany(new[] { block, block });
                var hash2 = merkleHasher2.Compute(_hasher);
            });
        }

        [TestMethod]
        public void HandlesLargeNumberOfBlocks()
        {
            var blocks = new List<MerkleHasher.Block>();
            for (int i = 0; i < 10000; i++)
            {
                blocks.Add(CreateBlock(i, i.ToString()));
            }

            var merkleHasher = new MerkleHasher();
            merkleHasher.AddMany(blocks);

            ContentHash hash;
            try
            {
                hash = merkleHasher.Compute(_hasher);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Exception occurred while hashing a large number of blocks: {ex.Message}");
                return;
            }

            Assert.IsNotNull(hash, "Hash should not be null for a large number of blocks.");
        }

        /// <summary>
        /// Helper method to create a block with a specific ID and content string.
        /// </summary>
        private static MerkleHasher.Block CreateBlock(int id, string? content = null, CompressionAlgorithm compression = CompressionAlgorithm.None)
        {
            if (content is null)
            {
                content = "A";
            }

            var hashType = HashType.SHA256;
            var hasher = HashInfoLookup.GetContentHasher(hashType);
            var contentHash = hasher.GetContentHash(Encoding.UTF8.GetBytes(content));
            return new MerkleHasher.Block(
                Id: id,
                ContentHash: contentHash,
                Destination: new Chunk(Start: id * 100, End: id * 100 + 50),
                Compression: compression);
        }
    }
}
