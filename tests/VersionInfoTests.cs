// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Utilities;

namespace FastDownload.Tests
{
    [TestClass]
    public class VersionInfoTests
    {
        [TestMethod]
        public void TryParse_FromAssemblyVersion_Succeeds()
        {
            var assemblyVersion = VersionInfo.GetAssemblyVersion();

            if (string.IsNullOrEmpty(assemblyVersion))
            {
                Assert.Inconclusive("Assembly version is not set.");
            }

            bool result = VersionInfo.TryParseFromAssemblyVersion(out var versionInfo);
            Assert.IsTrue(result, $"Failed to parse {nameof(VersionInfo)} from {VersionInfo.GetAssemblyVersion() ?? "UNKNOWN"}");
            Assert.IsNotNull(versionInfo);
        }

        [TestMethod]
        public void TryParseDebug_ValidDebugVersion_ReturnsTrueAndParsesCorrectly()
        {
            string validDebugVersion = "1.0.0+2b92d677b034173848ef88f604a35c1dc40f7be0";
            bool result = VersionInfo.TryParseDebug(validDebugVersion, out var versionInfo);

            Assert.IsTrue(result);
            Assert.IsNotNull(versionInfo);
            Assert.AreEqual(null, versionInfo?.Date);
            Assert.AreEqual("2b92d677", versionInfo?.ShortHash);
            Assert.AreEqual("2b92d677b034173848ef88f604a35c1dc40f7be0", versionInfo?.LongHash);
        }

        [TestMethod]
        public void TryParseDebug_InvalidDebugVersion_ReturnsFalse()
        {
            string invalidDebugVersion = "1.0.0+INVALIDHASH";
            bool result = VersionInfo.TryParseDebug(invalidDebugVersion, out var versionInfo);

            Assert.IsFalse(result);
            Assert.IsNull(versionInfo);
        }

        [TestMethod]
        public void TryParseDebug_EmptyString_ReturnsFalse()
        {
            string emptyString = string.Empty;
            bool result = VersionInfo.TryParseDebug(emptyString, out var versionInfo);

            Assert.IsFalse(result);
            Assert.IsNull(versionInfo);
        }

        [TestMethod]
        public void TryParseDebug_MissingLongHash_ReturnsFalse()
        {
            string missingLongHashVersion = "1.0.0+";
            bool result = VersionInfo.TryParseDebug(missingLongHashVersion, out var versionInfo);

            Assert.IsFalse(result);
            Assert.IsNull(versionInfo);
        }

        [TestMethod]
        public void TryParseDebug_ExtraCharacters_ReturnsFalse()
        {
            string extraCharactersVersion = "1.0.0+2b92d677b034173848ef88f604a35c1dc40f7be0EXTRA";
            bool result = VersionInfo.TryParseDebug(extraCharactersVersion, out var versionInfo);

            Assert.IsFalse(result);
            Assert.IsNull(versionInfo);
        }

        [TestMethod]
        public void TryParse_ValidVersionString_ReturnsTrueAndParsesCorrectly()
        {
            string validVersion = "2024.11.18-76d7c6f9+76d7c6f9b9fd937b3a9990b1c5476e59c23e7d5a";

            bool result = VersionInfo.TryParse(validVersion, out var versionInfo);

            Assert.IsTrue(result, "Parsing should succeed for a valid version string.");
            Assert.IsNotNull(versionInfo, "VersionInfo should not be null.");
            Assert.AreEqual(new DateTime(2024, 11, 18), versionInfo?.Date);
            Assert.AreEqual("76d7c6f9", versionInfo?.ShortHash);
            Assert.AreEqual("76d7c6f9b9fd937b3a9990b1c5476e59c23e7d5a", versionInfo?.LongHash);
        }

        [TestMethod]
        public void TryParse_InvalidVersionString_ReturnsFalse()
        {
            string invalidVersion = "invalid.version.string";

            bool result = VersionInfo.TryParse(invalidVersion, out var versionInfo);

            Assert.IsFalse(result, "Parsing should fail for an invalid version string.");
            Assert.IsNull(versionInfo, "VersionInfo should be null when parsing fails.");
        }

        [TestMethod]
        public void TryParse_InvalidDate_ReturnsFalse()
        {
            string invalidDateVersion = "2024.02.30-76d7c6f9+76d7c6f9b9fd937b3a9990b1c5476e59c23e7d5a"; // February 30th is invalid.

            bool result = VersionInfo.TryParse(invalidDateVersion, out var versionInfo);

            Assert.IsFalse(result, "Parsing should fail for a version string with an invalid date.");
            Assert.IsNull(versionInfo, "VersionInfo should be null when the date is invalid.");
        }

        [TestMethod]
        public void TryParse_MissingHash_ReturnsFalse()
        {
            string missingHashVersion = "2024.11.18-+76d7c6f9b9fd937b3a9990b1c5476e59c23e7d5a";

            bool result = VersionInfo.TryParse(missingHashVersion, out var versionInfo);

            Assert.IsFalse(result, "Parsing should fail for a version string with a missing short hash.");
            Assert.IsNull(versionInfo, "VersionInfo should be null when the short hash is missing.");
        }

        [TestMethod]
        public void TryParse_ExtraCharacters_ReturnsFalse()
        {
            string extraCharactersVersion = "2024.11.18-76d7c6f9+76d7c6f9b9fd937b3a9990b1c5476e59c23e7d5aEXTRA";

            bool result = VersionInfo.TryParse(extraCharactersVersion, out var versionInfo);

            Assert.IsFalse(result, "Parsing should fail for a version string with extra characters.");
            Assert.IsNull(versionInfo, "VersionInfo should be null when the version string contains extra characters.");
        }
    }
}
