using System.ComponentModel.DataAnnotations;
using MGR.CommandLineParser.Command;
using Microsoft.TeamFoundation.Core.WebApi.Types;
using Microsoft.TeamFoundation.Work.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System.ComponentModel;
using MGR.CommandLineParser.Extensibility;

namespace MGR.AzDO.IterationCreator;
public class CreateIterationCommand : CommandBase
{
    [Required]
    public string Pat { get; set; } = null!;
    [Required,
     Display(ShortName = "a")]
    public Uri AccountUri { get; set; } = null!;
    [Required,
        Display(ShortName = "y")]
    public int Year { get; set; }
    [Required,
        Display(ShortName = "t")]
    public List<string> Team { get; set; } = new List<string>();
    [Required, Display(ShortName = "p")]
    public string ProjectName { get; set; } = null!;
    [Required, Display(ShortName = "i")]
    public string RootIterationPath { get; set; } = null!;
    [DefaultValue(10),
     Display(Name = "number-iteration-keep", ShortName = "k")]
    public int NumberOfIterationsToKeep { get; set; } = 10;
    [DefaultValue(DayOfWeek.Monday),
     Converter(typeof(DayOfWeekConverter)),
        Display(ShortName = "d")]
    public DayOfWeek FirstDayOfSprint { get; set; } = DayOfWeek.Monday;
    [Display(ShortName = "f")]
    public bool OnlyCreateIfFuture { get; set; }

    public CreateIterationCommand(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    protected override async Task<int> ExecuteCommandAsync()
    {
        using VssConnection vssConnection = new(AccountUri,
            new VssBasicCredential(string.Empty, Pat)
            );
        await vssConnection.ConnectAsync();
        var witClient = vssConnection.GetClient<WorkItemTrackingHttpClient>();
        var workClient = vssConnection.GetClient<WorkHttpClient>();
        Dictionary<string, List<Guid>> pastIterations = await GetPastIterationsToDelete(workClient);

        var newSprintIterations = await CreateSprintIterationsForTheYear(witClient);
        await AssignIteationsToTeams(workClient, newSprintIterations);
        await UnassignIterationsToTeams(workClient, pastIterations);
        return 0;
    }

    private async Task UnassignIterationsToTeams(WorkHttpClient workClient, Dictionary<string, List<Guid>> pastIterations)
    {
        foreach (var pastIterationsOfTeam in pastIterations)
        {
            var teamContext = new TeamContext(ProjectName, pastIterationsOfTeam.Key);
            foreach (var iteration in pastIterationsOfTeam.Value)
            {
                await workClient.DeleteTeamIterationAsync(teamContext, iteration);
            }
        }
    }

    private async Task<WorkItemClassificationNode> EnsureYearIterationExists(WorkItemTrackingHttpClient witClient, string yearAsString)
    {
        Console.WriteLine("Retrieve root iteration '{0}'", RootIterationPath);
        var rootIteration = await witClient.GetClassificationNodeAsync(ProjectName,
                    TreeStructureGroup.Iterations, RootIterationPath, 2);
        var rootIterationRelativePath = ConvertAbsolutePathToRelativePath(rootIteration.Path);
        Console.WriteLine("Retrieve iteration for the year '{0}'", yearAsString);
        var yearIteration = (rootIteration.Children ?? Enumerable.Empty<WorkItemClassificationNode>())
                .SingleOrDefault(iteration => iteration.Name == yearAsString);
        if (yearIteration == null)
        {
            Console.WriteLine("Iteration for the year '{0}' don't exists. Create it.", yearAsString);
            yearIteration = new WorkItemClassificationNode
            {
                Name = yearAsString,
                StructureType = TreeNodeStructureType.Iteration
            };
            yearIteration = await witClient.CreateOrUpdateClassificationNodeAsync(yearIteration,
                ProjectName, TreeStructureGroup.Iterations, rootIterationRelativePath);
        }

        return yearIteration;
    }
    private async Task<IEnumerable<WorkItemClassificationNode>> CreateSprintIterationsForTheYear(WorkItemTrackingHttpClient witClient)
    {
        var yearAsString = Year.ToString();
        var yearIteration = await EnsureYearIterationExists(witClient, yearAsString);
        Console.WriteLine("Retrieve sprint iterations for the year '{0}'", yearAsString);
        var sprintIterations = (yearIteration.Children ?? Enumerable.Empty<WorkItemClassificationNode>()).ToList();
        var children = sprintIterations.Select(child => child.Name).ToHashSet();
        var missingIterations = new List<(int Number, string Name)>();
        Console.WriteLine("Determine missing sprint iterations for the year '{0}'", yearAsString);
        for (int sprintNumber = 1; sprintNumber <= 26; sprintNumber++)
        {
            var iterationToCreate = $"{yearAsString}-{sprintNumber:D2}";
            if (!children.Contains(iterationToCreate))
            {
                missingIterations.Add((sprintNumber, iterationToCreate));
            }
        }
        Console.WriteLine("{0} missing sprint iterations for the year '{1}'", missingIterations.Count, yearAsString);
        var firstJanuary = new DateTime(Year, 1, 1);
        var firstStartDate = firstJanuary.AddDays((int)FirstDayOfSprint - (int)firstJanuary.DayOfWeek);
        Console.WriteLine("Create missing sprint iterations for the year '{1}'", missingIterations.Count, yearAsString);
        Console.WriteLine("Skip past sprint iteration for the year '{0}'", yearAsString);
        var createdIterations = new List<WorkItemClassificationNode>();
        foreach (var missingIteration in missingIterations)
        {
            var startDate = firstStartDate.AddDays((missingIteration.Number - 1) * 14);
            var finishDate = startDate.AddDays(13);
            if (OnlyCreateIfFuture && finishDate < DateTime.Today)
            {
                Console.WriteLine("Skip creation of iteration '{0}' as it is on the past", missingIteration.Name);
                continue;
            }
            Console.WriteLine("Create iteration '{0}'", missingIteration.Name);
            var newClassificationNode = new WorkItemClassificationNode
            {
                Name = missingIteration.Name,
                StructureType = TreeNodeStructureType.Iteration,
                Attributes = new Dictionary<string, object>()
                {
                    ["startDate"] = startDate,
                    ["finishDate"] = finishDate
                }
            };
            var iteration = await witClient.CreateOrUpdateClassificationNodeAsync(newClassificationNode, ProjectName,
                        TreeStructureGroup.Iterations, ConvertAbsolutePathToRelativePath(yearIteration.Path));
            createdIterations.Add(iteration);
        }
        return createdIterations;
    }
    private async Task AssignIteationsToTeams(WorkHttpClient workClient, IEnumerable<WorkItemClassificationNode> iterations)
    {
        Console.WriteLine("Assign the created iteration to the teams");
        foreach (var iteration in iterations)
        {
            var teamSettingsIteration = new TeamSettingsIteration { Id = iteration.Identifier };
            foreach (var team in Team)
            {
                await workClient.PostTeamIterationAsync(
                    teamSettingsIteration,
                    new TeamContext(ProjectName, team)
                );
            }
        }
    }
    private async Task<Dictionary<string, List<Guid>>> GetPastIterationsToDelete(WorkHttpClient workClient)
    {
        Console.WriteLine("Retrieve past iterations to delete");
        var rootProjectIterationRootPath = $"{ProjectName}\\{RootIterationPath}";
        var pastIterations = new Dictionary<string, List<Guid>>(
            Team.Select(t => new KeyValuePair<string, List<Guid>>(t, new()))
        );
        var today = DateTime.Today;

        foreach (var team in Team)
        {
            var teamIterations = (await workClient.GetTeamIterationsAsync(new TeamContext(ProjectName, team))).ToList();
            pastIterations[team].AddRange(
                teamIterations
                    .Where(i =>
                    i.Attributes.FinishDate != null && i.Attributes.FinishDate < today
                    && i.Path.StartsWith(rootProjectIterationRootPath))
                .OrderByDescending(i => i.Attributes.StartDate)
                .Skip(NumberOfIterationsToKeep).Select(i => i.Id));
        }

        return pastIterations;
    }

    private string ConvertAbsolutePathToRelativePath(string absolutePath)
    {
        return absolutePath.Replace($"\\{ProjectName}\\Iteration", "");
    }
}
