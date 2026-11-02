using System.Security.Claims;
using Trader.Core.Abstractions;
using Trader.Core.Agender;

namespace Trader.Web.Endpoints;

public static class AgenderEndpoints
{
    public static IEndpointRouteBuilder MapAgenderEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/agender").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, IAgenderSync service, CancellationToken cancellationToken) =>
            Results.Ok(new AgenderView(await service.GetSettingsAsync(UserId(user), cancellationToken), service.GetStatus(UserId(user)))));

        group.MapPut("/", async (SaveAgenderSettingsRequest request, ClaimsPrincipal user, IAgenderSync service, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<string> errors = await service.SaveSettingsAsync(UserId(user), request, cancellationToken);

            return errors.Count == 0
                ? Results.NoContent()
                : Results.Problem(title: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);
        });

        group.MapDelete("/", async (ClaimsPrincipal user, IAgenderSync service, CancellationToken cancellationToken) =>
        {
            await service.RemoveAsync(UserId(user), cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/sync", async (ClaimsPrincipal user, IAgenderSync service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SyncNowAsync(UserId(user), cancellationToken)));

        return app;
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
}
