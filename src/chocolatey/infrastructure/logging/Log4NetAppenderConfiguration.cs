﻿// Copyright © 2017 Chocolatey Software, Inc
// Copyright © 2011 - 2017 RealDimensions Software, LLC
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// 
// You may obtain a copy of the License at
// 
// 	http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace chocolatey.infrastructure.logging
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using adapters;
    using app;
    using log4net;
    using log4net.Appender;
    using log4net.Config;
    using log4net.Core;
    using log4net.Filter;
    using log4net.Layout;
    using log4net.Repository;
    using log4net.Repository.Hierarchy;
    using platforms;

    public sealed class Log4NetAppenderConfiguration
    {
        private static readonly log4net.ILog _logger = LogManager.GetLogger(typeof(Log4NetAppenderConfiguration));

        private static bool _alreadyConfiguredFileAppender;
        private static readonly string _summaryLogAppenderName = "{0}.summary.log.appender".format_with(ApplicationParameters.Name);

        /// <summary>
        ///   Pulls xml configuration from embedded location and applies it. 
        ///   Then it configures a file appender to the specified output directory if one is provided.
        /// </summary>
        /// <param name="outputDirectory">The output directory.</param>
        /// <param name="excludeLoggerNames">Loggers, such as a verbose logger, to exclude from this.</param>
        public static void configure(string outputDirectory = null, params string[] excludeLoggerNames)
        {
            GlobalContext.Properties["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            
            var xmlConfigFile = Path.Combine(ApplicationParameters.InstallLocation, "log4net.config.xml");
            if (File.Exists(xmlConfigFile))
            {
                XmlConfigurator.ConfigureAndWatch(new FileInfo(xmlConfigFile));
                _logger.DebugFormat("Configured Log4Net configuration from file ('{0}').", xmlConfigFile);
            }
            else
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resource = ApplicationParameters.Log4NetConfigurationResource;
                if (Platform.get_platform() != PlatformType.Windows)
                {
                    // it became much easier to do this once we realized that updating the current mappings is about impossible.
                    resource = resource.Replace("log4net.", "log4net.mono.");
                }
                Stream xmlConfigStream = assembly.get_manifest_stream(resource);

                XmlConfigurator.Configure(xmlConfigStream);

                _logger.DebugFormat("Configured Log4Net configuration ('{0}') from assembly {1}", resource, assembly.FullName);
            }
            
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                set_file_appender(outputDirectory, excludeLoggerNames);
            }
        }

        /// <summary>
        ///   Adds a file appender to all current loggers. Only runs one time.
        /// </summary>
        /// <param name="outputDirectory">The output directory.</param>
        /// <param name="excludeLoggerNames">Loggers, such as a trace logger, to exclude from file appender.</param>
        private static void set_file_appender(string outputDirectory, params string[] excludeLoggerNames)
        {
            if (excludeLoggerNames == null) excludeLoggerNames = new string[] {};

            if (!_alreadyConfiguredFileAppender)
            {
                _alreadyConfiguredFileAppender = true;

                var layout = new PatternLayout
                    {
                        ConversionPattern = "%date %property{pid} [%-5level] - %message%newline"
                    };
                layout.ActivateOptions();

                var app = new RollingFileAppender
                    {
                        Name = "{0}.changes.log.appender".format_with(ApplicationParameters.Name),
                        File = Path.Combine(Path.GetFullPath(outputDirectory), ApplicationParameters.LoggingFile),
                        Layout = layout,
                        AppendToFile = true,
                        RollingStyle = RollingFileAppender.RollingMode.Size,
                        MaxFileSize = 1024 * 1024 * 10,
                        MaxSizeRollBackups = 50,
                        LockingModel = new FileAppender.MinimalLock(),
                        PreserveLogFileNameExtension = true,
                    };
                app.ActivateOptions();

                var infoOnlyAppender = new RollingFileAppender
                {
                    Name = _summaryLogAppenderName,
                    File = Path.Combine(Path.GetFullPath(outputDirectory), ApplicationParameters.LoggingSummaryFile),
                    Layout = layout,
                    AppendToFile = true,
                    RollingStyle = RollingFileAppender.RollingMode.Size,
                    MaxFileSize = 1024 * 1024 * 10,
                    MaxSizeRollBackups = 50,
                    LockingModel = new FileAppender.MinimalLock(),
                    PreserveLogFileNameExtension = true,
                };
                infoOnlyAppender.AddFilter(new LevelRangeFilter { LevelMin = Level.Info, LevelMax = Level.Fatal });
                infoOnlyAppender.ActivateOptions();

                ILoggerRepository logRepository = LogManager.GetRepository(Assembly.GetCallingAssembly().UnderlyingType);
                foreach (ILogger log in logRepository.GetCurrentLoggers().Where(l => excludeLoggerNames.All(name => !l.Name.is_equal_to(name))).or_empty_list_if_null())
                {
                    var logger = log as Logger;
                    if (logger != null)
                    {
                        logger.AddAppender(app);
                        logger.AddAppender(infoOnlyAppender);
                    }
                }
            }
        }

        /// <summary>
        ///   Sets all loggers to Debug level
        /// </summary>
        /// <param name="enableDebug">
        ///   if set to <c>true</c> [enable debug].
        /// </param>
        /// <param name="excludeAppenderNames">Appenders, such as a verbose console appender, to exclude from debug.</param>
        public static void set_logging_level_debug_when_debug(bool enableDebug, params string[] excludeAppenderNames)
        {
            if (excludeAppenderNames == null) excludeAppenderNames = new string[] { };

            if (enableDebug)
            {
                ILoggerRepository logRepository = LogManager.GetRepository(Assembly.GetCallingAssembly().UnderlyingType);
                logRepository.Threshold = Level.Debug;
                foreach (ILogger log in logRepository.GetCurrentLoggers())
                {
                    var logger = log as Logger;
                    if (logger != null)
                    {
                        logger.Level = Level.Debug;
                    }
                }

                foreach (var append in logRepository.GetAppenders().Where(a => excludeAppenderNames.All(name => !a.Name.is_equal_to(name))).or_empty_list_if_null())
                {
                    var appender = append as AppenderSkeleton;
                    if (appender != null && !appender.Name.is_equal_to(_summaryLogAppenderName))
                    {
                        // slightly naive implementation
                        appender.ClearFilters();
                    }
                }
            }
        }

        /// <summary>
        ///   Sets a named verbose logger to Info Level (or Debug Level) when enableVerbose is true.
        /// </summary>
        /// <param name="enableVerbose">
        ///   if set to <c>true</c> [enable verbose].
        /// </param>
        /// <param name="enableDebug">If debug is also enabled</param>
        /// <param name="verboseLoggerName">Name of the verbose logger.</param>
        public static void set_verbose_logger_when_verbose(bool enableVerbose, bool enableDebug, string verboseLoggerName)
        {
            if (enableVerbose)
            {
                ILoggerRepository logRepository = LogManager.GetRepository(Assembly.GetCallingAssembly().UnderlyingType);
                foreach (var append in logRepository.GetAppenders())
                {
                    var appender = append as AppenderSkeleton;
                    if (appender != null && appender.Name.is_equal_to(verboseLoggerName))
                    {
                        appender.ClearFilters();
                        var minLevel = enableDebug ? Level.Debug : Level.Info;
                        appender.AddFilter(new log4net.Filter.LevelRangeFilter { LevelMin = minLevel, LevelMax = Level.Fatal });
                    }
                }
            }
        }

        /// <summary>
        /// Sets a named trace logger to Debug level when enable trace is true.
        /// </summary>
        /// <param name="enableTrace">if set to <c>true</c> [enable trace].</param>
        /// <param name="traceLoggerName">Name of the trace logger.</param>
        public static void set_trace_logger_when_trace(bool enableTrace, string traceLoggerName)
        {
            if (enableTrace)
            {
                System.Diagnostics.Trace.Listeners.Clear();
                System.Diagnostics.Trace.AutoFlush = true;
                System.Diagnostics.Trace.Listeners.Add(new TraceLog());

                var fileAppenders = new List<AppenderSkeleton>();

                ILoggerRepository logRepository = LogManager.GetRepository(Assembly.GetCallingAssembly().UnderlyingType);
                foreach (var append in logRepository.GetAppenders())
                {
                    var appender = append as AppenderSkeleton;
                    if (appender != null && appender.Name.is_equal_to(traceLoggerName))
                    {
                        appender.ClearFilters();
                        var minLevel = Level.Debug;
                        appender.AddFilter(new log4net.Filter.LevelRangeFilter { LevelMin = minLevel, LevelMax = Level.Fatal });
                    }

                    if (appender != null && appender.GetType() == typeof(RollingFileAppender)) fileAppenders.Add(appender);
                }

                foreach (ILogger log in logRepository.GetCurrentLoggers().Where(l => l.Name.is_equal_to("Trace")).or_empty_list_if_null())
                {
                    var logger = log as Logger;
                    if (logger != null)
                    {
                        foreach (var appender in fileAppenders.or_empty_list_if_null())
                        {
                            logger.AddAppender(appender);
                        }
                    }
                }

                foreach (var append in logRepository.GetAppenders())
                {
                    var appender = append as AppenderSkeleton;
                    if (appender != null && appender.Name.is_equal_to("{0}.changes.log.appender".format_with(ApplicationParameters.Name)))
                    {
                        var traceLayout = new PatternLayout
                        {
                            ConversionPattern = "%date %property{pid}:%thread [%-5level] - %message - %file:%method:%line %newline"
                        };
                        traceLayout.ActivateOptions();

                        appender.Layout = traceLayout;
                    }
                }
            }
        }

        public static void configure_additional_log_file(string logFileLocation)
        {
            if (string.IsNullOrWhiteSpace(logFileLocation)) return;

            var logDirectory = Path.GetDirectoryName(logFileLocation);
            var logFileName = Path.GetFileNameWithoutExtension(logFileLocation);
            if (!string.IsNullOrWhiteSpace(logDirectory) && !Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);
            var layout = new PatternLayout
            {
                ConversionPattern = "%date %property{pid} [%-5level] - %message%newline"
            };
            layout.ActivateOptions();

            var app = new FileAppender()
            {
                Name = "{0}.{1}.log.appender".format_with(ApplicationParameters.Name, logFileName),
                File = logFileLocation,
                Layout = layout,
                AppendToFile = true,
                LockingModel = new FileAppender.MinimalLock(),
            };
            app.ActivateOptions();

            ILoggerRepository logRepository = LogManager.GetRepository(Assembly.GetCallingAssembly().UnderlyingType);
            foreach (ILogger log in logRepository.GetCurrentLoggers().Where(l => !l.Name.is_equal_to("Trace")).or_empty_list_if_null())
            {
                var logger = log as Logger;
                if (logger != null)
                {
                    logger.AddAppender(app);
                }
            }

        }
    }
}