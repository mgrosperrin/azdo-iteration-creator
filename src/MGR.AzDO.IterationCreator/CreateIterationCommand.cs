using System.ComponentModel.DataAnnotations;
using MGR.CommandLineParser.Command;
using Microsoft.TeamFoundation.Core.WebApi.Types;
using Microsoft.TeamFoundation.Work.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System.ComponentModel;
using System.Net;

namespace MGR.AzDO.IterationCreator;
public class CreateIterationCommand : CommandBase
{
    [Required]
    public string Pat { get; set; } = null!;
    [Required]
    public Uri AccountUri { get; set; } = null!;
    [Required]
    public int Year { get; set; }
    [Required]
    public List<string> Team { get; set; } = new List<string>();
    [Required]
    public string ProjectName { get; set; } = null!;
    [Required]
    public string RootIterationPath { get; set; } = null!;
    [DefaultValue(10),
     Display(Name = "number-iteration-keep")]
    public int NumberOfIterationsToKeep { get; set; } = 10;

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

        var newSprintIterations = CreateSprintIterationsForTheYear(witClient);
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
        var rootIteration = await witClient.GetClassificationNodeAsync(ProjectName,
                    TreeStructureGroup.Iterations, RootIterationPath, 2);
        var rootIterationRelativePath = ConvertAbsolutePathToRelativePath(rootIteration.Path);
        var yearIteration = (rootIteration.Children ?? Enumerable.Empty<WorkItemClassificationNode>())
                .SingleOrDefault(iteration => iteration.Name == yearAsString);
        if (yearIteration == null)
        {
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
    private async IAsyncEnumerable<WorkItemClassificationNode> CreateSprintIterationsForTheYear(WorkItemTrackingHttpClient witClient)
    {
        var yearAsString = Year.ToString();
        WorkItemClassificationNode? yearIteration = await EnsureYearIterationExists(witClient, yearAsString);
        var sprintIterations = (yearIteration.Children ?? Enumerable.Empty<WorkItemClassificationNode>()).ToList();
        var children = sprintIterations.Select(child => child.Name).ToHashSet();
        var missingIterations = new List<(int Number, string Name)>();
        for (int sprintNumber = 1; sprintNumber <= 26; sprintNumber++)
        {
            var iterationToCreate = $"{yearAsString}-{sprintNumber:D2}";
            if (!children.Contains(iterationToCreate))
            {
                missingIterations.Add((sprintNumber, iterationToCreate));
            }
        }
        var firstJanuary = new DateTime(Year, 1, 1);
        var firstStartDate = firstJanuary.AddDays((int)DayOfWeek.Friday - (int)firstJanuary.DayOfWeek);
        foreach (var missingIteration in missingIterations)
        {
            var startDate = firstStartDate.AddDays((missingIteration.Number - 1) * 14);
            var finishDate = startDate.AddDays(13);
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
            yield return iteration;
        }
    }
    private async Task AssignIteationsToTeams(WorkHttpClient workClient, IAsyncEnumerable<WorkItemClassificationNode> iterations)
    {
        await foreach (var iteration in iterations)
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
