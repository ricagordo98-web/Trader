using Microsoft.AspNetCore.Http.Features;
using Trader.Core.Abstractions;
using Trader.Core.Learning;

namespace Trader.Web.Endpoints;

public static class LearningEndpoints
{
    public static IEndpointRouteBuilder MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/learning").RequireAuthorization();

        group.MapGet("/", async (ILearningService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapPost("/links", async (AddLearningLinkRequest request, ILearningService service, CancellationToken cancellationToken) =>
        {
            (LearningItemDto? item, IReadOnlyList<string> errors) = await service.AddLinkAsync(request, cancellationToken);

            return item is null
                ? Results.Problem(title: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(item);
        });

        group.MapPost("/pdf", async (HttpRequest request, string title, string? category, string? fileName, ILearningService service, CancellationToken cancellationToken) =>
        {
            request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = LearningRules.MaxPdfBytes;

            try
            {
                (LearningItemDto? item, IReadOnlyList<string> errors) = await service.AddPdfAsync(title, category, fileName ?? "documento.pdf", request.Body, cancellationToken);

                return item is null
                    ? Results.Problem(title: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest)
                    : Results.Ok(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadHttpRequestException)
            {
                return Results.Problem(title: "Não foi possível guardar o PDF: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        group.MapGet("/{id:long}/file", async (long id, ILearningService service, CancellationToken cancellationToken) =>
        {
            (Stream Content, string FileName)? file = await service.OpenPdfAsync(id, cancellationToken);

            return file is null
                ? Results.NotFound()
                : Results.File(file.Value.Content, "application/pdf", enableRangeProcessing: true);
        });

        group.MapDelete("/{id:long}", async (long id, ILearningService service, CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
