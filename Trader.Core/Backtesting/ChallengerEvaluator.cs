namespace Trader.Core.Backtesting;

public sealed record ChallengerYear(int Year, int Selected, double AverageNetR, double UniverseNetR);

public sealed record ChallengerResult(
    string Name,
    string Label,
    int Selected,
    double AverageNetR,
    double ConfidenceLow,
    double ConfidenceHigh,
    int TestYears,
    int PositiveYears,
    IReadOnlyList<ChallengerYear> Years,
    string Verdict);

public sealed record ChallengerReport(
    int Samples,
    int TestYears,
    double UniverseNetR,
    IReadOnlyList<ChallengerResult> Results,
    IReadOnlyList<string> Notes);

public static class ChallengerEvaluator
{
    public const int MinTrainYears = 3;
    public const int MinTestYears = 4;
    public const int MinYearSamples = 100;
    public const int MinSelected = 300;
    public const double OverlapFactor = 3;
    public const double YearConsistency = 0.7;
    public const double SelectionShare = 0.2;
    public const int EmbargoDays = 14;

    public const string VerdictAccepted = "accepted";
    public const string VerdictWeak = "weak";
    public const string VerdictRejected = "rejected";

    private const double ConfidenceZ = 1.96;
    private const double RidgeLambda = 50;
    private const double TargetFloor = -3;
    private const double TargetCeiling = 5;
    private const string BaselineName = "score";

    private delegate Func<double[], double> Trainer(double[][] x, double[] y, IReadOnlyList<string> features);

    private static readonly (string Name, string Label, Trainer Train)[] Challengers =
    [
        ("ridge", "Regressão linear (ridge)", TrainRidge),
        ("boosting", "Gradient boosting (árvores de profundidade 2)", TrainBoosting)
    ];

    public static ChallengerReport Evaluate(IReadOnlyList<ResearchSample> samples)
    {
        List<string> notes =
        [
            "Cada modelo é treinado só com os anos anteriores (walk-forward, com 14 dias de intervalo) e escolhe os 20% de entradas que prevê serem melhores no ano seguinte. O limiar de escolha vem do treino, nunca do teste.",
            "Só é aceite um modelo que, fora da amostra, dê R líquido positivo com o intervalo de confiança inteiro acima de zero, em 70% dos anos testados e melhor que a pontuação atual do motor. Os intervalos assumem que as entradas vizinhas se sobrepõem."
        ];

        List<string> features = samples
            .SelectMany(s => s.Features.Where(f => double.IsFinite(f.Value)).Select(f => f.Key))
            .GroupBy(k => k)
            .Where(g => g.Count() >= samples.Count / 2)
            .Select(g => g.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        List<int> years = samples.GroupBy(s => s.Time.Year).Where(g => g.Count() >= MinYearSamples).Select(g => g.Key).OrderBy(y => y).ToList();
        List<int> testYears = years.Skip(MinTrainYears).ToList();

        if (features.Count == 0 || testYears.Count < MinTestYears)
        {
            notes.Insert(0, $"Histórico insuficiente para validar modelos: são precisos {MinTrainYears + MinTestYears} anos com amostras (há {years.Count}). Corre o backtest de 10 anos.");
            return new ChallengerReport(samples.Count, testYears.Count, samples.Count == 0 ? 0 : samples.Average(s => s.NetR), [], notes);
        }

        List<ResearchSample> ordered = samples.OrderBy(s => s.Time).ToList();
        double[][] matrix = ordered.Select(s => features.Select(f => s.Features.TryGetValue(f, out double v) && double.IsFinite(v) ? v : double.NaN).ToArray()).ToArray();
        double[] target = ordered.Select(s => Math.Clamp(s.NetR, TargetFloor, TargetCeiling)).ToArray();
        int baselineIndex = features.IndexOf(BaselineName);

        List<(string Name, string Label, Dictionary<int, List<double>> PerYear)> runs = [];

        if (baselineIndex >= 0)
        {
            runs.Add((BaselineName, "Pontuação atual do motor (referência)", []));
        }

        runs.AddRange(Challengers.Select(m => (m.Name, m.Label, new Dictionary<int, List<double>>())));
        Dictionary<int, List<double>> universe = [];

        foreach (int year in testYears)
        {
            DateTimeOffset testStart = new(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset trainEnd = testStart.AddDays(-EmbargoDays);
            int[] trainIdx = Enumerable.Range(0, ordered.Count).Where(i => ordered[i].Time < trainEnd).ToArray();
            int[] testIdx = Enumerable.Range(0, ordered.Count).Where(i => ordered[i].Time.Year == year).ToArray();

            if (trainIdx.Length < MinYearSamples * MinTrainYears || testIdx.Length < MinYearSamples)
            {
                continue;
            }

            universe[year] = testIdx.Select(i => ordered[i].NetR).ToList();
            Standardiser standard = Standardiser.Fit(trainIdx.Select(i => matrix[i]).ToArray(), features.Count);
            double[][] trainX = trainIdx.Select(i => standard.Apply(matrix[i])).ToArray();
            double[] trainY = trainIdx.Select(i => target[i]).ToArray();
            double[][] testX = testIdx.Select(i => standard.Apply(matrix[i])).ToArray();

            foreach ((string name, _, Dictionary<int, List<double>> perYear) in runs)
            {
                Func<double[], double> predict = name == BaselineName
                    ? x => x[baselineIndex]
                    : Challengers.First(m => m.Name == name).Train(trainX, trainY, features);

                double threshold = Quantile(trainX.Select(predict).ToArray(), 1 - SelectionShare);
                perYear[year] = testIdx.Where((_, k) => predict(testX[k]) >= threshold).Select(i => ordered[i].NetR).ToList();
            }
        }

        List<int> tested = universe.Keys.OrderBy(y => y).ToList();
        double baselineNetR = runs.Count > 0 && runs[0].Name == BaselineName ? Mean(runs[0].PerYear.Values.SelectMany(v => v)) : double.NegativeInfinity;
        List<ChallengerResult> results = runs.Select(r => Summarise(r.Name, r.Label, r.PerYear, universe, tested, r.Name == BaselineName ? null : baselineNetR)).ToList();

        return new ChallengerReport(samples.Count, tested.Count, Mean(universe.Values.SelectMany(v => v)), results, notes);
    }

    private static ChallengerResult Summarise(string name, string label, Dictionary<int, List<double>> perYear, Dictionary<int, List<double>> universe, List<int> tested, double? baselineNetR)
    {
        List<double> all = perYear.Values.SelectMany(v => v).ToList();
        double mean = Mean(all);
        double stdDev = all.Count > 1 ? Math.Sqrt(all.Sum(r => (r - mean) * (r - mean)) / (all.Count - 1)) : 0;
        double margin = all.Count > 1 ? ConfidenceZ * stdDev / Math.Sqrt(all.Count / OverlapFactor) : 0;

        List<ChallengerYear> years = tested
            .Where(perYear.ContainsKey)
            .Select(y => new ChallengerYear(y, perYear[y].Count, Mean(perYear[y]), Mean(universe[y])))
            .ToList();
        int positive = years.Count(y => y.AverageNetR > 0);

        string verdict = VerdictRejected;

        if (baselineNetR is { } baseline)
        {
            bool consistent = years.Count >= MinTestYears && positive >= YearConsistency * years.Count;

            if (all.Count >= MinSelected && mean - margin > 0 && consistent && mean > baseline)
            {
                verdict = VerdictAccepted;
            }
            else if (mean > 0 && mean > baseline)
            {
                verdict = VerdictWeak;
            }
        }

        return new ChallengerResult(name, label, all.Count, mean, mean - margin, mean + margin, years.Count, positive, years, verdict);
    }

    private static double Mean(IEnumerable<double> values)
    {
        double sum = 0;
        int count = 0;

        foreach (double value in values)
        {
            sum += value;
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }

    private static double Quantile(double[] values, double q)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)(q * sorted.Length), 0, sorted.Length - 1)];
    }

    private sealed class Standardiser(double[] means, double[] deviations)
    {
        public static Standardiser Fit(double[][] rows, int width)
        {
            double[] means = new double[width];
            double[] deviations = new double[width];

            for (int j = 0; j < width; j++)
            {
                double[] column = rows.Select(r => r[j]).Where(double.IsFinite).ToArray();
                means[j] = column.Length == 0 ? 0 : column.Average();
                double variance = column.Length > 1 ? column.Sum(v => (v - means[j]) * (v - means[j])) / (column.Length - 1) : 0;
                deviations[j] = variance > 1e-12 ? Math.Sqrt(variance) : 1;
            }

            return new Standardiser(means, deviations);
        }

        public double[] Apply(double[] row)
        {
            double[] result = new double[row.Length];

            for (int j = 0; j < row.Length; j++)
            {
                result[j] = double.IsFinite(row[j]) ? Math.Clamp((row[j] - means[j]) / deviations[j], -5, 5) : 0;
            }

            return result;
        }
    }

    private static Func<double[], double> TrainRidge(double[][] x, double[] y, IReadOnlyList<string> features)
    {
        int p = features.Count;
        double mean = y.Average();
        double[,] a = new double[p, p + 1];

        for (int i = 0; i < x.Length; i++)
        {
            double residual = y[i] - mean;

            for (int j = 0; j < p; j++)
            {
                for (int k = 0; k < p; k++)
                {
                    a[j, k] += x[i][j] * x[i][k];
                }

                a[j, p] += x[i][j] * residual;
            }
        }

        for (int j = 0; j < p; j++)
        {
            a[j, j] += RidgeLambda;
        }

        double[] w = Solve(a, p);
        return row => mean + row.Zip(w, (v, c) => v * c).Sum();
    }

    private static double[] Solve(double[,] a, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int pivot = col;

            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(a[pivot, col]) < 1e-12)
            {
                continue;
            }

            if (pivot != col)
            {
                for (int k = 0; k <= n; k++)
                {
                    (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                }
            }

            for (int r = 0; r < n; r++)
            {
                if (r == col)
                {
                    continue;
                }

                double factor = a[r, col] / a[col, col];

                for (int k = col; k <= n; k++)
                {
                    a[r, k] -= factor * a[col, k];
                }
            }
        }

        double[] solution = new double[n];

        for (int i = 0; i < n; i++)
        {
            solution[i] = Math.Abs(a[i, i]) < 1e-12 ? 0 : a[i, n] / a[i, i];
        }

        return solution;
    }

    private const int Rounds = 80;
    private const double LearningRate = 0.05;
    private const int MaxDepth = 2;
    private const int MinLeaf = 200;
    private const int Bins = 32;

    private sealed class Node
    {
        public int Feature = -1;
        public double Threshold;
        public double Value;
        public Node? Left;
        public Node? Right;

        public double Predict(double[] row)
        {
            Node node = this;

            while (node.Feature >= 0)
            {
                node = row[node.Feature] <= node.Threshold ? node.Left! : node.Right!;
            }

            return node.Value;
        }
    }

    private static Func<double[], double> TrainBoosting(double[][] x, double[] y, IReadOnlyList<string> features)
    {
        int n = x.Length;
        int p = features.Count;
        double[][] thresholds = new double[p][];
        byte[][] bins = new byte[p][];

        for (int j = 0; j < p; j++)
        {
            double[] sorted = x.Select(r => r[j]).OrderBy(v => v).ToArray();
            thresholds[j] = Enumerable.Range(1, Bins - 1).Select(b => sorted[Math.Min(n - 1, b * n / Bins)]).Distinct().ToArray();
            bins[j] = new byte[n];

            for (int i = 0; i < n; i++)
            {
                int bin = Array.FindIndex(thresholds[j], t => x[i][j] <= t);
                bins[j][i] = (byte)(bin < 0 ? thresholds[j].Length : bin);
            }
        }

        double baseValue = y.Average();
        double[] fitted = Enumerable.Repeat(baseValue, n).ToArray();
        List<Node> trees = [];

        for (int round = 0; round < Rounds; round++)
        {
            double[] residual = new double[n];

            for (int i = 0; i < n; i++)
            {
                residual[i] = y[i] - fitted[i];
            }

            Node tree = Grow(Enumerable.Range(0, n).ToArray(), residual, bins, thresholds, 0);
            trees.Add(tree);

            for (int i = 0; i < n; i++)
            {
                fitted[i] += LearningRate * tree.Predict(x[i]);
            }
        }

        return row => baseValue + LearningRate * trees.Sum(t => t.Predict(row));
    }

    private static Node Grow(int[] index, double[] residual, byte[][] bins, double[][] thresholds, int depth)
    {
        double total = 0;

        foreach (int i in index)
        {
            total += residual[i];
        }

        Node node = new() { Value = index.Length == 0 ? 0 : total / index.Length };

        if (depth >= MaxDepth || index.Length < 2 * MinLeaf)
        {
            return node;
        }

        double bestGain = 1e-9;
        int bestFeature = -1;
        int bestBin = -1;
        double baseScore = total * total / index.Length;

        for (int j = 0; j < bins.Length; j++)
        {
            double[] sums = new double[Bins];
            int[] counts = new int[Bins];

            foreach (int i in index)
            {
                sums[bins[j][i]] += residual[i];
                counts[bins[j][i]]++;
            }

            double leftSum = 0;
            int leftCount = 0;

            for (int b = 0; b < thresholds[j].Length; b++)
            {
                leftSum += sums[b];
                leftCount += counts[b];
                int rightCount = index.Length - leftCount;

                if (leftCount < MinLeaf || rightCount < MinLeaf)
                {
                    continue;
                }

                double rightSum = total - leftSum;
                double gain = leftSum * leftSum / leftCount + rightSum * rightSum / rightCount - baseScore;

                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestFeature = j;
                    bestBin = b;
                }
            }
        }

        if (bestFeature < 0)
        {
            return node;
        }

        node.Feature = bestFeature;
        node.Threshold = thresholds[bestFeature][bestBin];
        node.Left = Grow(index.Where(i => bins[bestFeature][i] <= bestBin).ToArray(), residual, bins, thresholds, depth + 1);
        node.Right = Grow(index.Where(i => bins[bestFeature][i] > bestBin).ToArray(), residual, bins, thresholds, depth + 1);
        return node;
    }
}
