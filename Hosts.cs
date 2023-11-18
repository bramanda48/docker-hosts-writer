using Docker.DotNet;

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
        private Dictionary<string, Dictionary<string, DockerHosts>> _dockerHosts;

        private string _domainPrefix = String.Empty;
        private string _domainSuffix = String.Empty;

        public Hosts(ILogger<Hosts> logger)
        {
            _logger = logger;
            _dockerHosts = new Dictionary<string, Dictionary<string, DockerHosts>>();
        }

        public void SetDockerClient(DockerClient dockerClient)
        {
            _dockerClient = dockerClient;

        }

        public void SetPrefixSuffix(
            string defaultPrefix,
            string defaultSuffix)
        {
            _domainPrefix = defaultPrefix;
            _domainSuffix = defaultSuffix;
        }

        public Dictionary<string, Dictionary<string, DockerHosts>> GetHost() => _dockerHosts;

        public async Task AddHost(string containerId, string networkName, CancellationToken cancellationToken)
        {
            var domains = new List<string>();
            var containerDetails = await _dockerClient!.Containers.InspectContainerAsync(containerId, cancellationToken);

            domains.Add(containerDetails.Config.Hostname);
            domains.Add(containerDetails.Name.Replace("/", ""));

            if (containerDetails.Config.Labels.ContainsKey("com.docker.compose.project"))
                domains.Add(containerDetails.Config.Labels["com.docker.compose.project"]);
            if (containerDetails.Config.Labels.ContainsKey("com.docker.compose.service"))
                domains.Add(containerDetails.Config.Labels["com.docker.compose.service"]);

            var network = containerDetails.NetworkSettings.Networks[networkName];
            if (network != null)
                AddHost(containerId, networkName, network.IPAddress, domains);
        }

        private void AddHost(string containerId, string networkName, string ip, List<string> domains)
        {

            domains = domains.Distinct().Select(domain =>
            {
                domain = !String.IsNullOrEmpty(_domainPrefix) && !domain.StartsWith(_domainSuffix) ? $"{_domainPrefix}{domain}" : domain;
                domain = !String.IsNullOrEmpty(_domainSuffix) && !domain.EndsWith(_domainSuffix) ? $"{domain}{_domainSuffix}" : domain;
                return domain;
            }).ToList();

            if (!_dockerHosts.ContainsKey(containerId))
            {
                _dockerHosts.Add(containerId, new Dictionary<string, DockerHosts>{{
                        networkName, new DockerHosts() {
                            IPAddress = ip,
                            Domain = domains
                        }}});
            }
            else
            {
                if (_dockerHosts[containerId].ContainsKey(networkName))
                    _dockerHosts[containerId].Remove(networkName);

                _dockerHosts[containerId].Add(networkName, new DockerHosts()
                {
                    IPAddress = ip,
                    Domain = domains
                });
            }
        }

        public void RemoveHost(string containerId, string networkName)
        {
            _logger.LogInformation(3001, $"Removing hosts (containerID={containerId.Substring(0, 12)}, networkName={networkName})");

            if (_dockerHosts.ContainsKey(containerId))
                _dockerHosts[containerId].Remove(networkName);
        }
    }
}
