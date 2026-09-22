using AiProxy.Contracts;

namespace AiProxy.Services.Tools;

/// <summary>
/// Verktøykall og verktøyresultater når modellen gjennom protokollens egne blokker. Uten denne
/// filtreringen ville de i tillegg blitt gjengitt som "tool:"- og tomme "assistant:"-linjer i
/// prompten, altså sendt dobbelt.
/// </summary>
public static class ClientToolMessageFilter
{
    public static List<OpenAiMessage> WithoutToolExchange(IEnumerable<OpenAiMessage> messages) =>
        messages.Where(IsPromptMessage).ToList();

    private static bool IsPromptMessage(OpenAiMessage message)
    {
        if (string.Equals(message.Role, OpenAiConstants.Roles.Tool, StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.IsNullOrWhiteSpace(message.Content) || message.Images.Count > 0;
    }
}
