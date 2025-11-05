// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Runtime.CompilerServices;

namespace FastDownload.Utilities
{
    /// <summary>
    /// Global variables that affect the entire program.
    /// </summary>
    internal static class Globals
    {
        /// <summary>
        /// Set when running inside unit test
        /// </summary>
        public static bool IsTest { get; set; }

        /// <summary>
        /// Indicates whether to use native buffers
        /// </summary>
        internal static bool UseNativeBuffers = GetGlobalSwitch();

        /// <summary>
        /// Indicates whether to use plain text flat log layout instead of json layout
        /// </summary>
        public static Switch FlatLogLayout { get; set; } = GetGlobalSwitch(defaultValue: null);

        /// <summary>
        /// Indicates whether to debug peer proxy code
        /// </summary>
        public static bool DebugProxy { get; set; } = GetGlobalSwitch();

        /// <summary>
        /// The correlation ID for the current invocation of the program.
        /// </summary>
        /// <remarks>
        /// The value of this property will be set inside of the manifest when it's generated to allow correlating the
        /// upload and download runs.
        ///
        /// When the download happens, the manifest's <see cref="CorrelationId"/> lands in
        /// <see cref="SourceCorrelationId"/>.
        /// </remarks>
        public static string CorrelationId { get; set; } = string.Empty;

        /// <summary>
        /// The correlation ID of the program that generated the archive that's being downloaded.
        /// </summary>
        /// <remarks>
        /// Only set during download operations.
        /// </remarks>
        public static string SourceCorrelationId { get; set; } = string.Empty;

        /// <summary>
        /// Product name.
        /// </summary>
        /// <remarks>
        /// Centralized here because it's used in a bunch of places.
        /// </remarks>
        public static string ProductName { get; } = "FastDownload";

        /// <summary>
        /// Product version.
        /// </summary>
        public static VersionInfo? ProductVersion { get; private set; }

        static Globals()
        {
            VersionInfo.TryParseFromAssemblyVersion(out var versionInfo);
            ProductVersion = versionInfo;
        }

        private static Switch GetGlobalSwitch([CallerMemberName] string name = null!, bool allowPrint = true, bool? defaultValue = false)
        {
            var value = GetGlobalVariable(name, allowPrint: false);
            var result = string.IsNullOrEmpty(value) ? defaultValue : (value == "1" || "true".Equals(value, StringComparison.OrdinalIgnoreCase));

            if (allowPrint && GetGlobalSwitch("PrintGlobals", allowPrint: false))
            {
                Console.WriteLine($"Global switch: '{name}' == '{result}' (raw value: '{value}')");
            }

            return result;
        }

        private static string? GetGlobalVariable([CallerMemberName] string name = null!, bool allowPrint = true)
        {
            var variableName = "FASTDOWNLOAD_" + name;
            var value = Environment.GetEnvironmentVariable(variableName);

            if (allowPrint && GetGlobalSwitch("PrintGlobals", allowPrint: false))
            {
                Console.WriteLine($"Global variable: '{variableName}' == '{value}'");
            }

            return value;
        }

        public record struct Switch(bool Value, bool HasValue = true)
        {
            public static implicit operator bool(Switch s) => s.Value;
            public static implicit operator Switch(bool? value) => new(value.GetValueOrDefault(), value.HasValue);
            public bool GetValueOrDefault(bool defaultValue) => HasValue ? Value : defaultValue;
        }
    }
}
