namespace AiProxy.Services.Providers;

public static class ClaudeStreamConstants
{
    public const string AssistantEventType = "assistant";
    public const string ResultEventType = "result";
    public const string StreamEventType = "stream_event";
    public const string ContentBlockDeltaEventType = "content_block_delta";
    public const string TextDeltaType = "text_delta";
    public const string TextBlockType = "text";
    public const string ToolUseBlockType = "tool_use";
    public const string TodoWriteToolName = "TodoWrite";

    public const string ContentProperty = "content";
    public const string DeltaProperty = "delta";
    public const string TextProperty = "text";
    public const string TypeProperty = "type";
    public const string NameProperty = "name";
    public const string InputProperty = "input";
    public const string TodosProperty = "todos";
    public const string StatusProperty = "status";
}
