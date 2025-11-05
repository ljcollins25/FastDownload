// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json;
using System.Text.Json.Serialization;
using BuildXL.Cache.ContentStore.Hashing;

namespace FastDownload.Shared.Manifest.V0
{
    /// <summary>
    /// Adapter for System.Text.Json serialization of <see cref="ContentHash"/>.
    /// </summary>
    public class ContentHashJsonConverter : JsonConverter<ContentHash>
    {
        public override ContentHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? serialized = reader.GetString();
            if (string.IsNullOrEmpty(serialized))
            {
                throw new JsonException($"Expected to find a {nameof(ContentHash)} but found null or empty string instead");
            }

            if (!ContentHash.TryParse(serialized, out ContentHash hash))
            {
                throw new JsonException($"Attempt to parse a {nameof(ContentHash)} failed");
            }

            return hash;
        }

        public override void Write(Utf8JsonWriter writer, ContentHash value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
