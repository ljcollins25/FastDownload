// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Net.Http.Headers;
using System.Reflection;
using System.Text.RegularExpressions;

namespace FastDownload.Utilities
{
    public record VersionInfo(DateTime? Date, string ShortHash, string LongHash, string? Source = null)
    {
        private static readonly Regex VersionPattern = new Regex(
            @"^(?<Year>\d{4})\.(?<Month>\d{2})\.(?<Day>\d{2})-(?<ShortHash>[a-f0-9]{8})\+(?<LongHash>[a-f0-9]{40})$",
            RegexOptions.Compiled);

        private static readonly Regex DebugVersionPattern = new Regex(
            @"^1\.0\.0\+(?<LongHash>[a-f0-9]{40})$",
            RegexOptions.Compiled);

        public static bool TryParse(string version, out VersionInfo? result)
        {
            result = null;

            var match = VersionPattern.Match(version);

            if (!match.Success)
            {
                return false;
            }

            if (!int.TryParse(match.Groups["Year"].Value, out int year) ||
                !int.TryParse(match.Groups["Month"].Value, out int month) ||
                !int.TryParse(match.Groups["Day"].Value, out int day))
            {
                return false;
            }

            try
            {
                // Use DateTime constructor to create the date
                var date = new DateTime(year, month, day);

                string shortHash = match.Groups["ShortHash"].Value;
                string longHash = match.Groups["LongHash"].Value;

                result = new VersionInfo(date, shortHash, longHash, version);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Handle invalid date (e.g., February 30)
                return false;
            }
        }

        public static bool TryParseDebug(string version, out VersionInfo? result)
        {
            result = null;

            var match = DebugVersionPattern.Match(version);

            if (!match.Success)
            {
                return false;
            }

            string longHash = match.Groups["LongHash"].Value;
            string shortHash = longHash.Substring(0, 8);

            result = new VersionInfo(Date: null, shortHash, longHash, version);
            return true;
        }

        public static bool TryParseFromAssemblyVersion(out VersionInfo? versionInfo)
        {
            versionInfo = null;
            string? informationalVersion = GetAssemblyVersion();
            if (string.IsNullOrEmpty(informationalVersion))
            {
                return false;
            }

            if (informationalVersion.StartsWith("1.0.0+"))
            {
                return TryParseDebug(informationalVersion, out versionInfo);
            }

            return TryParse(informationalVersion, out versionInfo);
        }

        internal static string? GetAssemblyVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return informationalVersion;
        }

        private static string GenerateShortProductVersionString()
        {
            var defaultVersion = "1.0";
            string? productVersion = Globals.ProductVersion?.ShortHash ?? defaultVersion;
            if (string.IsNullOrEmpty(productVersion))
            {
                productVersion = Globals.ProductVersion?.Date?.ToString("yyyyMMdd");
            }

            if (string.IsNullOrEmpty(productVersion))
            {
                productVersion = defaultVersion;
            }

            return productVersion;
        }

        private static string GenerateLongProductVersionString()
        {
            var defaultVersion = "1.0";
            string productVersion = Globals.ProductVersion?.Source ?? defaultVersion;
            if (string.IsNullOrEmpty(productVersion))
            {
                productVersion = defaultVersion;
            }

            return productVersion;
        }

        public static ProductInfoHeaderValue GenerateProductInfoHeader()
        {
            string productVersion = GenerateLongProductVersionString();

            return new ProductInfoHeaderValue(Globals.ProductName, productVersion);
        }

        internal static string GenerateApplicationId()
        {
            return $"{Globals.ProductName}/{GenerateShortProductVersionString()}";
        }
    }
}
