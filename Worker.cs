using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Newtonsoft.Json;

namespace docker_hosts_writer
{
    class DockerHosts
    {
        public string IPAddress { get; set; } = String.Empty;
        public List<string> Domain { get; set; } = [];
    }

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

        private string _domainPrefix = String.Empty;
        private string _domainSuffix = ".docker";

        private string _beginBlock = "# DOCKER CONTAINERS START (Autogenerated By docker-hosts-writer. DO NOT CHANGE.)";
        private string _endBlock = "# DOCKER CONTAINERS END";

        private DockerClient? _dockerClient;
        private Dictionary<string, List<DockerHosts>> _dockerHosts = new Dictionary<string, List<DockerHosts>>();

        private enum ServiceHosting
        {
            WindowsService,
            Systemd,
        }

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _dockerClient = GetClient();

            try
            {
                _logger.LogInformation("Starting docker-hosts-writer service");
                _dockerClient = GetClient();

                // Monitor events
                var containerEventsParameters = new ContainerEventsParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        {
                            "event", new Dictionary<string, bool>()
                            {
                                {"start", true},
                                {"die", true}
                            }
                        },
                        {
                            "type", new Dictionary<string, bool>()
                            {
                                {"container", true},
                            }
                        },
                    },
                };

                _logger.LogInformation("Monitoring docker events");
                DockerMonitor<JSONMessage> monitor = new DockerMonitor<JSONMessage>(async value =>
                {
                    try
                    {
                        var containerID = value.ID;

                        _logger.LogInformation($"New ${value.Status} events from container {containerID}");
                        _logger.LogDebug(JsonConvert.SerializeObject(value));

                        var containerDetails = await _dockerClient!.Containers.InspectContainerAsync(containerID, stoppingToken);
                        if (value.Status == "start")
                        {
                            foreach (KeyValuePair<string, EndpointSettings> network in containerDetails.NetworkSettings.Networks)
                            {
                                AddHost(containerID, network.Value.IPAddress, containerDetails.Config.Hostname);
                                AddHost(containerID, network.Value.IPAddress, containerDetails.Name.Replace("/", ""));
                                AddHost(containerID, network.Value.IPAddress, containerDetails.Config.Labels["com.docker.compose.project"]);
                            }
                        }
                        else if (value.Status == "die")
                        {
                            RemoveHost(containerID);
                        }

                        // Rewriting hosts file
                        DoRewritingHostsFile();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Exception is {ex.Message}");
                        _logger.LogDebug(ex.StackTrace);
                    }
                });
                await _dockerClient.System.MonitorEventsAsync(
                    containerEventsParameters,
                    monitor,
                    stoppingToken
                );
            }
            catch (TimeoutException)
            {
                _logger.LogInformation($"Oops! Somenthing went wrong. Likely the Docker engine not running at [{_dockerClient!.Configuration.EndpointBaseUri}]");
                _logger.LogInformation($"You can try to change the endpoint path");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception is {ex.Message}");
                _logger.LogError(ex.StackTrace);

                if (ex.InnerException != null)
                {
                    _logger.LogError($"InnerException is {ex.InnerException.Message}");
                    _logger.LogError(ex.InnerException.StackTrace);
                }
            }
        }

        private void DoRewritingHostsFile()
        {
            bool IsSystemd = IsRunningOn(ServiceHosting.Systemd);
            string hostsPath = (IsSystemd) ? "/etc/hosts" : $"{Environment.GetEnvironmentVariable("windir")}\\system32\\drivers\\etc\\hosts";

            // Get old hosts data
            if (!File.Exists(hostsPath))
            {
                throw new FileNotFoundException($"Could not find hosts file at: {hostsPath}");
            }
            List<string> hostsLines = File.ReadAllLines(hostsPath).ToList();
            List<string> fiterLines = FilterListNotBetweenBlock(hostsLines, _beginBlock, _endBlock);

            List<string> newHostLines = fiterLines;
            newHostLines.Add(_beginBlock);
            foreach (KeyValuePair<string, List<DockerHosts>> hosts in _dockerHosts)
            {
                foreach (DockerHosts host in hosts.Value)
                {
                    var entry = $"{host.IPAddress}\t{String.Join(" ", host.Domain)}";
                    _logger.LogInformation($"Adding {entry}");
                    newHostLines.Add(entry);
                }
            }
            newHostLines.Add(_endBlock);

            _logger.LogDebug(JsonConvert.SerializeObject(_dockerHosts));

            // Writing to file
            File.WriteAllLines(hostsPath, newHostLines);
        }

        private void AddHost(string containerId, string ip, string domain)
        {
            domain = !String.IsNullOrEmpty(_domainPrefix) && !domain.StartsWith(_domainSuffix) ? $"{_domainPrefix}{domain}" : domain;
            domain = !String.IsNullOrEmpty(_domainSuffix) && !domain.EndsWith(_domainSuffix) ? $"{domain}{_domainSuffix}" : domain;

            if (_dockerHosts.ContainsKey(containerId))
            {
                int index = _dockerHosts[containerId].FindIndex(x => x.IPAddress == ip);
                if (index > -1)
                {
                    bool isExists = _dockerHosts[containerId][index].Domain.Contains(domain);
                    if (!isExists)
                    {
                        _dockerHosts[containerId][index].Domain.Add(domain);
                    }
                    return;
                }
            }

            _dockerHosts.Add(containerId, new List<DockerHosts>
            {
                new DockerHosts()
                {
                    IPAddress = ip,
                    Domain = [domain]
                }
            });
        }

        private void RemoveHost(string containerId)
        {
            _logger.LogInformation("Removing hosts");
            _dockerHosts.Remove(containerId);
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

        private bool IsRunningOn(ServiceHosting hosting)
        {
            switch (hosting)
            {
                case ServiceHosting.WindowsService:
                    return WindowsServiceHelpers.IsWindowsService();
                case ServiceHosting.Systemd:
                    return SystemdHelpers.IsSystemdService();
                default:
                    return false;
            }
        }

        private DockerClient GetClient()
        {
            string endpoint = "npipe://./pipe/docker_engine";
            return new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
        }
    }
}