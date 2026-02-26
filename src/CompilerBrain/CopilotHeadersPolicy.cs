using System.ClientModel.Primitives;

namespace CompilerBrain;

/// <summary>
/// Adds required Editor-Version and related headers to Copilot API requests.
/// </summary>
public class CopilotHeadersPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddHeaders(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void AddHeaders(PipelineMessage message)
    {
        message.Request.Headers.Set("Editor-Version", "vscode/1.96.0");
        message.Request.Headers.Set("Editor-Plugin-Version", "copilot-chat/0.24.0");
        message.Request.Headers.Set("Copilot-Integration-Id", "vscode-chat");
    }
}
