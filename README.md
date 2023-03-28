# qLog
## Overview
This library provides a fast, light weight and thread safe, application wide logging facility in a multi-threaded environment. qLog is itself a 
multi-threaded library. This library was originally designed to simply log to an out put file. Over time, support for other output destination 
were added such as EventLog, Message Queue, various flavours of database and various flavours of cloud storage. After a while (15 years) 
I decided to get back to basics and stop trying to support every output type possible and just concentrate on the core purpose of the
library. This code is the fruits of my labour. 

## Usage
In order to use qLog in your application, simply add a reference in your project to QLog and add a "using qLog;" in your code. If you want to go with 
the default setting, that's all you need to do. You can now start adding log messages to any part of your code in any thread. For example:

Log.Error($"We had a problem: '{ex.Message)}'");

The only other thing you need to do is call "Log.Shutdown();" before your application exits. This will stop our I/O thread and allow your application to close cleanly.

## Log Record Format
In addition to the message text provide by the user in a call to one of the Log.xxx methods; several other pieces of information are also written to the log.
They are:

+ **_Level_** This will be one of the following values based upon the Log method called - CRITICAL, ERROR, WARN, INFO, VERBOSE and DEBUG.
+ **_Seq#_** This is the sequence number of the record, starting at 1. If the log is being segmented into multiple files, the sequence numbers
will be contiguous through all the files.
+ **_Date_** The date that the record was written.
+ **_Time_** The time that the record was written, down the millisecond.
+ **_ThreadId_** The id of the thread that wrote the record.
+ **_Caller_** The full name of the method that called the Log method.
+ **_Message_** The message text passed to the Log.xxx method.

Here's a sample of the output as displayed in our associated QView application:

![QView in action](https://github.com/br15/QLog/blob/master/QViewScreenshot.png?raw=true)

## Settings
As mentioned earlier, qLog will work straight out of the box with no need to provide any runtime parameters. However, if you do want to override the default settings,
here are the options available to you:

### SwitchLog
This property allows you to control if and when the log file will be segmented (close the current log file and open a new one). Your option are:
+ **_SwitchLogOptions.NEVER_** You will only ever have one log file created during the run of your application. **_This is the default value._**
+ **_SwitchLogOptions.DAILY_** The log will be closed at midnight and a new log file will be created.
+ **_SwitchLogOptions.HOURLY_** The log will be closed on the hour and a new log file will be created. 
+ **_SwitchLogOptions.NUMBER_OF_LINES_** The log file will be closed when the specified number of messages has been written to the log file and a new log file will be created.
+ **_SwitchLogOptions.NUMBER_OF_LINES_OR_DAILY_** The log file will be closed when the specified number of messages has been written to the log or when the day has changed. A new log file will be created.
+ **_SwitchLogOptions.NUMBER_OF_LINES_OR_HOURLY_** The log file will be closed when the specified number of messages has been written to the log or the hour has changed. A new log file will be created.

### Level
Level determines the type of messages that will be written to the log file. The message levels are as follows:
+ **_Level.OFF_** No messages will be written to the log file.
+ **_Level.CRITICAL_** Only messages from Log.Critical() will be written to the log file.
+ **_Level.ERROR_** Only messages from Log.Error() and above (Critical) will be written to the log file.
+ **_Level.WARN_** Only messages from Log.Warn() and above (Critical and Error) will be written to the log file.
+ **_Level.INFO_** Only messages from Log.Info() and above (Critical, Error and Warn) will be written to the log file. **_This is the default value._**
+ **_Level.VERBOSE_** Only messages from Log.Verbose() and above (Critical, Error, Warn and Info) will be written to the log file.
+ **_Level.DEBUG_** All messages will be written to the log file.

### DestinationDirectory
This is the directory into which the log files should be written. By default the log file will be written to the same directory that the calling application resides in.

## qLog File Names
The qLog files are name using the following convention:

DestinationDirectory\machineName_applicationName_processId_yyyy-MM-dd_HH-mm-ss_Segn.qlog

+ **_DestinationDirectory_** The value of the DestinationDirectory property.
+ **_machineName_** The value of Environment.MachineName.
+ **_applicationName_** The name of the executable using qLog.
+ **_processId_** The value of Process.GetCurrentProcess().Id.
+ **_yyyy-MM-dd_HH-mm-ss_** The date and time that the file was created.
+ **_Segn_** The segment number of this log file. If the **_SwitchLog_** property is set to **_SwitchLogOptions.NEVER_**, then the value of "n" will always be 1.
For all other **_SwitchLog_** values "n" will start at 1 and be incremented at each log switch.
+ **_.qlog_** The file extension is set to qlog to that our associated QView application can easily identify the file as being in the qlog format.

Here's an example of a log file name:

XPS17_QLogTest_11804_2023-02-15_15-17-38_Seg1.qlog

## General Behaviour
1. If an application doesn't actually write to the log file (only reporting error and it was a clean run) during a run, it will never be created.
2. All the property settings (SwitchLog, Level and DestinationDirectory) may be changed while the application is running.
 

