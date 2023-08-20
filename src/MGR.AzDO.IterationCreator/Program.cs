using MGR.CommandLineParser.Hosting.Extensions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder()
        .ConfigureParser(builder => builder.AddClassBasedCommands())
        .Build();

var executeResult = await host.ParseCommandLineAndExecuteAsync(args);

return executeResult;