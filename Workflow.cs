using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;

internal static class WorkflowProgram
{
    public static async Task Main(string[] args)
    {
        LoadEnvironmentFile();

        var projectEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeManagedIdentityCredential = true
        });

        AIProjectClient projectClient = new(new Uri(projectEndpoint), credential);
        var agentsClient = projectClient.AgentAdministrationClient;

        Console.WriteLine("Retrieving Foundry agents...");
        ProjectsAgentRecord travelRecord = await agentsClient.GetAgentAsync("Travel-agent");
        ProjectsAgentRecord plannerRecord = await agentsClient.GetAgentAsync("Itinerary-planner-agent");
        ProjectsAgentRecord budgetRecord = await agentsClient.GetAgentAsync("Budget-analyst-agent");

        AIAgent travelAgent = projectClient.AsAIAgent(travelRecord);
        AIAgent plannerAgent = projectClient.AsAIAgent(plannerRecord);
        AIAgent budgetAgent = projectClient.AsAIAgent(budgetRecord);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.AddDevUI();
        builder.AddOpenAIResponses();
        builder.AddOpenAIConversations();

        builder
            .AddWorkflow(
                TravelWorkflow.Name,
                (_, name) => TravelWorkflow.Build(
                    travelAgent,
                    plannerAgent,
                    budgetAgent,
                    name))
            .AddAsAIAgent();

        WebApplication app = builder.Build();

        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();

        Console.WriteLine("\nTravel workflow DevUI: /devui");
        await app.RunAsync();
    }

    private static void LoadEnvironmentFile()
    {
        string? envPath = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env"))
        }.FirstOrDefault(File.Exists);

        if (envPath is null)
        {
            return;
        }

        Console.WriteLine($"Loading environment from {envPath}");

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

internal static class TravelWorkflow
{
    public const string Name = "TravelWorkflow";

    public static Workflow Build(
        AIAgent travelAgent,
        AIAgent plannerAgent,
        AIAgent budgetAgent,
        string name = Name)
    {
        var travelExecutor = new TravelAgentExecutor(travelAgent);
        var plannerExecutor = new PlannerAgentExecutor(plannerAgent);
        var budgetExecutor = new BudgetAgentExecutor(budgetAgent);

        return new WorkflowBuilder(travelExecutor)
            .WithName(name)
            .WithDescription("Researches a trip, builds an itinerary, and reviews its budget.")
            .AddEdge(travelExecutor, plannerExecutor)
            .AddEdge(plannerExecutor, budgetExecutor)
            .WithOutputFrom(budgetExecutor)
            .Build();
    }

    public static async Task<IReadOnlyList<ChatMessage>> RunAsync(
        AIAgent travelAgent,
        AIAgent plannerAgent,
        AIAgent budgetAgent,
        string userInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);

        Workflow workflow = Build(travelAgent, plannerAgent, budgetAgent);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, userInput)
        };

        await using StreamingRun run =
            await InProcessExecution.RunStreamingAsync(workflow, messages);

        string? currentExecutorId = null;

        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            if (evt is AgentResponseUpdateEvent update)
            {
                if (update.ExecutorId != currentExecutorId)
                {
                    currentExecutorId = update.ExecutorId;
                    Console.WriteLine($"\n\n[{currentExecutorId}]");
                }

                Console.Write(update.Update.Text);
            }
            else if (evt is WorkflowOutputEvent output)
            {
                return output.As<List<ChatMessage>>() ?? [];
            }
        }

        throw new InvalidOperationException("The travel workflow completed without producing output.");
    }
}

internal static class TravelState
{
    public const string Scope = "TravelWorkflow";
    public const string OriginalRequest = "OriginalRequest";
    public const string TravelResearch = "TravelResearch";
    public const string Itinerary = "Itinerary";
    public const string BudgetAnalysis = "BudgetAnalysis";
}

internal static class ConsoleOutput
{
    public static void WriteAgentResponse(string agentName, string? response)
    {
        WriteInColor(ConsoleColor.Blue, () =>
        {
            Console.WriteLine($"\n\n=== Agent: {agentName} ===");
            Console.WriteLine(response ?? "(no response)");
        });
    }

    private static void WriteInColor(ConsoleColor color, Action write)
    {
        ConsoleColor previousColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            write();
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }
}

internal abstract class TravelStepExecutor(
    string id,
    AIAgent agent,
    string stateKey,
    string? inputStateKey = null) : ChatProtocolExecutor(id)
{
    protected override async ValueTask TakeTurnAsync(
        List<ChatMessage> messages,
        IWorkflowContext context,
        bool? emitEvents,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ChatMessage> agentInput = messages;
        if (inputStateKey is not null)
        {
            string previousStepOutput = await context.ReadStateAsync<string>(
                inputStateKey,
                scopeName: TravelState.Scope,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Workflow state '{inputStateKey}' was not produced by the previous step.");

            agentInput = [new ChatMessage(ChatRole.User, previousStepOutput)];
        }

        AgentResponse response = await agent.RunAsync(agentInput, cancellationToken: cancellationToken);

        await context.QueueStateUpdateAsync(
            stateKey,
            response.Text,
            scopeName: TravelState.Scope,
            cancellationToken);

        var updatedMessages = new List<ChatMessage>(messages);
        updatedMessages.AddRange(response.Messages);

        ConsoleOutput.WriteAgentResponse(Id, response.Text);

        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            var update = new AgentResponseUpdate(
                ChatRole.Assistant,
                [new TextContent($"\n\n## {Id}\n\n{response.Text}")])
            {
                CreatedAt = DateTimeOffset.UtcNow,
                MessageId = Guid.NewGuid().ToString("N"),
                ResponseId = Guid.NewGuid().ToString("N"),
                Role = ChatRole.Assistant
            };

            await context.AddEventAsync(
                new AgentResponseUpdateEvent(Id, update),
                cancellationToken);
        }

        await OnCompletedAsync(updatedMessages, context, cancellationToken);
        await context.SendMessageAsync(updatedMessages, cancellationToken);
    }

    protected virtual ValueTask OnCompletedAsync(
        List<ChatMessage> messages,
        IWorkflowContext context,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class TravelAgentExecutor(AIAgent agent)
    : TravelStepExecutor("Travel-agent", agent, TravelState.TravelResearch)
{
    protected override async ValueTask OnCompletedAsync(
        List<ChatMessage> messages,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        string originalRequest = messages
            .First(message => message.Role == ChatRole.User)
            .Text;

        await context.QueueStateUpdateAsync(
            TravelState.OriginalRequest,
            originalRequest,
            scopeName: TravelState.Scope,
            cancellationToken);
    }
}

internal sealed class PlannerAgentExecutor(AIAgent agent)
    : TravelStepExecutor(
        "Itinerary-planner-agent",
        agent,
        TravelState.Itinerary,
        TravelState.TravelResearch);

internal sealed class BudgetAgentExecutor(AIAgent agent)
    : TravelStepExecutor(
        "Budget-analyst-agent",
        agent,
        TravelState.BudgetAnalysis,
        TravelState.Itinerary);
