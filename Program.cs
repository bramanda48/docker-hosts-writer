using docker_hosts_writer;
using System.Diagnostics;


// Windows event log variable
const string windowsEventSourceName = "Docker Hosts Writer (Worker)";
const string windowsEventLogName = "Docker Hosts Writer";

if (OperatingSystem.IsWindows())
{
    // Create event log
    if (!EventLog.SourceExists(windowsEventSourceName))
        EventLog.CreateEventSource(new EventSourceCreationData(windowsEventSourceName, windowsEventLogName));
}

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
        log.AddEventLog(options =>
        {
            options.SourceName = windowsEventSourceName;
            options.LogName = windowsEventLogName;
        });
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
