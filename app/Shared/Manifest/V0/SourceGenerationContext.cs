// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json.Serialization;

namespace FastDownload.Shared.Manifest.V0
{
    /// <summary>
    /// This class is required to generate the JSON serialization code for the <see cref="Manifest"/> class. The
    /// generated code is placed in the same namespace as this class, and the reason it's needed is that Native AOT
    /// doesn't support reflection, so we can't use the built-in System.Text.Json source generator.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Manifest))]
    [JsonSerializable(typeof(Block))]
    [JsonSerializable(typeof(Slice))]
    internal partial class SourceGenerationContext : JsonSerializerContext
    {
    }
}
