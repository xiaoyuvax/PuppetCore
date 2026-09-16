using System.Collections.Concurrent;
using Wima.Log;

namespace Puppet.Core
{
    public enum LogLevel { Trace, Debug, Info, Warn, Error, Fatal }

    public static class PLog
    {
        private static readonly ConcurrentDictionary<string, WimaLogger> _loggers = new();

        public static string Fwd(LogLevel level, string message, string loggerName = "Puppet.Forwarded")
        {
            if (string.IsNullOrEmpty(message)) return message;

            var logger = _loggers.GetOrAdd(loggerName,
                _ => new WimaLogger(loggerName, logMode: LogMode.Native | LogMode.Console));

            switch (level)
            {
                case LogLevel.Trace: logger.Trace(message); break;
                case LogLevel.Debug: logger.Debug(message); break;
                case LogLevel.Info:  logger.Info(message);  break;
                case LogLevel.Warn:  logger.Warn(message);  break;
                case LogLevel.Error: logger.Error(message); break;
                case LogLevel.Fatal: logger.Fatal(message); break;
            }
            return message;
        }

        public static string Fwd(LogLevel level, string format, params object[] args)
            => Fwd(level, args?.Length > 0 ? string.Format(format, args) : format);
    }
}