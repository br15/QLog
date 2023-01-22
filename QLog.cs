using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace qLog
{
    /// <summary>
    /// This library provides a fast and light weight application wide logging facility in a multi-threaded environment. qLog is itself a 
    /// multi-threaded library. This library was originally designed to simply log to an out put file. Over time, support for other output destination 
    /// were added such as EventLog, Message Queue, various flavours of database and various flavours of cloud storage. After a while (15 years) 
    /// I decided to get back to basics and stop trying to support every output type possible and just concentrate on the core purpose of the
    /// library. This code is the fruits of my labour. 
    /// 
    /// Usage:
    /// 
    ///     Log.Info($"An informational log message with an embedded variable at the end '{someVar}'.");
    ///     
    /// Note: Always make a call to Log.Shutdown() when you application is closing, otherwise the Log worker thread won't stop running.
    ///     
    /// </summary>
    public static class Log
    {

        #region Private data members.
        // The log message queue which is processed by the ProcessLogMessageQueue method. This method runs on its own thread.
        private static Queue<LogMessage>    logMessageQueue = new Queue<LogMessage>();

        // Used to tell ProcessLogMessageQueue method that it has work to do or that we are shutting down.
        private static AutoResetEvent       workToDoEvent = new AutoResetEvent(false);
        private static Thread               worker;

        // This is used by the worker thread to write messages to out log file.
        private static StreamWriter         swLogFile;

        // Get the machine name, application name and process id once here so we never have to again. Use to generate a log file name.
        private static readonly string      machineName      = Environment.MachineName;
        private static readonly string      processId        = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        private static readonly string      applicationName  = GetApplicationName();

        // Every entry in the log file gets its own sequence number.  A simple way to see if any messages are missing.
        private static int                  seqNo;

        // The unique name of our current log file.
        private static string               logFileName;

        // This flag is used to tell the worker thread to shutdown.
        private static bool shutdown;

        // File switching related data.
        private static int                  linesWritten;
        private static int                  logFilesWritten;
        private static string               currentDay;
        private static int                  currentHour;
        public enum SwitchLogOptions { NEVER, DAILY, HOURLY, NUMBER_OF_LINES = 4 } // Flags can be ORed together to allow multiple options to be selected.
        public enum Level { DEBUG, VERBOSE, INFO, WARN, ERROR, CRITICAL }
        #endregion

        /// <summary>
        /// Our static constructor.
        /// </summary>
        static Log()
        {
            // To speed things up, our I/O is performed on a separate thread.
            CreateMessageProcessingThread();
        }

        #region Public API.
        #region Public Properties. Before calling any of the logging methods, the follow options should be set. If the default values work for you, feel free to continue.
        public static long MaximumLinesInLog { get; set; }
        public static SwitchLogOptions SwitchLog { get; set; } = SwitchLogOptions.NEVER;

        // Set the default log level.
        public static Level LogLevel { get; set; } = Level.INFO;

        // Please note that the user application won't shutdown cleanly unless "Shutdown" is called to terminate the worker thread of qLog.
        public static void Shutdown()
        {
            // Indicate that we are about to shutdown. 
            shutdown = true;

            // Wake up the work so that it can shutdown.
            workToDoEvent.Set();

            // Tidy up any empty files if required to.
            DeleteEmptyLogFile();
        }

        // The log file will be written to the same directory that the calling application resides in.
        private static string destinationDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static string DestinationDirectory
        {
            get { return destinationDirectory; }
            set
            {
                try
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        if (!Directory.Exists(value))
                        {
                            Directory.CreateDirectory(value);
                        }

                        destinationDirectory = value;
                    }
                }
                catch
                {
                    // Not possible to report this error as we don't have a log to write to.
                    throw new Exception(string.Format($"qLog could not create its log file directory: {value}"));
                }
            }
        }
        #endregion Properties.

        #region Message logging methods.
        public static void Debug(string msg)    { QueueMessage(msg, Level.DEBUG); }
        public static void Verbose(string msg)  { QueueMessage(msg, Level.VERBOSE); }
        public static void Info(string msg)     { QueueMessage(msg, Level.INFO); }
        public static void Warn(string msg)     { QueueMessage(msg, Level.WARN); }
        public static void Error(string msg)    { QueueMessage(msg, Level.ERROR); }
        public static void Critical(string msg) { QueueMessage(msg, Level.CRITICAL); }
        #endregion

        #endregion

        #region Helper methods.
        private static void CreateMessageProcessingThread()
        {
            // Start the message processing method on its own thread.
            worker      = new Thread(ProcessLogMessageQueue);
            worker.Name = "qLog Worker";
            //worker.IsBackground = true; 
            worker.Start();
        }

        private static string GetApplicationName()
        {
            // Extract the name of the running executable minus the '.exe' suffix.
            string[] args = Environment.GetCommandLineArgs();
            args = args[0].Split('\\');
            args = args[args.Length - 1].Split('.');

            // Return the application name. Used in generating our log file name.
            return args[0];

        }
        private static string BuildLogFileName()
        {
            return $@"{destinationDirectory}\{machineName}_{applicationName}_{processId}_{DateTime.Now.ToString("yyyy'-'MM'-'dd'_'HH'-'mm'-'ss")}_Seg{logFilesWritten}.qlog";
        }
        private static void QueueMessage(string message, Level logLevel = Level.INFO) 
        {
            // Don't waste our time if we aren't logging this level of message.
            if (logLevel >= LogLevel)
            {
                try
                {
                    lock (logMessageQueue)
                    {
                        // We make a copy of the original message text so that we always log the value at the time of calling not at the time when
                        // ProcessLogMessageQueue method processes it.
                        LogMessage msg = new LogMessage(logLevel, ++seqNo, DateTime.Now, Thread.CurrentThread.ManagedThreadId, GetCaller(), string.Copy(message));
                        logMessageQueue.Enqueue(msg);
                    }
                }
                catch (Exception ex)
                { }
                // Tell the worker thread (ProcessLogMessageQueue()) it has work to do.
                workToDoEvent.Set();
            }
        }

        /// <summary>
        /// This method runs on its own thread to improve performance. 
        /// Please note that I do not use WriteLineAsync as tests have shown that when we are very stressed WriteLineAsync is actually much slower.
        /// </summary>
        private static void ProcessLogMessageQueue()
        {
            LogMessage message;

            while (!shutdown)
            {
                // Wait for a message to be put on the queue.
                workToDoEvent.WaitOne();

                if (logMessageQueue.Count > 0)
                {
                    // Now process all the messages in the queue.
                    while (logMessageQueue.Count > 0)
                    {
                        try
                        {
                            // Before we write this message, check whether it should be written to a new log file. 
                            SwitchToNewFileIfRequired();

                            lock (logMessageQueue)
                            {
                                // Get a message from the queue to write to the current log file.
                                 message = logMessageQueue.Dequeue();
                            }

                            // Write our message to the log file. Test show that when we are very stressed WriteLineAsync is actually much slower.
                            swLogFile.WriteLine(message.ToString());

                            // Flush it from the I/O buffer to disk so we don't lose anything if we crash.
                            swLogFile.Flush();

                            // Keep track of how many lines have been written to this specific log file.
                            ++linesWritten;
                        }
                        catch (Exception)
                        {
                            // This error can be detected by a gap in the sequence numbers in the log.
                        }
                    }
                }
            }

            // We are shutting down.  Delete the log file if it's empty.  This prevents a build up of empty files.
            if (0 == linesWritten)
            {
                try
                {
                    // Close our current log file so that we may delete it.
                    if (swLogFile != null)
                    {
                        // Close our log file. We don't need to call flush as it is already called after every write.
                        // Calling 'Close' also silently invokes Dispose.
                        swLogFile.Close();

                        // Delete the empty file from disk.
                        File.Delete(logFileName);
                    }
                }
                catch (Exception)
                {
                   // This allows us to at least set a breakpoint in-case something bad happens.
                }
            }
        }

        private static void SwitchToNewFileIfRequired()
        {
            // Force the creation of the log file if this is the first time this method has been called.
            bool switchFile = swLogFile == null;

            DateTime dt     = DateTime.Now;


            try
            {

                // Have any of our switch conditions been met?
                switch (SwitchLog)
                {
                    // Never switch from the original log file. Everything in one file.
                    case SwitchLogOptions.NEVER:  
                        break;
                    
                    // Create a new log file each day. 
                    case SwitchLogOptions.DAILY:
                        if (currentDay != dt.DayOfWeek.ToString())
                        {
                            // The day has changed. Save the new day name.
                            currentDay = dt.DayOfWeek.ToString();
                            switchFile = true; 
                        }
                        break;

                    // Create a new log file every hour.
                    case SwitchLogOptions.HOURLY:
                        if (currentHour != dt.Hour)
                        {
                            // The hour has changed. Save the new hour.
                            currentHour = dt.Hour;
                            switchFile = true;
                        }
                        break;

                    // Create a new log file as soon as it reaches the specified size limit.
                    case SwitchLogOptions.NUMBER_OF_LINES:
                        if (linesWritten >= MaximumLinesInLog)
                        {
                            // We've reached our maximum log file size. 
                            switchFile = true;
                        }
                        break;


                    default:
                        break;
                }

                if (switchFile)
                {
                    // Close our current log file if we have one. This might be the first time through. 
                    if (swLogFile != null) swLogFile.Close();
                    
                    // Create a new log file.
                    CreateLogFile();
                }
            }
            catch (Exception)
            {
                // This allows us to at least set a breakpoint in-case something bad happens.
            }
        }

        private static void CreateLogFile(int bufferSizeInMB = 10)
        {
            int MB = 1024 * 1024;

            try
            {
                // Reset line counter.
                linesWritten = 0;

                // Increment the number of times we've created a new log file.
                ++logFilesWritten;

                // Set the new name of the log file name if it's not already set. 
                logFileName = BuildLogFileName();

                // Create the new log file on disk.
                swLogFile = new StreamWriter(new FileStream(logFileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSizeInMB * MB));
            }
            catch (Exception)
            {
                // This allows us to at least set a breakpoint in-case something bad happens.
            }
        }

        private static void DeleteEmptyLogFile()
        {
            // If the current log file is open but has not been written to, delete it. 
            if (swLogFile != null && logFilesWritten == 0)
            {
                swLogFile.Close();
                File.Delete(logFileName);
            }
        }

        /// <summary>
        /// This method gets the full caller name (namespace + class name + method name) of one of the Logxxx methods.
        /// Note: In the call to GetFrame we use a hard coded value of 3.  We can safely do this as we know our code 
        /// path, for example:
        /// 
        /// Caller
        /// ^
        /// |
        /// LogInfo("Some message") 
        /// ^
        /// |
        /// Log("Some message") 
        /// 
        /// </summary>
        /// <returns>The full caller name (namespace + class name + method name).</returns>
        private static string GetCaller()
        {
            string caller, typeName;

            try
            {
                StackTrace stackTrace       = new StackTrace();
                StackFrame stackFrame       = stackTrace.GetFrame(3);
                MethodBase stackFrameMethod = stackFrame.GetMethod();
                typeName                    = stackFrameMethod.ReflectedType.FullName;
                caller                      = $"{typeName}.{stackFrameMethod.Name}";
            }
            catch
            {
                caller = "We failed to locate caller.";
            }

            return caller;
        }

        #endregion
    }

    public struct LogMessage
    {   
        public DateTime         timestamp;
        public int              threadId;
        public Log.Level        logLevel;
        public string           caller;
        public string           message;
        public long             seqNo;

        public LogMessage(Log.Level logLevel, long seqNo, DateTime timestamp, int threadId,  string caller, string message)
        {
            this.logLevel   = logLevel;
            this.seqNo      = seqNo;
            this.timestamp  = timestamp;
            this.threadId   = threadId;
            this.caller     = caller;
            this.message    = message;
            
        }

        public override string ToString()
        {
            return $"{logLevel} {seqNo:n0} {timestamp.ToString("dd/MM/yyyy-HH:mm:ss.ffff")} {threadId} {caller} {message}";
        }
    }
}