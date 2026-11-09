using Trader.Core.Abstractions;
using Trader.Core.Projection;

namespace Trader.Web.Endpoints;

public static class ProjectionEndpoints
{
    private const int MinHorizon = 15;
    private const int MaxHorizon = 480;

    public static IEndpointRouteBuilder MapProjectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projection/{symbol}", async (string symbol, int? horizon, IProjectionService service, CancellationToken cancellationToken) =>
        {
            int minutes = horizon ?? 120;

            if (minutes is < MinHorizon or > MaxHorizon || minutes % ProjectionEngine.StepMinutes != 0)
            {
                return Results.Problem(title: $"O horizonte tem de estar entre {MinHorizon} e {MaxHorizon} minutos, em múltiplos de {ProjectionEngine.StepMinutes}.", statusCode: StatusCodes.Status400BadRequest);
            }

            PriceProjection? projection = await service.ProjectAsync(symbol, minutes, cancellationToken);

            return projection is null
                ? Results.Problem(title: "Não foi possível projetar este ativo: não está na lista ou não há velas de 5 minutos suficientes (são precisas pelo menos 200).", statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(projection);
        }).RequireAuthorization();

        app.MapPost("/api/projection-records", async (RegisterProjectionRequest request, IProjectionRecordService service, CancellationToken cancellationToken) =>
        {
            (ProjectionRecordDto? record, IReadOnlyList<string> errors) = await service.RegisterAsync(request, cancellationToken);

            return record is null
                ? Results.Problem(title: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(record);
        }).RequireAuthorization();

        app.MapGet("/api/projection-records", async (IProjectionRecordService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken))).RequireAuthorization();

        app.MapDelete("/api/projection-records/{id:long}", async (long id, IProjectionRecordService service, CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound()).RequireAuthorization();

        return app;
    }
}
