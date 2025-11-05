// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using NLog;

namespace FastDownload.Tests.Utilities
{
    public class TestOutputTestBase
    {
        public TestContext TestContext { get; set; }

        public ILogger TestLogger { get; private set; }

        [TestInitialize]
        public void InitializeWriter()
        {
            TestLogger = LogManager.GetLogger(GetType().Name);
            Console.SetOut(new TestOutputWriter(TestContext));
            Console.SetError(new TestOutputWriter(TestContext));
        }
    }

    public class TestOutputWriter(TestContext context) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write([StringSyntax("CompositeFormat")] string format, params object?[] arg)
        {
            context.Write(format, arg);
        }

        public override void Write(string? value)
        {
            context.Write(value);
        }

        public override void WriteLine([StringSyntax("CompositeFormat")] string format, params object?[] arg)
        {
            context.WriteLine(format, arg);
        }

        public override void WriteLine(string? value)
        {
            context.WriteLine(value);
        }
    }
}
