using MGR.AzDO.IterationCreator;
using MGR.CommandLineParser.Extensibility.Converters;
using MGR.CommandLineParser.Hosting.Extensions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder()
        .ConfigureParser(builder => builder.AddClassBasedCommands()
        .Services.AddSingleton<IConverter, DayOfWeekConverter>())
        .Build();

var executeResult = await host.ParseCommandLineAndExecuteAsync<CreateIterationCommand>(args);

return executeResult;