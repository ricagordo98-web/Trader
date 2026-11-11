using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trader.Core.Abstractions;
using Trader.Core.Models;
using Trader.Core.Rotation;
using Trader.Infrastructure.Persistence;

namespace Trader.Infrastructure.Rotation;

public sealed class RotationService(
    TraderDbContext dbContext,
    IMarketDataService marketData,
    TimeProvider timeProvider,
    ILogger<RotationService> logger) : IRotationService
{
    private const string SettingKey = "rotation.last";
    private const string Range = "max";
    private const string Interval = "1d";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<RotationResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("Já existe um teste de rotação em execução. Aguarda que termine.");
        }

        try
        {
            List<RotationInput> inputs = [];

            foreach (string symbol in RotationEngine.Universe)
            {
                try
                {
                    IReadOnlyList<Candle> candles = await marketData.GetCandlesAsync(symbol, Interval, Range, cancellationToken);
                    inputs.Add(new RotationInput(symbol, candles));
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning("Rotation skipped {Symbol}: {Message}", symbol, ex.Message);
                }
            }

            RotationResult? result = await Task.Run(() => RotationEngine.Run(inputs, timeProvider.GetUtcNow()), cancellationToken);

            if (result is null)
            {
                throw new InvalidOperationException("Não foi possível obter histórico suficiente dos ETFs (são precisos o SPY, as obrigações IEF e pelo menos 6 anos de dados em comum).");
            }

            await SaveAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<RotationResult?> GetLastAsync(CancellationToken cancellationToken)
    {
        AppSetting? setting = await dbContext.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);

        return setting is null ? null : JsonSerializer.Deserialize<RotationResult>(setting.Value, JsonOptions);
    }

    private async Task SaveAsync(RotationResult result, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(result, JsonOptions);
        AppSetting? setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);

        if (setting is null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = SettingKey, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
