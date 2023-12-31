using Docker.DotNet;
using Docker.DotNet.Models;
using Newtonsoft.Json;

namespace docker_hosts_writer
{
    class DockerMonitor<T> : IProgress<T>
    {
        public delegate void CallbackDelegate(T value);

        public event CallbackDelegate Callback;

        public DockerMonitor(CallbackDelegate callback)
        {
            Callback += callback;
        }

        public void Report(T value)
        {
            Callback?.Invoke(value);
        }
    }

    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly CommandLines _commands;
        private readonly Hosts _hosts;

        private DockerClient? _dockerClient;
        private CommandLines.Options _options;

        private string _beginBlock = "# DOCKER CONTAINERS START (Autogenerated By docker-hosts-writer. DO NOT CHANGE.)";
        private string _endBlock = "# DOCKER CONTAINERS END";

        private bool _isFirstRunning = true;

        public Worker(
            ILogger<Worker> logger,
            ILoggerFactory loggerFactory,
            CommandLines commands,
            Hosts hosts)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _commands = commands;
            _hosts = hosts;
            _options = new CommandLines.Options();
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(1000, "Starting docker-hosts-writer service");

            var args = _commands.Args;
            if (args.Length > 0)
            {
                _logger.LogInformation(1000, $"Running with argument: {String.Join(" ", args)}");
            }

            _options = _commands.Parse();
            _hosts.SetPrefixSuffix(_options.Prefix, _options.Suffix);
            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_dockerClient == null)
                    _dockerClient = await GetClient(cancellationToken);

                // Setup docker client
                _hosts.SetDockerClient(_dockerClient);

                // First running
                await DoWhenFirstRunning(cancellationToken);

                // Monitor events
                var containerEventsParameters = new ContainerEventsParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        {
                            "event", new Dictionary<string, bool>()
                            {
                                {"connect", true},
                                {"disconnect", true}
                            }
                        },
                        {
                            "type", new Dictionary<string, bool>()
                            {
                                {"network", true},
                            }
                        },
                    },
                };

                _logger.LogInformation(1000, "Monitoring docker events");
                DockerMonitor<Message> monitor = new DockerMonitor<Message>(async value =>
                {
                    try
                    {
                        string containerId = value.Actor.Attributes["container"];
                        string networkName = value.Actor.Attributes["name"];

                        _logger.LogInformation(1000, $"New ${value.Action} events from container {containerId.Substring(0, 12)} to network {networkName}");
                        _logger.LogDebug(JsonConvert.SerializeObject(value));

                        if (value.Action == "connect")
                            await _hosts.AddHost(containerId, networkName, cancellationToken);
                        else if (value.Action == "disconnect")
                            _hosts.RemoveHost(containerId, networkName);

                        // Rewriting hosts file
                        DoRewritingHostsFile();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(2002, $"Exception is {ex}");
                    }
                });
                await _dockerClient.System.MonitorEventsAsync(
                    containerEventsParameters,
                    monitor,
                    cancellationToken
                );
            }
            catch (IOException)
            {
                // Re-execute if docker disconnected
                _dockerClient = await GetClient(cancellationToken);
                await ExecuteAsync(cancellationToken);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(2003, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(2000, $"Exception is {ex}");
                if (ex.InnerException != null) _logger.LogError($"InnerException is {ex} ");
            }
        }

        private async Task DoWhenFirstRunning(CancellationToken cancellationToken)
        {
            if (!_isFirstRunning) return;

            _isFirstRunning = false;
            ContainersListParameters containerParams = new ContainersListParameters()
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>()
                {
                    {
                        "status", new Dictionary<string, bool>()
                        {
                            {"running", true}
                        }
                    }
                }
            };
            _logger.LogInformation(1000, "Detected first running");

            var containers = await _dockerClient!.Containers.ListContainersAsync(containerParams);
            foreach (var value in containers)
            {
                foreach (KeyValuePair<string, EndpointSettings> network in value.NetworkSettings.Networks)
                {
                    await _hosts.AddHost(value.ID, network.Key, cancellationToken);
                }
            }

            // Rewriting hosts file
            DoRewritingHostsFile();
        }

        private void DoRewritingHostsFile()
        {
            var hostsFile = _options.HostsFile;
            var dockerHosts = _hosts.GetHost();

            // Get old hosts data
            if (!File.Exists(hostsFile))
            {
                throw new FileNotFoundException($"Could not find hosts file at: {hostsFile}");
            }
            List<string> hostsLines = File.ReadAllLines(hostsFile).ToList();
            List<string> fiterLines = FilterListNotBetweenBlock(hostsLines, _beginBlock, _endBlock);

            _logger.LogDebug(1000, JsonConvert.SerializeObject(dockerHosts));

            List<string> newHostLine = fiterLines;
            List<string> newHostLogs = new List<string>() { "Adding hosts:" };

            newHostLine.Add(_beginBlock);
            foreach (var container in dockerHosts)
            {
                foreach (var network in container.Value)
                {
                    var entry = $"{network.Value.IPAddress}\t{String.Join(" ", network.Value.Domain)}";
                    newHostLine.Add(entry);
                    newHostLogs.Add("\t" + entry);
                }
            }
            newHostLine.Add(_endBlock);

            if (dockerHosts.Count > 0)
                _logger.LogInformation(3000, String.Join("\n", newHostLogs));

            WriteAllLines(hostsFile, newHostLine);
        }

        private void WriteAllLines(string path, List<string> contents, int retry = 0)
        {
            try
            {
                File.WriteAllLines(path, contents);
            }
            catch (IOException)
            {
                if (retry <= 2)
                {
                    //Recall with delay
                    retry++;
                    Thread.Sleep(2000);
                    WriteAllLines(path, contents, retry);
                }
                else throw;
            }
        }

        private List<string> FilterListNotBetweenBlock(List<string> list, string begin, string end)
        {
            List<string> newList = new List<string>();
            bool isInTargetBlocks = false;
            foreach (string item in list)
            {
                if (Array.Exists([begin, end], b => b.Equals(item)))
                {
                    isInTargetBlocks = !isInTargetBlocks;
                }
                else if (!isInTargetBlocks)
                {
                    newList.Add(item);
                }
            }
            return newList;
        }

        private async Task<DockerClient> GetClient(CancellationToken cancellationToken)
        {
            // If cancelation requsted
            cancellationToken.ThrowIfCancellationRequested();

            // Trying connect to docker
            DockerClientConfiguration config = new DockerClientConfiguration(new Uri(_options.Endpoints));
            DockerClient client = config.CreateClient();

            try
            {
                VersionResponse response = await client.System.GetVersionAsync();
                _logger.LogInformation(1000, $"Connected to Docker {response.Version} with API vesion {response.APIVersion} (Arch: {response.Arch})");
                return client;
            }
            catch (Exception)
            {
                _logger.LogError(2001,
                    $"Oops! Somenthing went wrong. Likely the Docker engine not running at [{client.Configuration.EndpointBaseUri}]\n" +
                    $"Retrying in 5 seconds..."
                );
                await Task.Delay(5000, cancellationToken);
                return await GetClient(cancellationToken);
            }
        }
    }
}