using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace FileMonitor.Service;

public class KernelEventTracer
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

    [Flags]
    private enum ProcessAccessFlags : uint
    {
        QueryInformation = 0x400,
    }

    // dictionary to cache usernames and session id's to not make unnecessary system calls
    private static readonly ConcurrentDictionary<string, (string userName, string? sessionId)> UserCache = new();

    // cache for debouncing logic
    private static readonly ConcurrentDictionary<string, DateTime> LastLoggedEventTime = new();

    // stream writer for the log file
    private StreamWriter? _logFileWriter;

    private TraceEventSession? _traceEventSession;

    private readonly string _logFilePath;
    private readonly List<string> _foldersList;
    private readonly bool _includeSubdirectories;

    public KernelEventTracer(string logFilePath, List<string> folderList, bool includeSubdirectories)
    {
        _logFilePath = logFilePath;
        _foldersList = folderList;
        _includeSubdirectories = includeSubdirectories;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!TraceEventSession.IsElevated() ?? false)
        {
            Console.WriteLine("You need to be running as Administrator to capture kernel events.");
            return;
        }

        Console.WriteLine("Worker running at: {0}", DateTimeOffset.Now);

        await Task.Run(() =>
        {
            stoppingToken.Register(Cleanup);

            using var logFileWriter = new StreamWriter(_logFilePath, append: true);
            logFileWriter.AutoFlush = true;
            _logFileWriter = logFileWriter;

            using var session = new TraceEventSession("FileMonitor.Service.KernelFileIOTraceSession");
            _traceEventSession = session;

            try
            {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit |
                                             KernelTraceEventParser.Keywords.FileIO);
            }
            catch (COMException e)
            {
                Console.WriteLine(e.Message);
                return;
            }

            // comment FileIOCreate to reduce logging of all file create handles..
            session.Source.Kernel.FileIOCreate += KernelOnFileIOCreate;
            session.Source.Kernel.FileIOWrite += KernelOnFileIOWrite;
            session.Source.Kernel.FileIORead += KernelOnFileIORead;
            session.Source.Kernel.FileIODelete += KernelOnFileIODelete;
            session.Source.Kernel.FileIORename += KernelOnFileIORename;
            
            session.Source.Process();
            _traceEventSession = null;
            _logFileWriter = null;
        }, stoppingToken);
    }

    private void Cleanup()
    {
        if (_traceEventSession is not null)
        {
            _traceEventSession.Stop();
            _traceEventSession = null;
        }
    }

    private void KernelOnFileIORename(FileIOInfoTraceData obj)
    {
        LogEvent(obj.EventName, obj.TimeStamp, obj.ProcessID, obj.FileName);
    }

    private void KernelOnFileIODelete(FileIOInfoTraceData obj)
    {
        LogEvent(obj.EventName, obj.TimeStamp, obj.ProcessID, obj.FileName);
    }

    private void KernelOnFileIOCreate(FileIOCreateTraceData obj)
    {
        LogEvent(obj.EventName, obj.TimeStamp, obj.ProcessID, obj.FileName);
    }

    private void KernelOnFileIOWrite(FileIOReadWriteTraceData obj)
    {
        LogEvent(obj.EventName, obj.TimeStamp, obj.ProcessID, obj.FileName);
    }

    private void KernelOnFileIORead(FileIOReadWriteTraceData obj)
    {
        LogEvent(obj.EventName, obj.TimeStamp, obj.ProcessID, obj.FileName);
    }

    private static bool IsSubdirectory(string parentDir, string childDir)
    {
        try
        {
            if (childDir.StartsWith('\\'))
            {
                return false;
            }

            var parentUri = new Uri(parentDir.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? parentDir
                : parentDir + Path.DirectorySeparatorChar);
            var childUri = new Uri(childDir.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? childDir
                : childDir + Path.DirectorySeparatorChar);
            
            return parentUri.IsBaseOf(childUri);
        }
        catch (UriFormatException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid Uri! parent dir: {parentDir} | child dir: {childDir}");
            Console.ForegroundColor = ConsoleColor.Gray; //ConsoleColor
            return false;
        }
    }

    private void LogEvent(string eventName, DateTime eventTime, int processId, string fileName)
    {
        // skip zero-length file names, events generated by writing to the log file, and
        // files with .etl extensions (event trace log files)
        if (fileName.Length == 0 ||
            string.Equals(fileName, _logFilePath, StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileDirectory = Path.GetDirectoryName(fileName);
        if (fileDirectory is null)
        {
            return; // skip file without directory
        }

        // Check if the file is under any folder in the folder list (or subfolder if requested)
        var isInFolders = _foldersList.Any(folder =>
            string.Equals(fileDirectory, folder, StringComparison.OrdinalIgnoreCase) ||
            (_includeSubdirectories && IsSubdirectory(folder, fileDirectory))
        );

        if (!isInFolders)
        {
            return; // if received file path is not in the list, skip
        }

        string userName = GetUserNameFromProcessId(processId);
        if (string.IsNullOrEmpty(userName))
        {
            return; // if the process raising the event is not impersonated, skip the file
        }

        // Debounce logic: skip events that are too close together
        var eventKey = $"{eventName}:{fileName}";
        if (LastLoggedEventTime.TryGetValue(eventKey, out var lastEventTime))
        {
            if ((eventTime - lastEventTime).TotalMilliseconds < 500)
            {
                return; // skip duplicate events within 500ms window
            }
        }

        LastLoggedEventTime[eventKey] = eventTime;

        var eventTimeString = eventTime.ToString("yyyy-MM-dd HH:mm:ss.fff"); // Use 24-hour format for consistency
        string logMessage = $"{eventTimeString} | Event: {eventName} | FileName: {fileName} | User: {userName}";
        string fileEntry = $"{eventTimeString},{eventName},{fileName},{userName}";

        Console.WriteLine(logMessage);
        _logFileWriter?.WriteLine(fileEntry);
    }

    private static string GetUserNameFromProcessId(int processId)
    {
        try
        {
            IntPtr processHandle = OpenProcess(ProcessAccessFlags.QueryInformation, false, processId);
            if (processHandle != IntPtr.Zero)
            {
                if (OpenProcessToken(processHandle, 8, out var tokenHandle)) // TOKEN_QUERY = 8
                {
                    using (var identity = new WindowsIdentity(tokenHandle))
                    {
                        CloseHandle(tokenHandle);
                        CloseHandle(processHandle);
                        return identity.Name;
                    }
                }
                CloseHandle(processHandle);
            }
        }
        catch
        {
            return "Unknown (Exception raised)";
        }

        return "Unknown"; // Default to "Unknown" if unable to retrieve the username
    }
}