using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using log4net.Repository;

namespace CoapClient
{
    internal static class CoapLog
    {
        private const string RepositoryName = "CoapProjectRepository";
        private const string LoggerName = "[COAP]";

        private static readonly Lazy<ILog> Logger = new Lazy<ILog>(CreateLogger);

        public static void Info(string message)
        {
            Logger.Value.Info(message);
        }

        public static void Error(string message)
        {
            Logger.Value.Error(message);
        }

        public static void Error(string message, Exception exception)
        {
            Logger.Value.Error(message, exception);
        }

        private static ILog CreateLogger()
        {
            ILoggerRepository repository = GetOrCreateRepository();

            if (!repository.Configured)
            {
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
                Directory.CreateDirectory(logDirectory);

                PatternLayout layout = new PatternLayout
                {
                    ConversionPattern = "%date %-5level [%thread] %logger - %message%newline%exception",
                };
                layout.ActivateOptions();

                RollingFileAppender appender = new RollingFileAppender
                {
                    Name = "CoapProjectFileAppender",
                    File = Path.Combine(logDirectory, "coap.log"),
                    AppendToFile = true,
                    RollingStyle = RollingFileAppender.RollingMode.Size,
                    MaxSizeRollBackups = 10,
                    MaximumFileSize = "10MB",
                    StaticLogFileName = true,
                    Layout = layout,
                    Threshold = Level.Info,
                    LockingModel = new FileAppender.MinimalLock(),
                };
                appender.ActivateOptions();

                BasicConfigurator.Configure(repository, appender);
            }

            return LogManager.GetLogger(RepositoryName, LoggerName);
        }

        private static ILoggerRepository GetOrCreateRepository()
        {
            return LogManager.GetAllRepositories().FirstOrDefault(repository => repository.Name == RepositoryName)
                ?? LogManager.CreateRepository(RepositoryName);
        }
    }
}