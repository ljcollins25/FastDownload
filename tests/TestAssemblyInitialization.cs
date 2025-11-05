// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics;
using FastDownload.Tests.Utilities;
using FastDownload.Utilities;

namespace FastDownload.Tests
{
    /// <summary>
    /// The purpose of this class is to initialize assembly-wide resources such as logging.
    /// </summary>
    [TestClass]
    public class TestAssemblyInitialization
    {
        [AssemblyInitialize]
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
#pragma warning disable IDE0060 // Remove unused parameter
        public static void InitializeAssembly(TestContext context)
#pragma warning restore IDE0060 // Remove unused parameter
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
        {
            // Logging to console leads to the Visual Studio Test Explorer showing the logs appropriately.
            Globals.FlatLogLayout = true;
            Globals.DebugProxy = true;
            Globals.IsTest = true;
            Program.StartNLog(useJsonLayout: false);

            Program.SetMinimumLogLevel(NLog.LogLevel.Debug);


            var defaultListener = Trace.Listeners[0];
            Trace.Listeners.RemoveAt(0);
            Trace.Listeners.Add(new DebugTraceListener(defaultListener));
        }

        [AssemblyCleanup]
        public static void CleanupLogging()
        {
            Program.StopNLog();
        }
    }
}
