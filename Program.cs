using docker_hosts_writer;
using System.Diagnostics;

// Windows event log variable
const string windowsEventSourceName = "Docker Hosts Writer (Worker)";
const string windowsEventLogName = "Docker Hosts Writer";

var isWindows = OperatingSystem.IsWindows();
if (isWindows)
{
    // Create event log
    if (!EventLog.SourceExists(windowsEventSourceName))
        EventLog.CreateEventSource(new EventSourceCreationData(windowsEventSourceName, windowsEventLogName));
}

// Parsing arguments
CommandLines commands = new CommandLines() { Args = args };
CommandLines.Options options = commands.Parse();

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "Docker Hosts Writer";
    })
    .UseSystemd()
    .ConfigureLogging(log =>
    {
        log.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        if (isWindows)
        {
            log.AddEventLog(options =>
            {
                options.SourceName = windowsEventSourceName;
                options.LogName = windowsEventLogName;
            });
        }

        // Verbose command only work without appsettings.json
        // Log level in appsettings.json has higher priority
        if (options.Verbose)
            log.SetMinimumLevel(LogLevel.Debug);
        else
            log.SetMinimumLevel(LogLevel.Information);
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton(commands);
        services.AddSingleton<Hosts>();
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
