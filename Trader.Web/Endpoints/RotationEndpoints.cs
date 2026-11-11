using Trader.Core.Abstractions;
using Trader.Core.Rotation;

namespace Trader.Web.Endpoints;

public static class RotationEndpoints
{
    public static IEndpointRouteBuilder MapRotationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/rotation").RequireAuthorization();

        group.MapPost("/", async (IRotationService service, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.RunAsync(cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        group.MapGet("/last", async (IRotationService service, CancellationToken cancellationToken) =>
            await service.GetLastAsync(cancellationToken) is { } result ? Results.Ok(result) : Results.NoContent());

        return app;
    }
}
