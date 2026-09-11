using System.Text;
using AiProxy.Contracts;

namespace AiProxy.Services;

/// <summary>
/// Makes resolved local images explicit to coding-agent CLIs.  The paths are passed
/// as attachment references, never as base64 embedded in a command line or prompt.
/// </summary>
public static class ImagePromptBuilder
{
    public static string Build(IEnumerable<OpenAiMessage> messages, IReadOnlyList<string> imagePaths)
    {
        var builder = new StringBuilder();
        var imageIndex = 0;

        foreach (var message in messages)
        {
            builder.Append(message.Role).Append(": ").Append(message.Content ?? string.Empty).AppendLine();

            foreach (var _ in message.Images)
            {
                if (imageIndex >= imagePaths.Count)
                    throw new InvalidOperationException("Antall løste bilder stemmer ikke med forespørselen.");

                // @-syntaksen is understood by the installed coding CLIs. The XML label
                // also gives agents that expose an image-reading tool an unambiguous path.
                var imagePath = imagePaths[imageIndex++];
                builder.Append("<image_attachment path=\"")
                    .Append(imagePath)
                    .Append("\">@")
                    .Append(imagePath)
                    .AppendLine("</image_attachment>");
            }

            builder.AppendLine();
        }

        if (imageIndex != imagePaths.Count)
            throw new InvalidOperationException("Antall løste bilder stemmer ikke med forespørselen.");

        return builder.ToString().Trim();
    }
}
