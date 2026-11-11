namespace Trader.Core.Rotation;

public static class RotationEngine
{
    public const string Benchmark = "SPY";
    public const string Bonds = "IEF";
    public const int MomentumMonths = 12;
    public const int SkipMonths = 1;
    public const int TopCount = 3;
    public const int TrendMonths = 10;
    public const double CostPercentPerSide = 0.05;
    public const double HighCostMultiplier = 5;
    public const double MinTStat = 2;
    public const double LessRiskDrawdownShare = 0.75;
    public const double LessRiskSharpeShare = 0.9;
    public const int MinMonths = 60;

    public static readonly IReadOnlyList<string> Sectors = ["XLB", "XLE", "XLF", "XLI", "XLK", "XLP", "XLU", "XLV", "XLY"];
    public static readonly IReadOnlyList<string> Global = ["SPY", "EFA", "EEM", "TLT", "IEF", "GLD"];

    public static IReadOnlyList<string> Universe { get; } = Sectors.Concat(Global).Distinct().ToList();

    private sealed record Strategy(string Key, string Name, string Description, Func<MonthlyPrices, int, Dictionary<string, double>> Weights);

    public static RotationResult? Run(IReadOnlyList<RotationInput> inputs, DateTimeOffset generatedAt)
    {
        MonthlyPrices? prices = MonthlyPrices.Build(inputs, Benchmark, generatedAt);

        if (prices is null || !prices.Has(Bonds))
        {
            return null;
        }

        int first = prices.StartIndex + MomentumMonths;

        if (prices.Count - 1 - first < MinMonths)
        {
            return null;
        }

        List<Strategy> strategies =
        [
            new("sector-momentum", "Setores: momentum 12-1, top 3", "Todos os meses compra os 3 setores dos EUA com melhor retorno dos últimos 12 meses (sem contar o último), em partes iguais.", (p, i) => TopMomentum(p, i, Sectors, absoluteFilter: false)),
            new("global-momentum", "Global: momentum 12-1 com filtro", "Os 3 melhores entre ações dos EUA, internacionais, emergentes, obrigações e ouro, mas só os que subiram nos últimos 12 meses. O resto fica em dinheiro.", (p, i) => TopMomentum(p, i, Global, absoluteFilter: true)),
            new("trend", "Tendência: SPY acima da média de 10 meses", "Fica no SPY enquanto o preço de fecho do mês está acima da média dos últimos 10 meses. Caso contrário passa para obrigações do Tesouro a 7-10 anos.", Trend)
        ];

        double[] benchmark = Returns(prices, first, _ => new Dictionary<string, double> { [Benchmark] = 1 }, CostPercentPerSide, out double[] benchmarkTurnover);
        double[] equalWeight = Returns(prices, first, _ => Sectors.ToDictionary(s => s, _ => 1.0 / Sectors.Count), CostPercentPerSide, out double[] equalTurnover);

        List<RotationStrategyResult> results =
        [
            BenchmarkResult("spy", "Comprar e manter o SPY", "Referência: S&P 500 sempre investido.", prices, first, benchmark, benchmarkTurnover, [new RotationHolding(Benchmark, 1)]),
            BenchmarkResult("equal-sectors", "Setores em partes iguais", "Referência: os 9 setores dos EUA, reequilibrados todos os meses.", prices, first, equalWeight, equalTurnover, Sectors.Select(s => new RotationHolding(s, 1.0 / Sectors.Count)).ToList())
        ];

        foreach (Strategy strategy in strategies)
        {
            double[] returns = Returns(prices, first, p => strategy.Weights(prices, p), CostPercentPerSide, out double[] turnover);
            double[] highCost = Returns(prices, first, p => strategy.Weights(prices, p), CostPercentPerSide * HighCostMultiplier, out _);
            results.Add(Build(strategy, prices, first, returns, turnover, highCost, benchmark, prices.Count - 1));
        }

        return new RotationResult(
            generatedAt,
            prices.Dates[first + 1],
            prices.Dates[^1],
            benchmark.Length,
            CostPercentPerSide,
            Universe,
            results,
            Notes());
    }

    private static IReadOnlyList<string> Notes() =>
    [
        "Os parâmetros (12 meses, saltar 1, top 3, média de 10 meses) são os habituais da literatura e foram fixados antes de ver o resultado. Não foram otimizados.",
        "Os retornos usam preços ajustados a dividendos. Os custos assumidos são 0,05% por lado e por unidade rodada (spread e comissão de um ETF líquido). A coluna 'custos ×5' mostra o resultado com custos 5 vezes mais altos.",
        "O dinheiro rende 0% (conservador). O Sharpe usa taxa sem risco 0%, por isso é um pouco mais alto do que o 'oficial', para todas as linhas por igual.",
        "O histórico começa onde todos os ETFs já existem. Cobre a crise de 2008, 2020 e 2022, mas é um só caminho histórico: o futuro pode comportar-se de outra forma.",
        "Estes ETFs ainda existem, mas são os que hoje são líquidos. Nos ETFs não há o viés de sobrevivência das ações individuais, mas continua a haver incerteza sobre o futuro.",
        "'Posição atual' é o que cada estratégia compraria agora para o mês que começa, com os dados disponíveis. Não é um conselho financeiro."
    ];

    private static RotationStrategyResult BenchmarkResult(string key, string name, string description, MonthlyPrices prices, int first, double[] returns, double[] turnover, IReadOnlyList<RotationHolding> holdings) =>
        new(key, name, description, true, Stats(returns, turnover), HalfSharpe(returns, false), HalfSharpe(returns, true), Stats(returns, turnover).Cagr, null, null, RotationVerdicts.Benchmark, Yearly(prices, first, returns), holdings);

    private static RotationStrategyResult Build(Strategy strategy, MonthlyPrices prices, int first, double[] returns, double[] turnover, double[] highCost, double[] benchmark, int lastIndex)
    {
        RotationStats stats = Stats(returns, turnover);
        RotationStats benchmarkStats = Stats(benchmark, new double[benchmark.Length]);
        double[] excess = returns.Zip(benchmark, (r, b) => r - b).ToArray();
        double t = TStat(excess);
        double firstHalf = HalfSharpe(returns, false);
        double secondHalf = HalfSharpe(returns, true);
        double benchmarkFirst = HalfSharpe(benchmark, false);
        double benchmarkSecond = HalfSharpe(benchmark, true);
        double highCostSharpe = Stats(highCost, turnover).Sharpe;

        string verdict = RotationVerdicts.NoEdge;

        if (stats.Sharpe > benchmarkStats.Sharpe && firstHalf > benchmarkFirst && secondHalf > benchmarkSecond && highCostSharpe > benchmarkStats.Sharpe && t >= MinTStat)
        {
            verdict = RotationVerdicts.Better;
        }
        else if (stats.MaxDrawdown <= benchmarkStats.MaxDrawdown * LessRiskDrawdownShare && stats.Sharpe >= benchmarkStats.Sharpe * LessRiskSharpeShare)
        {
            verdict = RotationVerdicts.LessRisk;
        }

        Dictionary<string, double> now = strategy.Weights(prices, lastIndex);

        return new RotationStrategyResult(
            strategy.Key,
            strategy.Name,
            strategy.Description,
            false,
            stats,
            firstHalf,
            secondHalf,
            Stats(highCost, turnover).Cagr,
            stats.Cagr - benchmarkStats.Cagr,
            t,
            verdict,
            Yearly(prices, first, returns),
            now.Where(w => w.Value > 0).OrderByDescending(w => w.Value).Select(w => new RotationHolding(w.Key, w.Value)).ToList());
    }

    private static Dictionary<string, double> TopMomentum(MonthlyPrices prices, int index, IReadOnlyList<string> universe, bool absoluteFilter)
    {
        List<(string Symbol, double Momentum)> ranked = universe
            .Where(prices.Has)
            .Select(s => (Symbol: s, Momentum: prices.Price(s, index - SkipMonths) / prices.Price(s, index - MomentumMonths) - 1))
            .OrderByDescending(x => x.Momentum)
            .Take(TopCount)
            .ToList();

        return ranked
            .Where(x => !absoluteFilter || x.Momentum > 0)
            .ToDictionary(x => x.Symbol, _ => 1.0 / TopCount);
    }

    private static Dictionary<string, double> Trend(MonthlyPrices prices, int index)
    {
        double average = Enumerable.Range(index - TrendMonths + 1, TrendMonths).Average(i => prices.Price(Benchmark, i));

        return new Dictionary<string, double> { [prices.Price(Benchmark, index) > average ? Benchmark : Bonds] = 1 };
    }

    private static double[] Returns(MonthlyPrices prices, int first, Func<int, Dictionary<string, double>> weights, double costPercent, out double[] turnover)
    {
        int count = prices.Count - 1 - first;
        double[] returns = new double[count];
        turnover = new double[count];
        Dictionary<string, double> previous = [];

        for (int k = 0; k < count; k++)
        {
            int i = first + k;
            Dictionary<string, double> target = weights(i);
            double traded = target.Keys.Union(previous.Keys).Sum(s => Math.Abs(target.GetValueOrDefault(s) - previous.GetValueOrDefault(s)));
            double gross = target.Sum(w => w.Value * (prices.Price(w.Key, i + 1) / prices.Price(w.Key, i) - 1));

            returns[k] = gross - traded * costPercent / 100;
            turnover[k] = traded;
            previous = target;
        }

        return returns;
    }

    public static RotationStats Stats(double[] returns, double[] turnover)
    {
        if (returns.Length == 0)
        {
            return new RotationStats(0, 0, 0, 0, 0, 0, 0);
        }

        double growth = 1;
        double peak = 1;
        double drawdown = 0;

        foreach (double r in returns)
        {
            growth *= 1 + r;
            peak = Math.Max(peak, growth);
            drawdown = Math.Max(drawdown, 1 - growth / peak);
        }

        double mean = returns.Average();
        double deviation = Deviation(returns);
        double volatility = deviation * Math.Sqrt(12);

        return new RotationStats(
            returns.Length,
            Math.Pow(growth, 12.0 / returns.Length) - 1,
            volatility,
            volatility > 1e-12 ? mean * 12 / volatility : 0,
            drawdown,
            returns.Count(r => r > 0) / (double)returns.Length,
            turnover.Length == 0 ? 0 : turnover.Average());
    }

    private static double HalfSharpe(double[] returns, bool second)
    {
        int half = returns.Length / 2;
        double[] part = second ? returns.Skip(half).ToArray() : returns.Take(half).ToArray();

        return Stats(part, []).Sharpe;
    }

    private static double Deviation(double[] values)
    {
        if (values.Length < 2)
        {
            return 0;
        }

        double mean = values.Average();

        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));
    }

    private static double TStat(double[] values)
    {
        double deviation = Deviation(values);

        return deviation > 0 ? values.Average() / (deviation / Math.Sqrt(values.Length)) : 0;
    }

    private static Dictionary<int, double> Yearly(MonthlyPrices prices, int first, double[] returns)
    {
        Dictionary<int, double> growth = [];

        for (int k = 0; k < returns.Length; k++)
        {
            int year = prices.Dates[first + k + 1].Year;
            growth[year] = growth.GetValueOrDefault(year, 1) * (1 + returns[k]);
        }

        return growth.ToDictionary(g => g.Key, g => g.Value - 1);
    }
}
