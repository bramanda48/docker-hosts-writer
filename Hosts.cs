using Docker.DotNet;
using Docker.DotNet.Models;

namespace docker_hosts_writer
{
    public class DockerHosts
    {
        public string IPAddress { get; set; } = String.Empty;
        public List<string> Domain { get; set; } = [];
    }

    public class Hosts
    {
        private readonly ILogger<Hosts> _logger;

        private DockerClient? _dockerClient;
        private Dictionary<string, List<DockerHosts>> _dockerHosts;

        private string _domainPrefix = String.Empty;
        private string _domainSuffix = String.Empty;

        public Hosts(ILogger<Hosts> logger)
        {
            _logger = logger;
            _dockerHosts = new Dictionary<string, List<DockerHosts>>();
        }

        public void SetConfig(
            DockerClient dockerClient,
            string defaultPrefix,
            string defaultSuffix)
        {
            _dockerClient = dockerClient;
            _domainPrefix = defaultPrefix;
            _domainSuffix = defaultSuffix;
        }

        public Dictionary<string, List<DockerHosts>> GetHost() => _dockerHosts;

        public async Task AddHost(string containerId, CancellationToken cancellationToken)
        {
            var domains = new List<string>();
            var containerDetails = await _dockerClient!.Containers.InspectContainerAsync(containerId, cancellationToken);

            domains.Add(containerDetails.Config.Hostname);
            domains.Add(containerDetails.Name.Replace("/", ""));

            if (containerDetails.Config.Labels.ContainsKey("com.docker.compose.project"))
                domains.Add(containerDetails.Config.Labels["com.docker.compose.project"]);
            if (containerDetails.Config.Labels.ContainsKey("com.docker.compose.service"))
                domains.Add(containerDetails.Config.Labels["com.docker.compose.service"]);

            foreach (KeyValuePair<string, EndpointSettings> network in containerDetails.NetworkSettings.Networks)
                AddHost(containerId, network.Value.IPAddress, domains);
        }

        public void AddHost(string containerId, string ip, string domain)
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

        public void AddHost(string containerId, string ip, List<string> domains)
        {
            domains = domains.Distinct().Select(domain =>
            {
                domain = !String.IsNullOrEmpty(_domainPrefix) && !domain.StartsWith(_domainSuffix) ? $"{_domainPrefix}{domain}" : domain;
                domain = !String.IsNullOrEmpty(_domainSuffix) && !domain.EndsWith(_domainSuffix) ? $"{domain}{_domainSuffix}" : domain;
                return domain;
            }).ToList();

            if (_dockerHosts.ContainsKey(containerId))
            {
                _dockerHosts[containerId].Add(new DockerHosts()
                {
                    IPAddress = ip,
                    Domain = domains
                });
                return;
            }
            _dockerHosts.Add(containerId, new List<DockerHosts>
            {
                new DockerHosts()
                {
                    IPAddress = ip,
                    Domain = domains
                }
            });
        }

        public void RemoveHost(string containerId)
        {
            _logger.LogInformation(3001, $"Removing hosts (containerID={containerId.Substring(0, 12)})");
            _dockerHosts.Remove(containerId);
        }
    }
}
