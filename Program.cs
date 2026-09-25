using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Trace;

// Load .env file if present
var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
if (!File.Exists(envPath))
    envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");

if (File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            continue;

        var idx = trimmed.IndexOf('=');
        if (idx <= 0) continue;

        var key = trimmed[..idx].Trim();
        var value = trimmed[(idx + 1)..].Trim().Trim('"');
        if (Environment.GetEnvironmentVariable(key) is null)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

// Configuration
var projectEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");
var modelName = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? Environment.GetEnvironmentVariable("FOUNDRY_MODEL")
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME is not set.");
const string agentTelemetrySource = "FoundryAgentSample.Agents";

#pragma warning disable MAAI001
// The third constructor argument enables prompt and response content in telemetry.
OpenTelemetryAgent InstrumentAgent(AIAgent agent) => new(agent, agentTelemetrySource, true);
#pragma warning restore MAAI001

// Set up OpenTelemetry tracing to export to Application Insights
var appInsightsConnStr = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
TracerProvider? tracerProvider = null;
if (!string.IsNullOrEmpty(appInsightsConnStr))
{
    tracerProvider = Sdk.CreateTracerProviderBuilder()
        .AddSource(agentTelemetrySource)
        .AddSource("Azure.AI.Projects")
        .AddSource("Microsoft.Extensions.AI")
        .AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnStr)
        .Build();
    Console.WriteLine("Tracing enabled → Application Insights");
}
else
{
    Console.WriteLine("WARNING: APPLICATIONINSIGHTS_CONNECTION_STRING not set — traces will NOT be exported.");
}

var hasManagedIdentity =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT")) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSI_ENDPOINT"));
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeManagedIdentityCredential = !hasManagedIdentity
});

// Create the Foundry project client
AIProjectClient projectClient = new(new Uri(projectEndpoint), credential);
var agentsClient = projectClient.AgentAdministrationClient;

// Retrieve spoke agents (Foundry portal-managed)
Console.WriteLine("Retrieving spoke agents...");
ProjectsAgentRecord travelAgent = await agentsClient.GetAgentAsync("Travel-agent");
ProjectsAgentRecord plannerAgent = await agentsClient.GetAgentAsync("Itinerary-planner-agent");
ProjectsAgentRecord budgetAgent = await agentsClient.GetAgentAsync("Budget-analyst-agent");
Console.WriteLine("  Found: Travel-agent, Itinerary-planner-agent, Budget-analyst-agent");

// Instrument the agents themselves so invoke_agent and chat spans carry agent metadata.
using var instrumentedTravelAgent = InstrumentAgent(projectClient.AsAIAgent(travelAgent));
using var instrumentedPlannerAgent = InstrumentAgent(projectClient.AsAIAgent(plannerAgent));
using var instrumentedBudgetAgent = InstrumentAgent(projectClient.AsAIAgent(budgetAgent));

// Helper: invoke a spoke agent and return its text response
async Task<string> InvokeSpokeAsync(AIAgent spokeAgent, string spokeName, string request)
{
    Console.WriteLine($"  [{spokeName}] invoking...");
    Console.WriteLine($"  [{spokeName}] REQUEST: {request}");
    var response = await spokeAgent.RunAsync(request);
    Console.WriteLine($"  [{spokeName}] done.");
    return response.Text ?? "(no response)";
}

// Define function tools that invoke spoke agents
var spokeTools = new List<AITool>
{
    AIFunctionFactory.Create(
        async (string request) => await InvokeSpokeAsync(instrumentedTravelAgent, "Travel-agent", request),
        "invoke_travel_research_agent",
        "Researches destinations, flights, hotels, and activities based on travel preferences."),
    AIFunctionFactory.Create(
        async (string request) => await InvokeSpokeAsync(instrumentedPlannerAgent, "Itinerary-planner-agent", request),
        "invoke_itinerary_planner_agent",
        "Builds a structured day-by-day travel itinerary from research results."),
    AIFunctionFactory.Create(
        async (string request) => await InvokeSpokeAsync(instrumentedBudgetAgent, "Budget-analyst-agent", request),
        "invoke_budget_analyst_agent",
        "Reviews a proposed itinerary and provides cost breakdown and savings recommendations.")
};

// Create orchestrator: model-based agent with function-invoking middleware
string orchestratorInstructions = "You are a Travel Planning Orchestrator. You coordinate specialist agents to fulfill travel requests.\n\nWorkflow:\n1. Call invoke_travel_research_agent with the user's travel request.\n2. Review the research results, then call invoke_itinerary_planner_agent with the research to build an itinerary.\n3. Call invoke_budget_analyst_agent with the itinerary for cost review.\n4. Synthesize a final response combining the itinerary and budget analysis.\n\nALWAYS invoke agents in this order. Present the final combined result to the user.";

ChatClientAgent orchestratorAgent = projectClient.AsAIAgent(
    modelName,
    orchestratorInstructions,
    "TravelOrchestrator",
    "Orchestrates travel planning across specialist agents",
    spokeTools,
    innerClient => new FunctionInvokingChatClient(innerClient),
    null,
    null);

using var instrumentedOrchestratorAgent = InstrumentAgent(orchestratorAgent);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(instrumentedOrchestratorAgent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();

tracerProvider?.Dispose();