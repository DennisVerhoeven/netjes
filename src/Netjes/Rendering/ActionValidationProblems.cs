using Microsoft.AspNetCore.Http;

namespace Netjes.Rendering;

/// <summary>
/// ASP.NET Core 10 validation (<c>AddValidation()</c>) rejects bad input before the handler runs and writes the errors
/// through <see cref="IProblemDetailsService"/>. For server actions and page actions, Netjes turns them into
/// <c>ServerAction.Invalid(errors)</c>, as for its own checks (<see cref="ActionInputValidation"/>): field errors for
/// <c>useAction</c>, or the form's page again.
/// Everything else goes to the registered writers (<c>AddProblemDetails()</c>), as without Netjes.
/// </summary>
internal sealed class ActionValidationProblems(IEnumerable<IProblemDetailsWriter> writers) : IProblemDetailsService
{
    public async ValueTask WriteAsync(ProblemDetailsContext context)
    {
        if (!await TryWriteAsync(context))
        {
            throw new InvalidOperationException("Unable to find a registered `IProblemDetailsWriter` that can write to the given context.");
        }
    }

    public async ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
    {
        var http = context.HttpContext;
        if (context.ProblemDetails is HttpValidationProblemDetails validation
            && http.GetEndpoint()?.Metadata is { } metadata
            && (metadata.GetMetadata<ServerActionMetadata>() is not null || metadata.GetMetadata<PageActionMetadata>() is not null))
        {
            await ServerAction.Invalid(validation.Errors).ExecuteAsync(http);
            return true;
        }

        foreach (var writer in writers)
        {
            if (writer.CanWrite(context))
            {
                await writer.WriteAsync(context);
                return true;
            }
        }

        return false;
    }
}
