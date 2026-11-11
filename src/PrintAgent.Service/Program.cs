using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PrintAgent.Service;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("NuxPrint Agent Service supports Linux only.");
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("/etc/nuxprint-agent/agent.json", optional: true, reloadOnChange: false);
var options = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
builder.Services.AddNuxPrintAgent(options);
await builder.Build().RunAsync();
