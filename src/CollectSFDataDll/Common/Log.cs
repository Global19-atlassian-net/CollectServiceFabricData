﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CollectSFData.Common
{
    public static class Log
    {
        public static int LogErrors = 0;
        private static ConsoleColor _highlightBackground = Console.ForegroundColor;
        private static ConsoleColor _highlightForeground = Console.BackgroundColor;
        private static bool _isRunning;
        private static JsonSerializerSettings _jsonSerializerSettings;
        private static SynchronizedList<LogMessage> _lastMessageList = new SynchronizedList<LogMessage>();
        private static string _logFile;
        private static SynchronizedList<LogMessage> _messageList = new SynchronizedList<LogMessage>();
        private static StreamWriter _streamWriter;
        private static Task _taskWriter;
        private static CancellationTokenSource _taskWriterCancellationToken;
        private static int _threadSleepMs = Constants.ThreadSleepMs100;

        public delegate void LogMessageHandler(object sender, LogMessage args);

        public static event LogMessageHandler MessageLogged;

        private static event EventHandler<Newtonsoft.Json.Serialization.ErrorEventArgs> JsonErrorHandler;

        public static bool IsConsole { get; set; }

        public static int LogDebug { get; set; }

        public static string LogFile { get => _logFile; set => _logFile = CheckLogFile(value) ? value : string.Empty; }

        public static bool LogFileEnabled;

        static Log()
        {
            JsonErrorHandler += Log_JsonErrorHandler;

            _jsonSerializerSettings = new JsonSerializerSettings()
            {
                Error = JsonErrorHandler
            };

            LogDebug = LoggingLevel.Info;
            IsConsole = true;
            Open();
        }

        public static void Reset()
        {
            Close();
            Open();
        }

        public static void Close()
        {
            try
            {
                _messageList.AddRange(_lastMessageList);
                _lastMessageList.Clear();
                _taskWriterCancellationToken.Cancel();
                _taskWriter.Wait();
                _taskWriter.Dispose();
                _isRunning = false;
            }
            catch (TaskCanceledException) { }
            catch (AggregateException e)
            {
                if (!e.InnerExceptions.Any(x => x.GetType() == typeof(TaskCanceledException)))
                {
                    throw new AggregateException(e);
                }
            }
        }

        public static void Debug(string message, object jsonSerializer = null, [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.Verbose)
            {
                Process("debug: " + message, ConsoleColor.Black, ConsoleColor.Gray, jsonSerializer, callerName: callerName);
            }
        }

        public static void Error(string message, object jsonSerializer = null, [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.Error)
            {
                Process("error: " + message, ConsoleColor.Red, ConsoleColor.Black, jsonSerializer, isError: true, callerName: callerName);
            }
        }

        public static void Exception(string message, object jsonSerializer = null, [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.Exception)
            {
                Process("exception: " + message, ConsoleColor.Black, ConsoleColor.Yellow, jsonSerializer, isError: true, callerName: callerName);
            }
        }

        public static void Highlight(string message, object jsonSerializer = null, [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.Warning)
            {
                ConsoleColor color = ConsoleColor.White;

                if (Regex.IsMatch(message, "succeed|success|info", RegexOptions.IgnoreCase))
                {
                    color = ConsoleColor.Green;
                }

                if (Regex.IsMatch(message, "fail|error|critical", RegexOptions.IgnoreCase))
                {
                    color = ConsoleColor.Red;
                }

                if (Regex.IsMatch(message, "exception|warn|terminate", RegexOptions.IgnoreCase))
                {
                    color = ConsoleColor.Yellow;
                }

                Process(message, color, null, jsonSerializer, callerName: callerName);
            }
        }

        public static void Info(string message,
                                        ConsoleColor? foregroundColor = null,
                                        ConsoleColor? backgroundColor = null,
                                        object jsonSerializer = null,
                                        bool minimal = false,
                                        bool lastMessage = false,
                                        bool isError = false,
                                        [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.Info)
            {
                Process(message, foregroundColor, backgroundColor, jsonSerializer, minimal, lastMessage, isError, callerName);
            }
        }

        public static void Info(string message, object jsonSerializer, [CallerMemberName] string callerName = "")
        {
            Info(message, null, null, jsonSerializer, callerName: callerName);
        }

        public static void Last(string message,
                                ConsoleColor? foregroundColor = null,
                                ConsoleColor? backgroundColor = null,
                                object jsonSerializer = null,
                                [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.Warning)
            {
                Process(message, foregroundColor, backgroundColor, jsonSerializer, false, true, callerName: callerName);
            }
        }

        public static void Min(string message,
                                ConsoleColor? foregroundColor = null,
                                ConsoleColor? backgroundColor = null,
                                object jsonSerializer = null,
                                [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.Info)
            {
                Process(message, foregroundColor, backgroundColor, jsonSerializer, true, callerName: callerName);
            }
        }

        public static void Open()
        {
            if (!_isRunning)
            {
                _taskWriterCancellationToken = new CancellationTokenSource();
                _taskWriter = new Task(TaskWriter, _taskWriterCancellationToken.Token);
                _taskWriter.Start();
                _isRunning = true;
            }
        }

        public static void ToFile(string message, object jsonSerializer = null, [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.File)
            {
                QueueMessage(false, new LogMessage()
                {
                    TimeStamp = DateTime.Now.ToString("o") + "::",
                    Message = $"{Thread.CurrentThread.ManagedThreadId}:{callerName}:trivial:{message}{serializeJson(jsonSerializer)}",
                    LogFileOnly = true
                });
            }
        }

        public static void Warning(string message, object jsonSerializer = null, [CallerMemberName] string callerName = "")
        {
            if (LogDebug >= LoggingLevel.Warning)
            {
                Process("warning: " + message, ConsoleColor.Yellow, ConsoleColor.Black, jsonSerializer, callerName: callerName);
            }
        }

        private static bool CheckLogFile(string logFile)
        {
            try
            {
                if (string.IsNullOrEmpty(logFile))
                {
                    LogFileEnabled = false;
                    Close();
                    return true;
                }

                if (!LogFileEnabled)
                {
                    if (!Directory.Exists(Path.GetDirectoryName(logFile)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(logFile));
                    }

                    File.Create(logFile).Close();
                    LogFileEnabled = true;
                }
                
                return true;
            }
            catch (Exception e)
            {
                LogFileEnabled = false;
                Exception(e.ToString());
                return false;
            }
        }

        private static void CloseFile()
        {
            if (_streamWriter != null)
            {
                _streamWriter.Flush();
                _streamWriter.Close();
                _streamWriter = null;
            }
        }

        private static void Log_JsonErrorHandler(object sender, Newtonsoft.Json.Serialization.ErrorEventArgs e)
        {
            e.ErrorContext.Handled = true;

            if (LogDebug >= LoggingLevel.Verbose)
            {
                Process($"json serialization error: {e.ErrorContext.OriginalObject} {e.ErrorContext.Path}");
            }
        }

        private static void Process(string message,
                                ConsoleColor? foregroundColor = null,
                                ConsoleColor? backgroundColor = null,
                                object jsonSerializer = null,
                                bool minimal = false,
                                bool lastMessage = false,
                                bool isError = false,
                                [CallerMemberName] string callerName = "")
        {
            if (!IsConsole & MessageLogged == null)
            {
                return;
            }

            if (!minimal)
            {
                message = $"{Thread.CurrentThread.ManagedThreadId}:{callerName}:{message}{serializeJson(jsonSerializer)}";
            }

            QueueMessage(lastMessage, new LogMessage()
            {
                TimeStamp = DateTime.Now.ToString("o") + "::",
                Message = message,
                BackgroundColor = backgroundColor,
                ForegroundColor = foregroundColor,
                IsError = isError
            });
        }

        private static void QueueMessage(bool lastMessage, LogMessage logMessage)
        {
            if (lastMessage)
            {
                _lastMessageList.Add(logMessage);
            }
            else
            {
                _messageList.Add(logMessage);
            }
        }

        private static void ResetColor(ConsoleColor? foregroundColor = null, ConsoleColor? backgroundColor = null)
        {
            if (foregroundColor != null | backgroundColor != null)
            {
                Console.ResetColor();
            }
        }

        private static string serializeJson(object jsonSerializer)
        {
            if (jsonSerializer != null)
            {
                try
                {
                    return Environment.NewLine + JsonConvert.SerializeObject(jsonSerializer, Formatting.Indented, _jsonSerializerSettings);
                }
                catch (Exception e)
                {
                    return Environment.NewLine + $"LOG:jsondeserialize error: {e.Message}";
                }
            }

            return string.Empty;
        }

        private static void SetColor(ConsoleColor? foregroundColor = null, ConsoleColor? backgroundColor = null)
        {
            if (foregroundColor != null)
            {
                Console.ForegroundColor = (ConsoleColor)foregroundColor;
            }

            if (backgroundColor != null)
            {
                Console.BackgroundColor = (ConsoleColor)backgroundColor;
            }
        }

        private static void TaskWriter()
        {
            while (!_taskWriterCancellationToken.IsCancellationRequested
                || (_taskWriterCancellationToken.IsCancellationRequested & _messageList.Any()))
            {
                while (_messageList.Any())
                {
                    foreach (LogMessage result in _messageList.DeListAll())
                    {
                        if (!result.LogFileOnly)
                        {
                            WriteMessage(result);
                        }

                        if (LogFileEnabled)
                        {
                            WriteFile(result);
                        }
                    }
                }

                Thread.Sleep(_threadSleepMs);
            }

            CloseFile();
        }

        private static void WriteFile(LogMessage result)
        {
            if (_streamWriter == null)
            {
                _streamWriter = new StreamWriter(LogFile, true);
            }

            _streamWriter.WriteLine(result.TimeStamp + result.Message);
        }

        private static void WriteMessage(LogMessage message)
        {
            if (IsConsole)
            {
                System.Diagnostics.Debug.Print(message.Message);
                SetColor(message.ForegroundColor, message.BackgroundColor);

                if (message.IsError)
                {
                    LogErrors++;
                    Console.Error.WriteLine(message.Message);
                }
                else
                {
                    Console.WriteLine(message.Message);
                }

                ResetColor(message.ForegroundColor, message.BackgroundColor);
            }

            LogMessageHandler logMessage = MessageLogged;
            logMessage?.Invoke(null, message);
        }
    }
}