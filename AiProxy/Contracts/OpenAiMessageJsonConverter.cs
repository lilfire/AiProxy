using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

/// <summary>Godtar både den eldre strengformen og OpenAI/OpenCode sin multimodale content-array.</summary>
public sealed class OpenAiMessageJsonConverter : JsonConverter<OpenAiMessage>
{
    public override OpenAiMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var role = root.TryGetProperty("role", out var roleValue) ? roleValue.GetString() ?? string.Empty : string.Empty;
        var (text, images) = root.TryGetProperty("content", out var content)
            ? ParseContent(content)
            : (null, new List<OpenAiImageInput>());

        var toolCallId = root.TryGetProperty("tool_call_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        var toolCalls = ParseToolCalls(root);
        return new OpenAiMessage(role, text) { Images = images, ToolCallId = toolCallId, ToolCalls = toolCalls };
    }

    public override void Write(Utf8JsonWriter writer, OpenAiMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role);
        writer.WriteString("content", value.Content);
        if (!string.IsNullOrWhiteSpace(value.ToolCallId))
            writer.WriteString("tool_call_id", value.ToolCallId);
        if (value.ToolCalls != null)
        {
            writer.WritePropertyName("tool_calls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }
        writer.WriteEndObject();
    }

    internal static (string? Text, List<OpenAiImageInput> Images) ParseContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return (content.GetString(), new List<OpenAiImageInput>());

        var textParts = new List<string>();
        var images = new List<OpenAiImageInput>();
        if (content.ValueKind != JsonValueKind.Array)
            return (null, images);

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;

            var type = part.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type is "text" or "input_text" or "output_text")
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    textParts.Add(text.GetString()!);
                continue;
            }

            if (type is "image_url" or "input_image")
            {
                var url = GetImageUrl(part);
                if (!string.IsNullOrWhiteSpace(url))
                    images.Add(new OpenAiImageInput(url));
            }
        }

        return (textParts.Count == 0 ? null : string.Join("\n", textParts), images);
    }

    private static string? GetImageUrl(JsonElement part)
    {
        if (!part.TryGetProperty("image_url", out var imageUrl))
            return null;

        return imageUrl.ValueKind switch
        {
            JsonValueKind.String => imageUrl.GetString(),
            JsonValueKind.Object when imageUrl.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String => url.GetString(),
            _ => null
        };
    }

    private static List<OpenAiChatToolCall>? ParseToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            return null;

        return JsonSerializer.Deserialize<List<OpenAiChatToolCall>>(calls.GetRawText());
    }
}
