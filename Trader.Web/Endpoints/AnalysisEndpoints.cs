using Trader.Core.Abstractions;
using Trader.Core.Analysis;

namespace Trader.Web.Endpoints;

public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/analysis", async (TradeAnalysisRequest request, ITradeAnalysisService service, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<string> errors = TradeAnalysisValidator.Validate(request);

            if (errors.Count > 0)
            {
                return Results.Problem(title: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);
            }

            TradeAnalysis? analysis = await service.AnalyzeAsync(request, cancellationToken);

            return analysis is null
                ? Results.Problem(title: "Não foi possível analisar este ativo: não está na lista de ativos ou não há dados de preço.", statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(analysis);
        }).RequireAuthorization();

        return app;
    }
}
