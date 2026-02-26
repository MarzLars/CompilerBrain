using System.ClientModel;
using System.ClientModel.Primitives;
using CompilerBrain;
using ConsoleAppFramework;
using Microsoft.Agents.AI;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using ZLogger;

var app = ConsoleApp.Create();

app.ConfigureDefaultConfiguration(builder =>
{
    builder.AddUserSecrets<Program>();
});

app.ConfigureServices((configuration, services) =>
{
    // memory is mutable, process-wide scope.
    services.AddSingleton<SessionMemory>();
    services.AddTransient<SolutionLoadProgress>();
    services.AddSingleton<CompilerBrainAIFunctions>();

    // make chat-client via GitHub Copilot (Claude Sonnet)
    var model = "claude-sonnet-4.5";

    services.AddSingleton<IChatClient>(serviceProvider =>
    {
        // Authenticate via GitHub OAuth device flow + Copilot token exchange
        var (copilotToken, apiBaseUrl) = GitHubCopilotAuth.GetCopilotToken();

        var credential = new ApiKeyCredential(copilotToken);
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(apiBaseUrl)
        };
        // Add required Copilot IDE headers to every request
        clientOptions.AddPolicy(new CopilotHeadersPolicy(), PipelinePosition.PerCall);
        var builder = new OpenAIClient(credential, clientOptions)
            .GetChatClient(model)
            .AsIChatClient()
            .AsBuilder();
        builder.UseFunctionInvocation(serviceProvider.GetRequiredService<ILoggerFactory>());
        return builder.Build(serviceProvider);
    });

    services.AddSingleton<CompilerBrainChatService>();
});

app.ConfigureLogging(x =>
{
    x.ClearProviders();
    x.SetMinimumLevel(LogLevel.Trace);
    x.AddZLoggerConsole();
});

app.PostConfigureServices(services =>
{
    var logger = services.GetRequiredService<ILogger<Program>>();
    ConsoleApp.Log = x => logger.ZLogInformation($"{x}");
    ConsoleApp.LogError = x => logger.ZLogError($"{x}");
});

app.Add<Commands>();

// call initialize
try
{
    await app.RunAsync(args, disposeServiceProvider: false);

    // If ConsoleAppFramework throws error/canceled, ExitCode will be set to non-zero.
    while (Environment.ExitCode == 0)
    {
        var command = Console.ReadLine();
        if (command == null) break;

        await app.RunAsync([command], disposeServiceProvider: false);
    }
}
finally
{
    (ConsoleApp.ServiceProvider as IDisposable)?.Dispose();
}
