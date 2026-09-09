using AiProxy.Contracts;
using FluentValidation;

namespace AiProxy.Validators;

public sealed class OpenAiResponsesRequestValidator : AbstractValidator<OpenAiResponsesRequest>
{
    public OpenAiResponsesRequestValidator()
    {
        RuleFor(x => x.Model)
            .NotEmpty();

        RuleFor(x => x.Input)
            .NotNull();
    }
}
