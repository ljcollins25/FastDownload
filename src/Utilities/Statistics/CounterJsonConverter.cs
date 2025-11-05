// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace FastDownload.Utilities;

public class CounterJsonConverter : JsonConverter<Counter>
{
    public override Counter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // We don't implement this because we should never ever deserialize a Counter.
        throw new NotSupportedException("Deserialization is not supported for Counter.");
    }

    public override void Write(Utf8JsonWriter writer, Counter value, JsonSerializerOptions options)
    {
        var counter = Interlocked.Read(ref value.Value);
        if (value.IsTime)
        {
            writer.WriteNumberValue(TimeSpan.FromTicks(counter).TotalSeconds);
        }
        else
        {
            writer.WriteNumberValue(counter);
        }
    }
}
