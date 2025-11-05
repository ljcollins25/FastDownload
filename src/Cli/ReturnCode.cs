// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Cli
{
    internal enum ReturnCode
    {
        Success = 0,
        UnhandledException = 1,
        NotFound = 2,
        Throttled = 3,
        Cancelled = 4,
        Timeout = 5,
    }
}
