// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Utilities;

public static class Constants
{
    public const string Predecessor = nameof(Predecessor);
    public const string StartWatch = nameof(StartWatch);
    public const string WorkItem = nameof(WorkItem);

    public static readonly long TotalStartMemoryGb = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20);
}
