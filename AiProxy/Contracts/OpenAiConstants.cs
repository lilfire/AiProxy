namespace AiProxy.Contracts;

public static class OpenAiConstants
{
    public const string ChatCompletionObject = "chat.completion";
    public const string ChatCompletionChunkObject = "chat.completion.chunk";
    public const string ChatCompletionIdPrefix = "chatcmpl-";
    public const string ResponseIdPrefix = "resp_";
    public const string ResponseItemIdSuffix = "_0";
    public const string DefaultOwnedBy = "shell-ai";
    public const string SseDoneData = "[DONE]";
    public const string SseContentType = "text/event-stream";
    public const string SseEventPrefix = "event: ";
    public const string SseDataPrefix = "data: ";
    public const string ErrorType = "api_error";
    public const string ErrorCodeRateLimit = "rate_limit_exceeded";
    public const string ModelObject = "model";
    public const string ModelsListObject = "list";
    public const string ResponseObject = "response";
    public const string SessionIdHeader = "X-ShellAi-Session";
    public const string OpenCodeSessionIdHeader = "X-Session-Id";
    public const string OpenCodeSessionAffinityHeader = "X-Session-Affinity";

    public static class Providers
    {
        public const string Aigravity = "Aigravity";
        public const string Grok = "Grok";
        public const string Claude = "Claude";
        public const string Codex = "Codex";
        public const string M365Copilot = "M365";
    }

    public static class ModelPrefixes
    {
        public const string Grok = "grok-";
        public const string Claude = "claude-";
        public const string Codex = "codex-";
        public const string M365Copilot = "m365-";
    }

    public static class FinishReasons
    {
        public const string Stop = "stop";
        public const string Empty = "";
    }

    public static class Roles
    {
        public const string User = "user";
        public const string Assistant = "assistant";
    }

    public static class ResponseEventTypes
    {
        public const string Created = "response.created";
        public const string InProgress = "response.in_progress";
        public const string OutputItemAdded = "response.output_item.added";
        public const string ContentPartAdded = "response.content_part.added";
        public const string OutputTextDelta = "response.output_text.delta";
        public const string OutputTextDone = "response.output_text.done";
        public const string ContentPartDone = "response.content_part.done";
        public const string OutputItemDone = "response.output_item.done";
        public const string Completed = "response.completed";
        public const string FunctionCallArgumentsDelta = "response.function_call_arguments.delta";
        public const string FunctionCallArgumentsDone = "response.function_call_arguments.done";
        public const string CustomToolCallInputDelta = "response.custom_tool_call_input.delta";
        public const string CustomToolCallInputDone = "response.custom_tool_call_input.done";
    }

    public static class ResponseStatuses
    {
        public const string InProgress = "in_progress";
        public const string Completed = "completed";
    }

    public static class ResponseObjectTypes
    {
        public const string Response = "response";
        public const string Message = "message";
        public const string OutputText = "output_text";
        public const string FunctionCall = "function_call";
        public const string CustomToolCall = "custom_tool_call";
    }

    public static class ResponseInputTypes
    {
        public const string InputText = "input_text";
        public const string InputImage = "input_image";
        public const string Message = "message";
        public const string FunctionCall = "function_call";
        public const string FunctionCallOutput = "function_call_output";
        public const string CustomToolCall = "custom_tool_call";
        public const string CustomToolCallOutput = "custom_tool_call_output";
    }

    public static class ToolCalls
    {
        public const string FunctionType = "function";
        public const string CustomType = "custom";
        public const string ProxyTodoCallIdPrefix = "call_aiproxy_todo_";
        public const string ProxyTodoItemIdPrefix = "fc_aiproxy_todo_";
        public const string TodoWriteName = "todowrite";
        public const string TodoWriteSnakeName = "todo_write";
        public const string ToolChoiceNone = "none";
    }

    public static class TodoStatuses
    {
        public const string Pending = "pending";
        public const string InProgress = "in_progress";
        public const string Completed = "completed";
        public const string Cancelled = "cancelled";
    }

    public static class TodoPriorities
    {
        public const string Medium = "medium";
    }

    public static class ToolSchemaProperties
    {
        public const string Properties = "properties";
        public const string Items = "items";
        public const string Todos = "todos";
        public const string Id = "id";
        public const string Priority = "priority";
        public const string Type = "type";
        public const string Name = "name";
        public const string CallId = "call_id";
    }
}
