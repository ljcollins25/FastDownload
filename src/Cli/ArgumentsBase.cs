// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities.Core.Tracing;
using FastDownload.Utilities;
using NLog;
using Polly.Timeout;

namespace FastDownload.Cli;

internal abstract record ArgumentsBase
{
    protected abstract Logger Logger { get; }

    public required string CorrelationId;

    public double ExecutionTimeoutMinutes;

    public TimeSpan ExecutionTimeout => TimeSpan.FromMinutes(ExecutionTimeoutMinutes);

    public bool Verbose;

    public Statistics Statistics { get; init; } = new();

    public async Task<ReturnCode> RunAsync(CancellationToken ctrlC)
    {
        if (Verbose)
        {
            Program.SetMinimumLogLevel(LogLevel.Trace);
        }

        LogProgramVersion(CorrelationId);

        using var executionTimeoutSource = new CancellationTokenSource(ExecutionTimeout);
        using var externalCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(executionTimeoutSource.Token, ctrlC);
        var externalCancellationToken = externalCancellationSource.Token;

        var stopwatch = StopwatchSlim.Start();
        try
        {
            // This is used to internally determine that we need to stop execution and halt it. For example, if there's
            // an error in one of the download tasks, we still want to cancel all of them.
            using var internalCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
            var internalCancellationToken = internalCancellationSource.Token;

            var performanceCollectorTask = PerformanceCountersCollector.RunAsync(internalCancellationToken);
            try
            {
                await RunCoreAsync(Statistics, internalCancellationSource, internalCancellationToken);
            }
            finally
            {
                // We trigger the internal cancellation source because at this point in the program, there's no more
                // work to do. Any remaining work should be cancelled and cleaned up.
                internalCancellationSource.Cancel();

                // The collection task should exit after the above cancellation, so we wait for it to complete. This
                // also prevents UnobservedTaskException in the event that it fails.
                await performanceCollectorTask;
            }

            return ReturnCode.Success;
        }
        catch (OperationCanceledException) when (ctrlC.IsCancellationRequested)
        {
            return OnControlC();
        }
        catch (OperationCanceledException) when (externalCancellationToken.IsCancellationRequested)
        {
            // Happens when the timeout is reached by the cancellation token above.
            return OnTimeout();
        }
        catch (AggregateException aex) when (aex.InnerExceptions.Any(ex => ex is TimeoutRejectedException))
        {
            // Happens when the timeout is reached inside of Polly's TimeoutPolicy.
            return OnTimeout();
        }
        catch (Exception ex)
        {
            return OnUnhandledException(ex);
        }
        finally
        {
            Statistics.Increment(TimeCounters.TotalExecutionTime, stopwatch.Elapsed);
            Statistics.LogStatisticsReport(Globals.FlatLogLayout);
        }
    }

    private void LogProgramVersion(string correlationId)
    {
        Globals.CorrelationId = correlationId;

        var version = Globals.ProductVersion;
        if (!string.IsNullOrEmpty(version?.Source))
        {
            var programVersion = version.ShortHash;
            if (string.IsNullOrEmpty(programVersion))
            {
                programVersion = version.LongHash;
            }

            if (string.IsNullOrEmpty(programVersion))
            {
                programVersion = version.Source;
            }

            if (string.IsNullOrEmpty(programVersion))
            {
                programVersion = "Unknown";
            }

            LogManager.Configuration.Variables["program-version"] = programVersion;

            Logger
                .ForInfoEvent()
                .Message("Running {ProductName} version: {ProgramVersion}", Globals.ProductName, version.Source ?? "Unknown")
                .Log();
        }
        else
        {
            LogManager.Configuration.Variables["program-version"] = "Unknown";

            Logger
                .ForWarnEvent()
                .Message("Couldn't determine {ProductName}'s version. This likely means there's some issue with the build process.", Globals.ProductName)
                .Log();
        }
    }

    protected abstract Task RunCoreAsync(Statistics statistics, CancellationTokenSource internalCancellationSource, CancellationToken internalCancellationToken);

    protected abstract ReturnCode OnUnhandledException(Exception ex);

    protected abstract ReturnCode OnTimeout();

    protected abstract ReturnCode OnControlC();
}
