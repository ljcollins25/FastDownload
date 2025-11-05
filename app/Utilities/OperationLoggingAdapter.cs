// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Tracing;
using NLog;

namespace FastDownload.Utilities;
using NLogger = NLog.ILogger;


/// <summary>
/// Adapts NLog <see cref="ILogger"/> into ContentStore <see cref="BuildXL.Cache.ContentStore.Interfaces.Logging.ILogger"/>
/// </summary>
public record OperationLoggingAdapter(NLogger Logger, Tracer Tracer) : BuildXL.Cache.ContentStore.Interfaces.Logging.ILogger
{
    /// <nodoc />
    public OperationLoggingAdapter(string name)
        : this(NLog.LogManager.GetLogger(name), new Tracer(name))
    {
    }

    /// <nodoc />
    public static implicit operator Tracer(OperationLoggingAdapter adapter) => adapter.Tracer;

    /// <inheritdoc />
    public Severity CurrentSeverity { get; } = ComputeCurrentSeverity(Logger);

    private int m_errorCount;
    /// <inheritdoc />
    public int ErrorCount => m_errorCount;

    private static Severity ComputeCurrentSeverity(NLogger log)
    {
        if (log.IsTraceEnabled)
        {
            return Severity.Diagnostic;
        }

        if (log.IsDebugEnabled)
        {
            return Severity.Debug;
        }

        if (log.IsInfoEnabled)
        {
            return Severity.Info;
        }

        if (log.IsWarnEnabled)
        {
            return Severity.Warning;
        }

        if (log.IsErrorEnabled)
        {
            return Severity.Error;
        }

        if (log.IsFatalEnabled)
        {
            return Severity.Fatal;
        }

        return Severity.Unknown;
    }

    //public void Debug(Exception? ex, string format, params object[] args) => _nlog.Debug(ex, format, args);
    //public void Debug(string message) => _nlog.Debug(message);
    //public void Debug(string format, params object[] args) => _nlog.Debug(format, args);

    //public void Error(string message) => _nlog.Error(message);
    //public void Error(string format, params object[] args) => _nlog.Error(format, args);
    //public void Error(Exception ex, string errorMessage) => _nlog.Error(ex, errorMessage);

    //public void Finest(string message) => _nlog.Trace(message);
    //public void Finest(string format, params object[] args) => _nlog.Trace(format, args);

    //public void Info(string message) => _nlog.Info(message);
    //public void Info(string format, params object[] args) => _nlog.Info(format, args);

    //public void Warning(string message) => _nlog.Warn(message);
    //public void Warning(string format, params object[] args) => _nlog.Warn(format, args);
    //public void Warning(Exception ex, string warningMessage) => _nlog.Warn(ex, warningMessage);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public void LogFormat(Severity severity, string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(severity), messageFormat, messageArgs);

        if (severity == Severity.Error)
        {
            Interlocked.Increment(ref m_errorCount);
        }
    }

    /// <inheritdoc />
    public void Log(Severity severity, string message)
    {
        Logger.Log(Translate(severity), message);

        if (severity == Severity.Error)
        {
            Interlocked.Increment(ref m_errorCount);
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        NLog.LogManager.Flush();
    }

    /// <inheritdoc />
    public void Debug(string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Debug), messageFormat, messageArgs);
    }

    /// <inheritdoc />
    public void Debug(Exception exception)
    {
        Logger.Log(Translate(Severity.Debug), exception);
    }

    /// <inheritdoc />
    public void Diagnostic(string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Diagnostic), messageFormat, messageArgs);
    }

    /// <inheritdoc />
    public void Info(string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Info), messageFormat, messageArgs);
    }

    /// <inheritdoc />
    public void Warning(string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Warning), messageFormat, messageArgs);
    }

    /// <inheritdoc />
    public void Error(string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Error), messageFormat, messageArgs);
        Interlocked.Increment(ref m_errorCount);
    }

    /// <inheritdoc />
    public void Error(Exception exception, string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Error), exception, messageFormat, messageArgs);
        Interlocked.Increment(ref m_errorCount);
    }

    /// <inheritdoc />
    public void ErrorThrow(Exception exception, string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Error), exception, messageFormat, messageArgs);
        Interlocked.Increment(ref m_errorCount);
    }

    /// <inheritdoc />
    public void Fatal(string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Fatal), messageFormat, messageArgs);
    }

    /// <inheritdoc />
    public void Always(string messageFormat, params object[] messageArgs)
    {
        Logger.Log(Translate(Severity.Always), messageFormat, messageArgs);
    }

    private static NLog.LogLevel Translate(Severity severity)
    {
        return severity switch
        {
            Severity.Unknown => NLog.LogLevel.Off,
            Severity.Diagnostic => NLog.LogLevel.Trace,
            Severity.Debug => NLog.LogLevel.Debug,
            Severity.Info => NLog.LogLevel.Info,
            Severity.Warning => NLog.LogLevel.Warn,
            Severity.Error => NLog.LogLevel.Error,
            Severity.Fatal => NLog.LogLevel.Fatal,
            // Notice that we can loose Always traces because there is no such concept in NLog
            Severity.Always => NLog.LogLevel.Info,
            _ => throw new NotImplementedException("Missing log level translation"),
        };
    }
}
