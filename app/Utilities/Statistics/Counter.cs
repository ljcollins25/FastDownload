// Copyright (C) Microsoft Corporation. All Rights Reserved.

#nullable enable

namespace FastDownload.Utilities;

public sealed class Counter
{
    internal readonly bool IsTime;

    internal long Value = 0;

    internal bool Printable => Read() != 0;

    public Counter(bool isTime)
    {
        IsTime = isTime;
    }

    internal long Read()
    {
        return Interlocked.Read(ref Value);
    }

    internal TimeSpan ReadTime()
    {
        return TimeSpan.FromTicks(Read());
    }

    internal long Add(long increment = 1)
    {
        return Interlocked.Add(ref Value, increment);
    }

    internal double Serialize()
    {
        var counter = Read();
        return IsTime ? TimeSpan.FromTicks(counter).TotalSeconds : counter;
    }
}
