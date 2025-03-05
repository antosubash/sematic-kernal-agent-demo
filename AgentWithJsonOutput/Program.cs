using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;

var builder = Kernel.CreateBuilder();
builder.AddOllamaChatCompletion("llama3.1:latest", new Uri("http://localhost:11434"));

var ReviewerName = "ArtDirector";
var ReviewerInstructions = """
    You are an art director who has opinions about copywriting born of a love for David Ogilvy.
    The goal is to determine if the given copy is acceptable to print.
    If so, state that it is approved.
    If not, provide insight on how to refine suggested copy without examples.
    """;

var CopyWriterName = "CopyWriter";
var CopyWriterInstructions = """
    You are a copywriter with ten years of experience and are known for brevity and a dry humor.
    The goal is to refine and decide on the single best copy as an expert in the field.
    Only provide a single proposal per response.
    Never delimit the response with quotation marks.
    You're laser focused on the goal at hand.
    Don't waste time with chit chat.
    Consider suggestions when refining an idea.
    """;

var kernel = builder.Build();

// Define the agents
ChatCompletionAgent agentReviewer = new()
{
    Instructions = ReviewerInstructions,
    Name = ReviewerName,
    Kernel = kernel,
};

ChatCompletionAgent agentWriter = new()
{
    Instructions = CopyWriterInstructions,
    Name = CopyWriterName,
    Kernel = kernel,
};

KernelFunction terminationFunction = AgentGroupChat.CreatePromptFunctionForStrategy(
    """
    Determine if the copy has been approved.  If so, respond with a single word: yes

    History:
    {{$history}}
    """,
    safeParameterNames: "history"
);

KernelFunction selectionFunction = AgentGroupChat.CreatePromptFunctionForStrategy(
    $$$"""
    Determine which participant takes the next turn in a conversation based on the the most recent participant.
    State only the name of the participant to take the next turn.
    No participant should take more than one turn in a row.

    Choose only from these participants:
    - {{{ReviewerName}}}
    - {{{CopyWriterName}}}

    Always follow these rules when selecting the next participant:
    - After {{{CopyWriterName}}}, it is {{{ReviewerName}}}'s turn.
    - After {{{ReviewerName}}}, it is {{{CopyWriterName}}}'s turn.

    History:
    {{$history}}
    """,
    safeParameterNames: "history"
);

// Limit history used for selection and termination to the most recent message.
ChatHistoryTruncationReducer strategyReducer = new(1);

// Create a chat for agent interaction.
AgentGroupChat chat = new(agentWriter, agentReviewer)
{
    ExecutionSettings = new()
    {
        // Here KernelFunctionTerminationStrategy will terminate
        // when the art-director has given their approval.
        TerminationStrategy = new KernelFunctionTerminationStrategy(
            terminationFunction,
            kernel
        )
        {
            // Only the art-director may approve.
            Agents = [agentReviewer],
            // Customer result parser to determine if the response is "yes"
            ResultParser = (result) =>
                result.GetValue<string>()?.Contains("yes", StringComparison.OrdinalIgnoreCase)
                ?? false,
            // The prompt variable name for the history argument.
            HistoryVariableName = "history",
            // Limit total number of turns
            MaximumIterations = 10,
            // Save tokens by not including the entire history in the prompt
            HistoryReducer = strategyReducer,
        },
        // Here a KernelFunctionSelectionStrategy selects agents based on a prompt function.
        SelectionStrategy = new KernelFunctionSelectionStrategy(
            selectionFunction,
            kernel
        )
        {
            // Always start with the writer agent.
            InitialAgent = agentWriter,
            // Returns the entire result value as a string.
            ResultParser = (result) => result.GetValue<string>() ?? CopyWriterName,
            // The prompt variable name for the history argument.
            HistoryVariableName = "history",
            // Save tokens by not including the entire history in the prompt
            HistoryReducer = strategyReducer,
            // Only include the agent names and not the message content
            EvaluateNameOnly = true,
        },
    },
};

// Invoke chat and display messages.
ChatMessageContent message = new(AuthorRole.User, "concept: maps made out of egg cartons. make 4 of them");
chat.AddChatMessage(message);
WriteAgentChatMessage(message);

await foreach (ChatMessageContent response in chat.InvokeAsync())
{
    WriteAgentChatMessage(response);
}

Console.WriteLine($"\n[IS COMPLETED: {chat.IsComplete}]");

void WriteAgentChatMessage(ChatMessageContent message)
{
    // Include ChatMessageContent.AuthorName in output, if present.
    string authorExpression =
        message.Role == AuthorRole.User ? string.Empty : $" - {message.AuthorName ?? "*"}";
    // Include TextContent (via ChatMessageContent.Content), if present.
    string contentExpression = string.IsNullOrWhiteSpace(message.Content)
        ? string.Empty
        : message.Content;
    bool isCode = message.Metadata?.ContainsKey("code") ?? false;
    string codeMarker = isCode ? "\n  [CODE]\n" : " ";
    Console.WriteLine($"\n# {message.Role}{authorExpression}:{codeMarker}{contentExpression}");

    // Provide visibility for inner content (that isn't TextContent).
    foreach (KernelContent item in message.Items)
    {
        if (item is AnnotationContent annotation)
        {
            Console.WriteLine(
                $"  [{item.GetType().Name}] {annotation.Quote}: File #{annotation.FileId}"
            );
        }
        else if (item is FileReferenceContent fileReference)
        {
            Console.WriteLine($"  [{item.GetType().Name}] File #{fileReference.FileId}");
        }
        else if (item is ImageContent image)
        {
            Console.WriteLine(
                $"  [{item.GetType().Name}] {image.Uri?.ToString() ?? image.DataUri ?? $"{image.Data?.Length} bytes"}"
            );
        }
        else if (item is FunctionCallContent functionCall)
        {
            Console.WriteLine($"  [{item.GetType().Name}] {functionCall.Id}");
        }
        else if (item is FunctionResultContent functionResult)
        {
            Console.WriteLine(
                $"  [{item.GetType().Name}] {functionResult.CallId} - {functionResult.Result?.ToString() ?? "*"}"
            );
        }
    }
}
