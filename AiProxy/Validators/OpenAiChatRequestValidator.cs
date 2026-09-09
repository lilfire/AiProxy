using AiProxy.Contracts;
using FluentValidation;

namespace AiProxy.Validators;

public sealed class OpenAiChatRequestValidator : AbstractValidator<OpenAiChatRequest>
{
    public OpenAiChatRequestValidator()
    {
        RuleFor(x => x.Model)
            .NotEmpty();

        RuleFor(x => x.Messages)
            .NotEmpty();

        RuleForEach(x => x.Messages)
            .ChildRules(message =>
            {
                message.RuleFor(x => x.Role).NotEmpty();
            });
    }
}
