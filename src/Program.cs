// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.CommandLine;
using System.IO.Compression;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Cli;
using FastDownload.Download;
using FastDownload.Upload;
using FastDownload.Utilities;
using NLog;
using NLog.Layouts;
using NLog.Targets;

namespace FastDownload;

public sealed class Program
{
    public static async Task<int> Main(params string[] args)
    {
        int returnCode = int.MaxValue; // replaced by the command's return code, or by the parser's when no command ran (usage errors must not exit 0)

        // For back-compat purposes, use structured log layout by default when correlation id is specified
        if (args.Length > 0 && !args.Any(a => string.Equals(a, "--correlation-id", StringComparison.OrdinalIgnoreCase)))
        {
            Globals.FlatLogLayout = Globals.FlatLogLayout.GetValueOrDefault(true);
        }

        StartNLog(useJsonLayout: !Globals.FlatLogLayout);
        Logger logger = LogManager.GetLogger(Globals.ProductName);
        SetupErrorLogging(logger);

        using var ctrlC = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            ctrlC.Cancel();
        };

        var rootCommand = new RootCommand("Download and uploads file quickly");

        var downloadCommand = new Command("download", "Download a file");
        rootCommand.AddCommand(downloadCommand);

        var uploadCommand = new Command("upload", "Upload a file");
        rootCommand.AddCommand(uploadCommand);

        CliModel.Bind<UploadArguments>(
            uploadCommand,
            m =>
            {
                var result = new UploadArguments()
                {
                    CorrelationId = m.Option(d => ref d.CorrelationId, name: "correlation-id", description: "Correlation ID", defaultValue: Guid.NewGuid().ToString()),
                    Uri = m.Option(d => ref d.Uri, name: "uri", description: "Uri to upload to in Azure Storage", required: true),
                    Path = m.Option(d => ref d.Path, name: "input", description: "The file to upload", required: true),
                    BlockSize = m.Option(d => ref d.BlockSize, name: "block-size", description: "Block size", defaultValue: 128 * 1024 * 1024),
                    Compression = m.Option(d => ref d.Compression, name: "compression", description: "Compression algorithm", defaultValue: CompressionAlgorithm.Brotli),
                    CompressionLevel = m.Option(d => ref d.CompressionLevel, name: "compression-level", description: "Compression level", defaultValue: CompressionLevel.Fastest),
                    UncompressedHashType = m.Option(d => ref d.UncompressedHashType, name: "hash-type", description: "Hash type of uncompressed blocks", defaultValue: HashType.SHA256),
                    CompressedHashType = m.Option(d => ref d.CompressedHashType, name: "checksum-hash-type", description: "Hash type of compressed blocks (used as a checksum)", defaultValue: HashType.Murmur),
                    Overwrite = m.Option(d => ref d.Overwrite, name: "overwrite", description: "Overwrite the target file", defaultValue: false),
                    Verbose = m.Option(d => ref d.Verbose, name: "verbose", description: "Enable verbose logging", defaultValue: false),
                };

                m.Option(d => ref d.Raw, name: "raw", description: "Specifies that file should be uploaded as-is with no compression or added metadata", defaultValue: false, isHidden: false);
                m.Option(d => ref d.ManifestPath, name: "manifest-path", description: "The file path to write computed manifest");
                m.Option(d => ref d.MaximumUploadConcurrency, name: "concurrency", description: "Maximum concurrency", defaultValue: Environment.ProcessorCount);
                m.Option(d => ref d.ExecutionTimeoutMinutes, name: "timeout",
                    description: "Timeout in minutes. The entire program will be cancelled if it takes this long",
                    defaultValue: 60);
                m.Option(d => ref d.SparseHandling, name: "sparse-handling",
                    description: "When uploading, how to handle upload of non-hole parts of sparse files",
                    defaultValue: SparseHandlingMode.None);

                m.Option(d => ref d.CheckOnly, name: "check-only", description: "Verifies that the content if the file matches the destination uri", defaultValue: false, isHidden: true);
                m.Option(d => ref d.CheckManifestUri, name: "check-uri", description: "Path to manifest or blob uri for uploaded file to validate block hashes during upload", isHidden: true);
                m.Option(d => ref d.RandomFileSize, name: "random-size", description: "Size (in bytes) of random content to generate instead of taking content from local file", isHidden: true);

                return result;
            },
            async command =>
            {
                returnCode = (int)await command.RunAsync(ctrlC.Token);
            });

        CliModel.Bind<DownloadArguments>(
            downloadCommand,
            m =>
            {
                var r = new DownloadArguments()
                {
                    CorrelationId = m.Option(d => ref d.CorrelationId, name: "correlation-id", description: "Correlation ID", defaultValue: Guid.NewGuid().ToString()),
                    Uri = m.Option(d => ref d.Uri, name: "uri", description: "Uri to download from", required: true),
                    Path = m.Option(d => ref d.Path, name: "output", description: "The file to write it down to", required: true),
                    Verbose = m.Option(d => ref d.Verbose, name: "verbose", description: "Enable verbose logging", defaultValue: false),
                };

                // This value was chosen because higher values have caused production incidents where Azure Storage
                // begins to fail requests due to the high rate of requests.
                var defaultDownloadMbps = 2000;
#if DEBUG
                // We set the download speed to 25 MB/s when in Debug builds to ensure we see the throttling at work
                // when debugging.
                defaultDownloadMbps = 25;
#endif
                m.Option(d => ref d.ManifestPath, name: "manifest-path", description: "The file to write extracted manifest");
                m.Option(d => ref d.MaximumDownloadMbps, name: "maximum-download-mbps", description: "Maximum download speed in MB/s. 0 disables the limit.", defaultValue: defaultDownloadMbps);
                m.Option(d => ref d.MinimumDownloadMbps, name: "minimum-download-mbps", description: "Minimum download speed in MB/s. Takes effect only when --maximum-download-mbps is set.", defaultValue: defaultDownloadMbps / 4);

                m.Option(d => ref d.GlobalDownloadSemaphoreFolderUri, name: "blob-semaphore-uri", "Semaphore of azure blob folder for shared represent semaphore for managing download concurrency");
                m.Option(d => ref d.MaxConcurrentDownloaders, name: "blob-semaphore-count", "Azure blob semaphore concurrency limit");
                m.Option(d => ref d.SemaphoreKeepAliveSeconds, name: "blob-semaphore-keepalive-secs", "Azure blob semaphore keep alive interval (in seconds)", defaultValue: 30);
                m.Option(d => ref d.SemaphoreRecheckSeconds, name: "blob-semaphore-recheck-secs", "Azure blob semaphore recheck interval (in seconds)", defaultValue: 5);
                m.Option(d => ref d.SemaphoreCleanupRatio, name: "blob-semaphore-cleanup-ratio", "The percentage between 0 and 1.0 of times when expired blobs should be deleted", defaultValue: 0.01);

                m.Option(d => ref d.MaximumDownloadConcurrency, name: "concurrency", description: "Maximum concurrency", defaultValue: (uint)Environment.ProcessorCount);
                m.Option(d => ref d.MaximumDecompressionConcurrency, name: "decompression-concurrency", description: "Maximum decompression concurrency", defaultValue: null);
                m.Option(
                    d => ref d.MaxWriteConcurrency,
                    name: "write-concurrency",
                    description: "Maximum number of writer threads",
                    required: false,
                    defaultValue: (uint)Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 8)));
                m.Option(d => ref d.ChunkSize, name: "chunk-size", description: "Chunk size", defaultValue: 128 * 1024 * 1024);
                m.Option(d => ref d.WriteThrough, name: "write-through", description: "Write Through", defaultValue: true);
                m.Option(d => ref d.ProgressIntervalSeconds, name: "progressSecs", description: "Interval (in seconds) to report progress", defaultValue: 1);
                m.Option(d => ref d.Async, name: "async", description: "Async IO", defaultValue: false);
                m.Option(d => ref d.Sparse, name: "sparse", description: "Sparse IO", defaultValue: true);
                m.Option(d => ref d.SkipZeroRegions, name: "skip-zero-regions", description: "Skip chunks only containing zeros and trim leading and trailing zeros before writing (result file will remain sparse)", defaultValue: false);
                m.Option(d => ref d.DedupeDownloadChunks, name: "dedude-download-chunks", description: "Deduplicate downloading of chunks to only unique chunks", defaultValue: false);
                m.Option(d => ref d.ExecutionTimeoutMinutes, name: "timeout",
                    description: "Timeout in minutes. The entire program will be cancelled if it takes this long",
                    defaultValue: 60);
                m.Option(d => ref d.ChunkDownloadTimeoutMinutes, name: "chunk-download-timeout",
                    description: "Chunk download timeout in minutes. The entire download will be permanently cancelled if any single chunk takes this long",
                    defaultValue: 10);

                m.Option(
                    d => ref d.MaxContiguousBuffers,
                    name: "chunk-batch-max",
                    description: "Maximum number of buffers to write in a batch",
                    defaultValue: 4);

                m.Option(
                    d => ref d.MaximumOutstandingWritesPerWriter,
                    name: "max-outstanding-writes",
                    description: "Maximum number of write batches that may be pending for each writing worker",
                    defaultValue: 0);

                // Proxy chain options
                m.Option(
                    d => ref d.ProxyBufferSizeMb,
                    name: "proxy-buffer-mb",
                    description: "The amount of megabytes of buffers to retain for proxy server",
                    defaultValue: Constants.TotalStartMemoryGb / 4);
                m.Option(
                    d => ref d.ProxyScopeId,
                    name: "proxy-scope",
                    description: "Specifies the scope identifier for a scope in which machines can share content.",
                    defaultValue: "default");
                m.Option(
                    d => ref d.Port,
                    name: "proxy-server-port",
                    description: "The port used by launched proxy server to expose downloaded file. If unspecified, proxy communication is disabled.");
                m.Option(
                    d => ref d.Port,
                    name: "proxy-port-with-defaults",
                    description: "The port used by launched proxy server to expose downloaded file. Also, sets other defaults for proxy communication.",
                    setPriority: -1,
                    afterSet: d =>
                    {
                        // Set defaults if port was specified
                        if (d.Port != 0)
                        {
                            // Use native buffers and do not pool since native buffers
                            // can be deallocated directly
                            Globals.UseNativeBuffers = true;
                            d.PoolCompressedBuffers = false;

                            // Do not throttle downloads
                            d.MaximumDownloadMbps = 0;

                            // Set proxy buffer to 3/4 total memory (we assume download can be aggressive with resource utilization)
                            d.ProxyBufferSizeMb = (uint)(Constants.TotalStartMemoryGb * 3).DivRoundUp(4);

                            // Set decompression concurrency to 3/4 total thread to avoid overloading CPU  for decompression
                            // which can interfere with p2p downloads
                            d.MaximumDecompressionConcurrency = (uint)(Environment.ProcessorCount * 3).DivRoundUp(4);
                        }
                    });
                m.Option(
                    d => ref d.ServerProtocol,
                    name: "server-protocol",
                    description: "http or https",
                    defaultValue: DownloadArguments.HttpProtocol.https);
                m.Option(
                    d => ref d.PendingShutdownDelaySeconds,
                    name: "server-shutdown-delay-secs",
                    description: "Delay before shutting down proxy server after download completes (in seconds)");

                // Debug options:
                m.Option(
                    d => ref Globals.UseNativeBuffers,
                    name: "use-native-buffers",
                    description: "Whether to use native allocation for buffers",
                    defaultValue: Globals.UseNativeBuffers,
                    isHidden: true);
                m.Option(
                    d => ref d.PoolCompressedBuffers,
                    name: "pool-compressed-buffers",
                    description: "Whether to pool buffers from download of compressed chunks which are below the chunk size",
                    defaultValue: true,
                    isHidden: true);
                m.Option(
                    d => ref d.UseHttp2,
                    name: "use-http2",
                    description: "Whether to use http2 for peer to peer communication",
                    defaultValue: false,
                    isHidden: true);
                m.Option(
                    d => ref d.ReleaseBuffers,
                    name: "release-buffers",
                    description: "Whether to release buffers on shutdown",
                    defaultValue: false,
                    isHidden: true);
                m.Option(
                    d => ref d.UseLinearProxyChain,
                    name: "linear-proxy-chain",
                    description: "Use linear proxy chain",
                    defaultValue: true,
                    isHidden: true);
                m.Option(d => ref d.SimulationMode, name: "simulation", description: "Type of simulation", defaultValue: DownloadArguments.SimulationModes.None, isHidden: true);

                return r;
            },
            async command =>
            {
                returnCode = (int)await command.RunAsync(ctrlC.Token);
            });

        try
        {
            var parserReturnCode = await rootCommand.InvokeAsync(args);
            if (returnCode == int.MaxValue)
            {
                returnCode = parserReturnCode;
            }
        }
        catch (OperationCanceledException) when (ctrlC.IsCancellationRequested)
        {
            logger
                .ForWarnEvent()
                .Message("Execution was cancelled by the user via Ctrl+C")
                .Log();

            returnCode = (int)ReturnCode.Cancelled;
        }
        catch (Exception ex)
        {
            logger
                .ForFatalEvent()
                .Message("Unhandled exception")
                .Exception(ex)
                .Log();

            returnCode = (int)ReturnCode.UnhandledException;
        }
        finally
        {
            logger.ForInfoEvent()
            .Message(
                "Command completed with exit code {ReturnCode} {ExitCode}",
                (ReturnCode)returnCode,
                returnCode)
            .Log();

            StopNLog();

            Console.WriteLine("Logging shutdown");
        }

        return returnCode;
    }

    public static void SetupErrorLogging(Logger logger)
    {
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            logger.ForErrorEvent()
                  .Message("Unobserved task exception detected")
                  .Exception(e.Exception)
                  .Log();

            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            logger.ForErrorEvent()
                  .Message("Unhandled exception detected")
                  .Exception((Exception)e.ExceptionObject)
                  .Log();
        };
    }

    public static void StartNLog(bool useJsonLayout = true)
    {
        // TODO: Update once new BuildXL packages are available
        //Tracer.DefaultOperationStartedSeverity = BuildXL.Cache.ContentStore.Interfaces.Logging.Severity.Info;
        //Tracer.DefaultOperationFinishedSuccessSeverity = BuildXL.Cache.ContentStore.Interfaces.Logging.Severity.Info;

        LogManager.Setup()
            .SetupExtensions(s =>
            {
                s.RegisterLayoutRenderer("correlation_id", (logEventInfo) => Globals.CorrelationId);
                s.RegisterLayoutRenderer("source_correlation_id", (logEventInfo) => Globals.SourceCorrelationId);
            })
            .LoadConfiguration(builder =>
            {
                var jsonLayout = new JsonLayout
                {
                    Attributes =
                    {
                        new JsonAttribute("PreciseTimeStamp", "${longdate:universalTime=true}"),
                        new JsonAttribute("LogLevelFriendly", "${level:uppercase=true}"),
                        new JsonAttribute("Version", "${var:program-version}"),
                        new JsonAttribute("CorrelationId", "${correlation_id}"),
                        new JsonAttribute("SourceCorrelationId", "${source_correlation_id}"),

                        // We use the logger name to identify the class that logged the message. This is done by
                        // convention because we can't rely on the callsite layout renderer as per below comment.
                        new JsonAttribute("Class", "${logger}"),

                        // We use the callsite renderer here to get the method name. The other fields are unreliable
                        // when using AOT because of trimming.
                        new JsonAttribute("Method", "${callsite:className=false:methodName=true:includeSourcePath=false:captureStackTrace=false}"),

                        new JsonAttribute("Source", "${callsite:className=false:methodName=false:fileName=true:includeSourcePath=false:captureStackTrace=false}"),

                        new JsonAttribute("Message", "${message}"),

                        // Report properties are special. Native AOT prevents us from doing reflection-based
                        // serialization, so we need to serialize these manually. We do that in the callsites, and here
                        // we just instruct nlog to log whatever comes in as it comes in, without touching it.
                        new JsonAttribute("Report", "${event-property:item=Report}", encode: false),

                        // We serialize exceptions to json to facilitate log parsing.
                        new JsonAttribute("Exception", "${exception:format=@}", encode: false)
                    },
                    IncludeEventProperties = true,
                    ExcludeEmptyProperties = true,
                    ExcludeProperties = new HashSet<string>()
                    {
                        "Report",
                    },
                    IncludeGdc = true,
                    IncludeScopeProperties = true,
                    IndentJson = false,
                    RenderEmptyObject = false,
                    MaxRecursionLimit = 10,
                };

                var consoleTarget = new ConsoleTarget("console")
                {
                    AutoFlush = true,
                };

                if (useJsonLayout)
                {
                    consoleTarget.Layout = jsonLayout;
                }

                builder.ForLogger(ruleName: "LogToConsole")
                    .FilterMinLevel(LogLevel.Info)
                    .WriteTo(consoleTarget);
            });
    }

    public static void SetMinimumLogLevel(LogLevel level)
    {
        var rule = LogManager.Configuration.FindRuleByName("LogToConsole");
        if (rule != null)
        {
            rule.SetLoggingLevels(level, LogLevel.Fatal);
        }

        LogManager.ReconfigExistingLoggers();
    }

    public static void StopNLog()
    {
        // It's important to flush at the very end to ensure all logs are written out.
        LogManager.Flush();
        LogManager.Shutdown();
    }
}
