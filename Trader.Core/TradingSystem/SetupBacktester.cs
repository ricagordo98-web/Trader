using System.Globalization;
using Trader.Core.Models;

namespace Trader.Core.TradingSystem;

public static class SimpleIndicators
{
    public static double[] Ema(IReadOnlyList<double> values, int period)
    {
        double[] result = new double[values.Count];
        double k = 2.0 / (period + 1);

        for (int i = 0; i < values.Count; i++)
        {
            result[i] = i == 0 ? values[0] : values[i] * k + result[i - 1] * (1 - k);
        }

        return result;
    }

    public static double[] Rsi(IReadOnlyList<double> closes, int period = 14)
    {
        double[] result = new double[closes.Count];
        double gain = 0;
        double loss = 0;

        for (int i = 1; i < closes.Count; i++)
        {
            double change = closes[i] - closes[i - 1];
            double up = Math.Max(change, 0);
            double down = Math.Max(-change, 0);

            if (i <= period)
            {
                gain += up / period;
                loss += down / period;
            }
            else
            {
                gain = (gain * (period - 1) + up) / period;
                loss = (loss * (period - 1) + down) / period;
            }

            result[i] = i < period ? 50 : loss == 0 ? 100 : 100 - 100 / (1 + gain / loss);
        }

        result[0] = 50;
        return result;
    }

    public static double[] Atr(IReadOnlyList<Candle> candles, int period = 14)
    {
        double[] result = new double[candles.Count];

        for (int i = 0; i < candles.Count; i++)
        {
            double range = candles[i].High - candles[i].Low;
            double trueRange = i == 0 ? range : Math.Max(range, Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close), Math.Abs(candles[i].Low - candles[i - 1].Close)));
            result[i] = i == 0 ? trueRange : (result[i - 1] * (period - 1) + trueRange) / period;
        }

        return result;
    }

    public static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n = Math.Min(a.Count, b.Count);

        if (n < 10)
        {
            return 0;
        }

        double meanA = a.Take(n).Average();
        double meanB = b.Take(n).Average();
        double cov = 0;
        double varA = 0;
        double varB = 0;

        for (int i = 0; i < n; i++)
        {
            cov += (a[i] - meanA) * (b[i] - meanB);
            varA += (a[i] - meanA) * (a[i] - meanA);
            varB += (b[i] - meanB) * (b[i] - meanB);
        }

        return varA <= 0 || varB <= 0 ? 0 : cov / Math.Sqrt(varA * varB);
    }

    public static IReadOnlyList<double> DailyReturns(IReadOnlyList<Candle> candles, IReadOnlyList<DateOnly> days)
    {
        Dictionary<DateOnly, double> closes = candles.GroupBy(c => DateOnly.FromDateTime(c.Time.UtcDateTime)).ToDictionary(g => g.Key, g => g.Last().Close);
        List<double> returns = [];

        for (int i = 1; i < days.Count; i++)
        {
            if (closes.TryGetValue(days[i], out double today) && closes.TryGetValue(days[i - 1], out double yesterday) && yesterday > 0)
            {
                returns.Add(today / yesterday - 1);
            }
            else
            {
                returns.Add(0);
            }
        }

        return returns;
    }
}

public static class SetupBacktester
{
    public const double DefaultCostR = 0.05;
    private const int Warmup = 200;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static IReadOnlyList<SetupBacktestTrade> Run(string symbol, IReadOnlyList<Candle> candles, SetupStrategy strategy, double costR = DefaultCostR)
    {
        List<SetupBacktestTrade> trades = [];

        if (strategy.Entry == SetupEntryRule.None || candles.Count < Warmup + 5)
        {
            return trades;
        }

        List<double> closes = candles.Select(c => c.Close).ToList();
        double[] ema20 = SimpleIndicators.Ema(closes, 20);
        double[] ema50 = SimpleIndicators.Ema(closes, 50);
        double[] ema200 = SimpleIndicators.Ema(closes, 200);
        double[] rsi = SimpleIndicators.Rsi(closes);
        double[] atr = SimpleIndicators.Atr(candles);
        int lookback = Math.Clamp(strategy.Lookback, 5, 200);

        for (int i = Warmup; i < candles.Count - 1; i++)
        {
            TradeDirection? signal = Signal(strategy, candles, i, ema20, ema50, ema200, rsi, lookback);

            if (signal is not { } direction || atr[i] <= 0)
            {
                continue;
            }

            int sign = direction == TradeDirection.Buy ? 1 : -1;
            double entry = candles[i + 1].Open;
            double risk = strategy.StopAtr * atr[i];

            if (risk <= 0 || entry <= 0)
            {
                continue;
            }

            double stop = entry - sign * risk;
            double target = entry + sign * strategy.TargetR * risk;
            double exit = candles[Math.Min(candles.Count - 1, i + strategy.MaxBars)].Close;
            int exitIndex = Math.Min(candles.Count - 1, i + strategy.MaxBars);

            for (int j = i + 1; j <= Math.Min(candles.Count - 1, i + strategy.MaxBars); j++)
            {
                bool stopHit = sign > 0 ? candles[j].Low <= stop : candles[j].High >= stop;
                bool targetHit = sign > 0 ? candles[j].High >= target : candles[j].Low <= target;

                if (stopHit)
                {
                    exit = stop;
                    exitIndex = j;
                    break;
                }

                if (targetHit)
                {
                    exit = target;
                    exitIndex = j;
                    break;
                }
            }

            double r = sign * (exit - entry) / risk - costR;
            trades.Add(new SetupBacktestTrade(symbol, direction, candles[i + 1].Time, entry, exit, r));
            i = exitIndex;
        }

        return trades;
    }

    public static SetupBacktestResult Summarize(IReadOnlyList<SetupBacktestTrade> trades, int symbolsTested, double costR = DefaultCostR)
    {
        PerformanceGroup result = SystemStatistics.Group("Setup", trades.Select(t => t.R));
        List<PerformanceGroup> bySymbol = trades
            .GroupBy(t => t.Symbol)
            .Select(g => SystemStatistics.Group(g.Key, g.Select(t => t.R)))
            .OrderByDescending(g => g.TotalR)
            .ToList();

        string verdict = result.Count switch
        {
            0 => "Nenhuma entrada encontrada com estas regras no histórico.",
            < 30 => $"Só {result.Count} trades no histórico: amostra pequena demais para concluir.",
            _ when result.CiLow > 0 => $"Margem positiva no histórico ({result.AverageR!.Value.ToString("+0.00;-0.00", Pt)} R por trade, já com custos), e o intervalo de confiança não inclui zero. Ainda assim, o passado não garante o futuro.",
            _ when result.CiHigh < 0 => $"Perdeu de forma consistente no histórico ({result.AverageR!.Value.ToString("+0.00;-0.00", Pt)} R por trade). Não arrisques dinheiro neste setup como está.",
            _ => $"Resultado médio {result.AverageR!.Value.ToString("+0.00;-0.00", Pt)} R por trade, mas o intervalo de confiança inclui zero: não há margem provada."
        };

        return new SetupBacktestResult(result, bySymbol, trades.OrderByDescending(t => t.EntryTime).Take(30).ToList(), symbolsTested, costR, verdict);
    }

    private static TradeDirection? Signal(SetupStrategy strategy, IReadOnlyList<Candle> c, int i, double[] ema20, double[] ema50, double[] ema200, double[] rsi, int lookback)
    {
        TradeDirection? direction = strategy.Entry switch
        {
            SetupEntryRule.TrendPullback =>
                c[i].Close > ema200[i] && ema50[i] > ema200[i] && c[i].Low <= ema20[i] && c[i].Close > ema20[i] ? TradeDirection.Buy
                : c[i].Close < ema200[i] && ema50[i] < ema200[i] && c[i].High >= ema20[i] && c[i].Close < ema20[i] ? TradeDirection.Sell
                : null,
            SetupEntryRule.Breakout =>
                c[i].Close > Enumerable.Range(i - lookback, lookback).Max(k => c[k].High) ? TradeDirection.Buy
                : c[i].Close < Enumerable.Range(i - lookback, lookback).Min(k => c[k].Low) ? TradeDirection.Sell
                : null,
            SetupEntryRule.RsiReversal =>
                rsi[i - 1] < strategy.RsiLevel && rsi[i] >= strategy.RsiLevel ? TradeDirection.Buy
                : rsi[i - 1] > 100 - strategy.RsiLevel && rsi[i] <= 100 - strategy.RsiLevel ? TradeDirection.Sell
                : null,
            SetupEntryRule.MovingAverageCross =>
                ema20[i - 1] <= ema50[i - 1] && ema20[i] > ema50[i] ? TradeDirection.Buy
                : ema20[i - 1] >= ema50[i - 1] && ema20[i] < ema50[i] ? TradeDirection.Sell
                : null,
            _ => null
        };

        return strategy.LongOnly && direction == TradeDirection.Sell ? null : direction;
    }
}

public static class TradeExcursion
{
    public static (double MaeR, double MfeR)? Compute(TradeDirection direction, double entry, double initialStop, IReadOnlyList<Candle> candles)
    {
        double risk = Math.Abs(entry - initialStop);

        if (risk <= 0 || candles.Count == 0)
        {
            return null;
        }

        int sign = direction == TradeDirection.Buy ? 1 : -1;
        double worst = candles.Min(c => sign > 0 ? c.Low : -c.High);
        double best = candles.Max(c => sign > 0 ? c.High : -c.Low);
        double mae = Math.Min(0, (worst - sign * entry) / risk);
        double mfe = Math.Max(0, (best - sign * entry) / risk);

        return (Math.Round(mae, 2), Math.Round(mfe, 2));
    }
}

public enum ManagementAction
{
    BreakEven,
    Partial,
    TimeExit
}

public static class TradeManagement
{
    public static ManagementAction? Check(TradeDirection direction, double entry, double initialStop, double price, DateTimeOffset entryTime, DateTimeOffset now, TradeRecord record)
    {
        double risk = Math.Abs(entry - initialStop);

        if (risk <= 0)
        {
            return null;
        }

        double r = (direction == TradeDirection.Buy ? price - entry : entry - price) / risk;

        if (record.PartialAtR > 0 && !record.PartialAlerted && r >= record.PartialAtR)
        {
            return ManagementAction.Partial;
        }

        if (record.BreakEvenAtR > 0 && !record.BreakEvenAlerted && r >= record.BreakEvenAtR)
        {
            return ManagementAction.BreakEven;
        }

        if (record.MaxHoldingDays > 0 && !record.TimeAlerted && now - entryTime >= TimeSpan.FromDays(record.MaxHoldingDays))
        {
            return ManagementAction.TimeExit;
        }

        return null;
    }

    public static string Message(ManagementAction action, TradeRecord record) => action switch
    {
        ManagementAction.BreakEven => $"Chegou a +{record.BreakEvenAtR.ToString("0.#", CultureInfo.GetCultureInfo("pt-PT"))}R: o teu plano manda mover o stop para a entrada (break-even). Muda-o na corretora e na Carteira.",
        ManagementAction.Partial => $"Chegou a +{record.PartialAtR.ToString("0.#", CultureInfo.GetCultureInfo("pt-PT"))}R: o teu plano manda realizar uma parte da posição.",
        _ => $"Está aberta há {record.MaxHoldingDays} dias, o máximo do teu plano. Fecha-a ou justifica na revisão."
    };
}
