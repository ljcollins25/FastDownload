// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable disable
#nullable enable annotations

using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using BuildXL.Cache.ContentStore.Utils;
using Shouldly;

namespace FastDownload.Tests.Azurite;

/// <summary>
/// Wrapper around local storage instance.
/// </summary>
public sealed class AzuriteStorageProcess(TestContext Context) : IDisposable
{
    private ProcessUtility _process;

    public string ConnectionString { get; private set; }

    public string Directory { get; } = GetDirectory(Context);

    private static string GetDirectory(TestContext context)
    {
        var key = "AzuriteStorageProcess.Index";
        int index = 0;
        if (context.Properties.Contains(key))
        {
            index = (int)context.Properties[key];
            index++;
        }

        context.Properties[key] = index;

        return Path.Combine(context.TestRunDirectory, $"azurite{index}");
    }

    public Uri SasUrl { get; private set; }

    public BlobServiceClient GetService(bool useSas = true)
    {
        return useSas ? new BlobServiceClient(SasUrl) : new BlobServiceClient(ConnectionString);
    }

    public BlobContainerClient GetContainer(string containerName, bool useSas = true)
    {
        return GetService(useSas).GetBlobContainerClient(containerName);
    }

    public const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private bool _disposed;
    private int _portNumber;

    internal bool Closed { get; private set; }

    public override string ToString()
    {
        return ConnectionString;
    }

    /// <summary>
    /// Creates an instance of a database with a given data.
    /// </summary>
    public static async Task<AzuriteStorageProcess> CreateAndStartAsync(
        TestContext context,
        List<string> accounts = null)
    {
        var instance = new AzuriteStorageProcess(context);

        try
        {
            await instance.StartAsync(accounts);
            return instance;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to start a local database. Exception=" + e);
            instance.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Dispose(close: true);

    /// <nodoc />
    public void Dispose(bool close)
    {
        if (close)
        {
            // Closing the instance and not returning it back to the pool.
            Close();
            return;
        }

        if (_disposed)
        {
            // The type should be safe for double dispose.
            return;
        }

        _disposed = true;
    }

    ~AzuriteStorageProcess()
    {
        // If the emulator is not gracefully closed,
        // then BuildXL will fail because surviving blob.exe instance.
        // So we're failing fast instead and will print the process Id that caused the issue.
        // This may happen only if the database is not disposed gracefully.
        if (!Closed)
        {
            string message = $"Storage process {_process?.Id} was not closed correctly.";
            Console.WriteLine(message);
            throw new InvalidOperationException(message);
        }
    }

    public void Close()
    {
        if (Closed)
        {
            return;
        }

        GC.SuppressFinalize(this);

        if (_process != null)
        {
            Console.WriteLine($"Killing the storage process {_process?.Id}...");
            SafeKillProcess();
        }

        Closed = true;
    }

    private void SafeKillProcess()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
                Console.WriteLine($"The storage process is killed {_process.Id}");
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task StartAsync(List<string> accounts = null)
    {
        // Can reuse an existing process only when this instance successfully created a connection to it.
        // Otherwise the test will fail with NRE.
        if (_process != null)
        {
            Console.WriteLine("Storage process is already running. Reusing an existing instance.");
            return;
        }

        Console.WriteLine("Starting a storage server.");

        var dir = Path.GetDirectoryName(GetType().Assembly.Location);
        string storageServerPath = Path.Combine(dir, "blob.exe");
        if (!File.Exists(storageServerPath))
        {
            throw new InvalidOperationException($"Could not find {storageServerPath}");
        }

        _portNumber = 0;

        const int maxRetries = 10;
        for (int i = 0; i < maxRetries; i++)
        {
            var storageServerWorkspacePath = Directory;
            if (System.IO.Directory.Exists(Directory))
            {
                BuildXL.Native.IO.FileUtilities.DeleteDirectoryContents(Directory);
            }

            _portNumber = Debugger.IsAttached ? 61747 : PortExtensions.GetNextAvailablePort();

            var args = $"--blobPort {_portNumber} --location \"{storageServerWorkspacePath}\" --skipApiVersionCheck --silent --loose";
            Console.WriteLine($"Running cmd=[{storageServerPath} {args}]");

            _process = new ProcessUtility(storageServerPath, args, createNoWindow: true, workingDirectory: Path.GetDirectoryName(storageServerPath), environment: CreateEnvironment(accounts));

            _process.Start();

            string processOutput;
            if (_process == null)
            {
                processOutput = "[Process could not start]";
                throw new InvalidOperationException(processOutput);
            }

            if (_process.HasExited)
            {
                if (_process.WaitForExit(5000))
                {
                    throw new InvalidOperationException();
                }

                throw new InvalidOperationException("Process or either wait handle timed out. ");
            }

            processOutput = $"[Process {_process.Id} is still running]";

            Console.WriteLine("Process output: " + processOutput);

            ConnectionString = $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={AccountKey};BlobEndpoint=http://127.0.0.1:{_portNumber}/devstoreaccount1;";

            var service = new BlobServiceClient(ConnectionString);

            var sasUri = service.GenerateAccountSasUri(AccountSasPermissions.All, DateTimeOffset.UtcNow + TimeSpan.FromHours(10), AccountSasResourceTypes.All);

            SasUrl = sasUri;

            Console.WriteLine($"Azurite Sas Uri:\n{sasUri}");

            try
            {
                var container = GetContainer("testcontainer");
                GetContainer(container.Name, useSas: false).Exists().Value.ShouldBeFalse();

                var blob = container.GetBlockBlobClient("test.blob");
                bool blobExists = await blob.ExistsAsync();
                blobExists.ShouldBeFalse();

                break;
            }
            catch (RequestFailedException ex)
            {
                SafeKillProcess();
                Console.WriteLine($"Retrying for exception connecting to storage process {_process.Id} with port {_portNumber}: {ex.ToString()}. Has process exited {_process.HasExited} with output");

                if (i != maxRetries - 1)
                {
                    Thread.Sleep(300);
                }
                else
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                SafeKillProcess();
                Console.Error.WriteLine(
                    $"Exception connecting to storage process {_process.Id} with port {_portNumber}: {ex.ToString()}. Has process exited {_process.HasExited} with output");
                throw;
            }
        }

        Console.WriteLine($"Storage server {_process.Id} is up and running at port {_portNumber}.");
    }

    private static Dictionary<string, string> CreateEnvironment(List<string> accounts = null)
    {
        if (accounts == null)
        {
            return null;
        }

        // See: https://github.com/Azure/Azurite#customized-storage-accounts--keys
        var dictionary = new Dictionary<string, string>();

        // Ensure the default account still exists
        accounts.Add("devstoreaccount1");

        // We use the same password for all storage accounts in the emulator
        dictionary["AZURITE_ACCOUNTS"] = string.Join(
            ";",
            accounts.Select(
                name => $"{name}:Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="));

        return dictionary;
    }
}
