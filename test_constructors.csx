using Microsoft.Agents.AI.Foundry;
var x = typeof(FoundryChatClient).GetConstructors();
foreach (var c in x)
    Console.WriteLine(string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)));
