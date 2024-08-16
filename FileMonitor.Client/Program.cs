using FileMonitor.Service;
using System.Threading;

namespace FileMonitor.Client;

public class Program
{
    private const bool IncludeSubdirectories = true;
    private const string DefaultFoldersListFileName = "folders.txt";
    private const string LogFileName = "filemonitor.csv";

    static async Task Main(string[] args)
    {
        // usage: FileMonitor.Client [foldersListFile]

        var foldersListFile = args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, DefaultFoldersListFileName) : args[0];

        Console.WriteLine($"Using {foldersListFile} file\r\nfor filtered folders list. Include subdirectories: {IncludeSubdirectories}.");

        if (!File.Exists(foldersListFile))
        {
            Console.WriteLine("File not found, exiting.");
            return;
        }

        var fileContent = await File.ReadAllTextAsync(foldersListFile);
        fileContent = fileContent.TrimEnd(';');

        var folders = fileContent.Split(';');
        if (folders.Length == 0)
        {
            Console.WriteLine("File does not have any folders to watch, exiting.");
            return;
        }

        var logFile = Path.Combine(AppContext.BaseDirectory, LogFileName);
        if (File.Exists(logFile))
        {
            Console.WriteLine("There is a previous log file, what would you like to do with it: [D]elete [A]ppend [E]xit");
            while (true)
            {
                var keyInfo = Console.ReadKey(true);
                
                if (keyInfo.Key == ConsoleKey.D)
                {
                    File.Delete(logFile);
                    break;
                }
                
                if (keyInfo.Key == ConsoleKey.A)
                {
                    break;
                }

                if (keyInfo.Key == ConsoleKey.E)
                {
                    return;
                }
            }
        }

        var foldersList = new List<string>(folders.Length);
        foreach (var folder in folders)
        {
            var folderName = folder.Trim();
            if (Directory.Exists(folderName))
            {
                foldersList.Add(folderName);
                Console.WriteLine($"+ {folderName}");
            }
            else
            {
                Console.WriteLine($"- {folderName}");
            }
        }

        if (foldersList.Count != folders.Length)
        {
            string s = foldersList.Count == 0 ? "All" : "Some";
            Console.WriteLine(
                $"{s} folders in the list are not present at the moment but the application will start monitoring anyway using indicated folders.");
        }

        Console.WriteLine("Press Enter to stop...");

        // Create a cancellation token source
        var cancellationTokenSource = new CancellationTokenSource();

        // Start the event tracer asynchronously
        var eventTracer = new KernelEventTracer(logFile, foldersList, IncludeSubdirectories);
        var runTask = eventTracer.RunAsync(cancellationTokenSource.Token);

        // Wait for the Enter key to be pressed
        Console.ReadLine();
        Console.WriteLine("Stopping event tracer...");

        // Cancel the event tracer
        await cancellationTokenSource.CancelAsync();

        // Await the completion of the task
        await runTask;

        Console.WriteLine("File monitor stopped.");
    }
}
