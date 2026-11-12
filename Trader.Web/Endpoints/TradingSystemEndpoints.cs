using System.Security.Claims;
using Trader.Core.Abstractions;
using Trader.Core.Assistant;
using Trader.Core.Models;
using Trader.Core.TradingSystem;

namespace Trader.Web.Endpoints;

public static class TradingSystemEndpoints
{
    public static IEndpointRouteBuilder MapTradingSystemEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder system = app.MapGroup("/api/system-trading").RequireAuthorization();

        system.MapGet("/status", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetStatusAsync(UserId(user), cancellationToken)));

        system.MapGet("/plan", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetPlanAsync(UserId(user), cancellationToken)));

        system.MapPut("/plan", async (SavePlanRequest request, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Problem(await service.SavePlanAsync(UserId(user), request, cancellationToken)));

        system.MapGet("/setups", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListSetupsAsync(UserId(user), cancellationToken)));

        system.MapPost("/setups", async (SaveSetupRequest request, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
        {
            (TradingSetupDto? setup, IReadOnlyList<string> errors) = await service.SaveSetupAsync(UserId(user), null, request, cancellationToken);
            return setup is not null ? Results.Ok(setup) : BadRequest(errors);
        });

        system.MapPut("/setups/{id:long}", async (long id, SaveSetupRequest request, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
        {
            (TradingSetupDto? setup, IReadOnlyList<string> errors) = await service.SaveSetupAsync(UserId(user), id, request, cancellationToken);
            return setup is not null ? Results.Ok(setup) : BadRequest(errors);
        });

        system.MapDelete("/setups/{id:long}", async (long id, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            await service.DeleteSetupAsync(UserId(user), id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        system.MapPost("/setups/{id:long}/backtest", async (long id, SetupBacktestRequest request, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            await service.BacktestSetupAsync(UserId(user), id, request, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound());

        system.MapPost("/check", async (PreTradeRequest request, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CheckAsync(UserId(user), request, cancellationToken)));

        system.MapPost("/trades", async (SystemTradeRequest request, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
        {
            SystemTradeResult result = await service.OpenAsync(UserId(user), request, cancellationToken);
            return result.Trade is not null ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
        });

        system.MapGet("/records", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListRecordsAsync(UserId(user), cancellationToken)));

        system.MapGet("/records/{tradeId:long}", async (long tradeId, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            await service.GetRecordAsync(UserId(user), tradeId, cancellationToken) is { } record ? Results.Ok(record) : Results.NotFound());

        system.MapGet("/records/{tradeId:long}/dossier", async (long tradeId, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            await service.GetDossierAsync(UserId(user), tradeId, cancellationToken) is { } json ? Results.Text(json, "application/json") : Results.NotFound());

        system.MapPost("/records/{tradeId:long}/review", async (long tradeId, TradeReviewRequest request, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Problem(await service.ReviewAsync(UserId(user), tradeId, request, cancellationToken)));

        system.MapGet("/reviews/pending", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.PendingReviewsAsync(UserId(user), cancellationToken)));

        system.MapGet("/reviews/week", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CurrentWeekAsync(UserId(user), cancellationToken)));

        system.MapGet("/reviews/weekly", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListWeeklyReviewsAsync(UserId(user), cancellationToken)));

        system.MapPost("/reviews/weekly", async (SaveWeeklyReviewRequest request, ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SaveWeeklyReviewAsync(UserId(user), request, cancellationToken)));

        system.MapPost("/phase/promote", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Problem(await service.PromoteAsync(UserId(user), cancellationToken)));

        system.MapGet("/performance", async (ClaimsPrincipal user, ITradingSystemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetPerformanceAsync(UserId(user), cancellationToken)));

        RouteGroupBuilder assistant = app.MapGroup("/api/assistant").RequireAuthorization();

        assistant.MapGet("/status", (IAssistantService service) => Results.Ok(service.GetStatus()));

        assistant.MapPost("/ask", async (AssistantRequest request, ClaimsPrincipal user, IAssistantService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.AskAsync(UserId(user), request, cancellationToken)));

        assistant.MapGet("/dossier/{symbol}", async (string symbol, TradeDirection? direction, ClaimsPrincipal user, ITradeDossierService service, CancellationToken cancellationToken) =>
            await service.BuildAsync(UserId(user), symbol, direction, cancellationToken) is { } dossier ? Results.Ok(dossier) : Results.NotFound());

        return app;
    }

    private static IResult Problem(IReadOnlyList<string> errors) =>
        errors.Count == 0 ? Results.NoContent() : BadRequest(errors);

    private static IResult BadRequest(IReadOnlyList<string> errors) =>
        Results.Problem(title: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);

    private static string UserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user without an identifier.");
}
