using CommandLine;
using CommandLine.Text;

namespace docker_hosts_writer
{
    public class CommandLines
    {
        public class Options
        {
            [Option('e', "endpoint", Required = false, HelpText = "(Optional) Docker Engine API endpoint. Default: " +
                "\nin Windows = npipe://./pipe/docker_engine " +
                "\nin Linux = unix:///var/run/docker.sock")]
            public string Endpoints { get; set; }

            [Option('f', "hosts-file", Required = false, HelpText = "(Optional) Hosts location. Default: " +
                "\nin Windows = %windir%\\system32\\drivers\\etc\\hosts" +
                "\nin Linux = /etc/hosts")]
            public string HostsFile { get; set; }

            [Option('s', "suffix", Required = false, HelpText = "(Optional) Suffix for every domain.")]
            public string Suffix { get; set; }

            [Option('p', "prefix", Required = false, HelpText = "(Optional) Prefix for every domain. Default: .docker")]
            public string Prefix { get; set; }

            [Option('v', "verbose", Required = false, HelpText = "(Optional) Set output to verbose messages.")]
            public bool Verbose { get; set; }

            public Options()
            {
                Endpoints = (OperatingSystem.IsWindows()) ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock";
                HostsFile = (OperatingSystem.IsWindows()) ? $"{Environment.GetEnvironmentVariable("windir")}\\system32\\drivers\\etc\\hosts" : "/etc/hosts";
                Suffix = ".docker";
                Prefix = String.Empty;
            }
        }

        public string[] Args { get; set; } = [];

        public Options Parse()
        {
            var option = new Options();
            var parser = new Parser(with => with.HelpWriter = null);
            var parserResult = parser.ParseArguments<Options>(Args);
            parserResult
              .WithParsed(parsedOptions => option = parsedOptions)
              .WithNotParsed(errors =>
              {
                  //Display help and errors to the user
                  var helpText = HelpText.AutoBuild(parserResult);
                  Console.WriteLine(helpText);
              });
            return option;
        }
    }
}
