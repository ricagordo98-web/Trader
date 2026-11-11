namespace Trader.Core.Learning;

public sealed record LearningLesson(string Id, string Title, string Category, string Summary, IReadOnlyList<string> Paragraphs, string? AppHint = null, string? AppLink = null);

public sealed record RecommendedResource(string Title, string Url, string Category, string Description);

public static class LearningCatalog
{
    public static IReadOnlyList<LearningLesson> Lessons { get; } =
    [
        new(
            "risk-per-trade",
            "Quanto arriscar em cada trade",
            "Gestão de risco",
            "Define primeiro a perda máxima aceitável e só depois o tamanho da posição.",
            [
                "A regra mais usada é arriscar entre 0,5% e 1% do capital por trade. O risco não é o valor investido: é o que perdes se o stop for atingido.",
                "Tamanho da posição = capital arriscado ÷ (preço de entrada − preço do stop). Exemplo: conta de 10 000, risco de 1% = 100. Entrada a 50 e stop a 48 dão 2 de risco por ação, logo 50 ações.",
                "Assim uma série de 5 perdas seguidas custa cerca de 5% da conta, em vez de a destruir. Sobreviver aos maus períodos vale mais do que acertar mais."
            ],
            "A calculadora de posição da Carteira faz esta conta por ti, já com a alavancagem.",
            "/journal"),
        new(
            "risk-reward",
            "Risco/retorno e taxa de acerto",
            "Gestão de risco",
            "Não precisas de acertar mais de metade das vezes, desde que ganhes mais do que perdes.",
            [
                "O risco/retorno (R:R) compara o ganho no alvo com a perda no stop. Alvo a 2× a distância do stop é R:R 2.",
                "A taxa de acerto mínima para não perder dinheiro é 1 ÷ (1 + R:R). Com R:R 1 precisas de 50%; com R:R 2, de cerca de 33%; com R:R 3, de 25%.",
                "A expectativa por trade, em múltiplos do risco (R), é: p × R:R − (1 − p), com p a taxa de acerto. Com p = 40% e R:R 2 dá 0,4 × 2 − 0,6 = +0,2R por trade.",
                "Cuidado: um R:R alto com alvos longe demais reduz a taxa de acerto. O que conta é o par das duas coisas, medido em trades reais."
            ],
            "A página Predição → Fiabilidade mostra o R médio e a taxa de acerto reais do modelo.",
            "/prediction"),
        new(
            "stop-atr",
            "Stops e volatilidade (ATR)",
            "Gestão de risco",
            "Um stop tem de respeitar o ruído normal do preço, ou é apanhado sem razão.",
            [
                "O ATR (Average True Range) mede a amplitude média das velas, tipicamente em 14 períodos. É uma medida de volatilidade, não de direção.",
                "Um stop a cerca de 1,5× ATR abaixo da entrada (numa compra) deixa o preço respirar. Um stop de percentagem fixa é demasiado apertado num ativo volátil e demasiado largo num calmo.",
                "Stops muito apertados fazem-te sair em ruído; muito largos exigem posições pequenas. O tamanho ajusta-se ao stop, nunca o stop ao tamanho que gostavas de ter."
            ],
            "O motor de sugestões usa 1,5× ATR para o stop e 2,5× ATR para o alvo.",
            "/prediction"),
        new(
            "rsi",
            "RSI: o que mede e como não o usar",
            "Indicadores",
            "Mede a força relativa dos movimentos recentes, numa escala de 0 a 100.",
            [
                "O RSI de 14 períodos compara a média das subidas com a das descidas. Acima de 70 costuma dizer-se sobrecomprado e abaixo de 30 sobrevendido.",
                "Em tendência forte o RSI pode ficar acima de 70 durante muito tempo. Vender só porque o RSI passou 70 é um erro clássico: em tendência, 'sobrecomprado' significa força.",
                "É mais útil como contexto: RSI baixo num ativo em tendência de alta pode marcar um recuo a aproveitar; divergências (preço faz novo máximo e o RSI não) avisam de perda de força.",
                "Nenhum indicador isolado é um sinal de entrada. Combina-o com tendência, níveis de preço e risco/retorno."
            ]),
        new(
            "moving-averages",
            "Médias móveis e tendência",
            "Indicadores",
            "Suavizam o preço para mostrar a direção, ao custo de chegarem atrasadas.",
            [
                "A média móvel simples (SMA) dá o mesmo peso a todos os períodos; a exponencial (EMA) dá mais peso aos recentes e reage mais depressa.",
                "Usos comuns: preço acima da EMA 200 sugere tendência de alta de longo prazo; EMA 20 acima da EMA 50 sugere impulso de curto prazo.",
                "Os cruzamentos de médias confirmam a tendência tarde. Em mercados laterais geram muitos sinais falsos (os 'whipsaws'), por isso convém saber primeiro se há tendência."
            ]),
        new(
            "vwap",
            "VWAP: o preço médio do dia",
            "Indicadores",
            "O preço médio da sessão, ponderado pelo volume. Serve de referência intradiária.",
            [
                "A VWAP é a soma de preço × volume dividida pelo volume acumulado desde o início da sessão. Reinicia todos os dias.",
                "Muitos participantes institucionais comparam a sua execução com a VWAP, por isso o preço tende a reagir perto dela. Acima da VWAP o dia é visto como mais comprador, abaixo como mais vendedor.",
                "Um preço muito afastado da VWAP tende a regressar suavemente, mas não é garantido: em dias de notícia pode afastar-se o dia todo."
            ],
            "A página Projeção usa a reversão suave à VWAP como um dos fatores da deriva.",
            "/projection"),
        new(
            "candles",
            "Ler velas sem as sobrevalorizar",
            "Análise técnica",
            "Cada vela resume abertura, máximo, mínimo e fecho de um período.",
            [
                "O corpo mostra de onde a vela abriu até onde fechou; os pavios mostram até onde o preço foi e recuou. Pavio longo contra a direção indica rejeição desse nível.",
                "Padrões como o martelo, o engolfante ou o doji descrevem uma disputa entre compradores e vendedores. O contexto manda: um martelo num suporte conta, o mesmo martelo no meio do nada quase não diz nada.",
                "Procura confirmação (a vela seguinte a seguir a direção) e define sempre o stop do outro lado do padrão."
            ]),
        new(
            "leverage",
            "Alavancagem: amplifica tudo",
            "Gestão de risco",
            "Permite abrir posições maiores do que o capital, mas também multiplica as perdas.",
            [
                "Com alavancagem 5× uma variação de 1% no preço vale 5% da margem usada. Com 10×, uma descida de 10% apaga a margem.",
                "A alavancagem deve ser consequência do plano, não o ponto de partida: primeiro calculas o tamanho pelo risco e pelo stop, depois vês se a margem disponível chega.",
                "Em corretoras de CFDs há também custos de financiamento por noite e risco de fecho forçado (margin call). Posições alavancadas deixadas abertas dias custam mais."
            ],
            "Podes definir a alavancagem por omissão de cada classe de ativo em Configurações.",
            "/settings"),
        new(
            "news-sentiment",
            "Notícias e sentimento",
            "Contexto de mercado",
            "As notícias mexem no preço depressa; o que ainda tem valor é o contexto.",
            [
                "Em ativos líquidos o preço reage a uma notícia em segundos. Quando a lês, a primeira reação já aconteceu. O valor está em perceber se muda a tendência ou se é ruído.",
                "Resultados trimestrais e dados macro (inflação, emprego, taxas) criam saltos e alargam a volatilidade. Evita ter stops apertados em cima destes eventos.",
                "O sentimento agregado de muitas notícias é um sinal fraco e ruidoso. Funciona melhor como filtro (não comprar contra uma vaga negativa forte) do que como razão de entrada."
            ],
            "O Calendário mostra os resultados e eventos económicos que aí vêm.",
            "/calendar"),
        new(
            "journal",
            "O diário de trades",
            "Disciplina",
            "O que não se mede não se melhora: regista cada trade e a razão.",
            [
                "Anota o setup, a razão da entrada, o stop, o alvo e como te sentias. Meses depois vês quais os setups que realmente funcionam para ti e quais as emoções que te fazem perder dinheiro.",
                "Compara o resultado real com o plano: saíste antes do alvo? Moveste o stop? Esses desvios costumam custar mais do que os maus setups.",
                "Rever o diário todas as semanas é o hábito com mais retorno para a maioria dos traders."
            ],
            "A Carteira guarda as tuas trades com setup, etiquetas e emoção.",
            "/journal")
    ];

    public static IReadOnlyList<RecommendedResource> Resources { get; } =
    [
        new("Análise técnica: introdução", "https://www.investopedia.com/terms/t/technicalanalysis.asp", "Técnicas", "Visão geral clara do que é a análise técnica, os seus pressupostos e limites (inglês)."),
        new("RSI explicado", "https://www.investopedia.com/terms/r/rsi.asp", "Técnicas", "Fórmula, leitura e armadilhas do índice de força relativa (inglês)."),
        new("MACD explicado", "https://www.investopedia.com/terms/m/macd.asp", "Técnicas", "Como se calcula e interpreta o MACD e os seus cruzamentos (inglês)."),
        new("Médias móveis", "https://www.investopedia.com/terms/m/movingaverage.asp", "Técnicas", "SMA, EMA e usos típicos na identificação de tendência (inglês)."),
        new("VWAP", "https://www.investopedia.com/terms/v/vwap.asp", "Técnicas", "O preço médio ponderado pelo volume e como é usado intradiário (inglês)."),
        new("ATR", "https://www.investopedia.com/terms/a/atr.asp", "Técnicas", "Medir a volatilidade e usá-la para definir stops (inglês)."),
        new("Velas japonesas", "https://www.investopedia.com/terms/c/candlestick.asp", "Técnicas", "Anatomia da vela e os padrões mais conhecidos (inglês)."),
        new("Tamanho da posição", "https://www.investopedia.com/terms/p/positionsizing.asp", "Gestão de risco", "Como decidir quanto comprar com base no risco aceite (inglês)."),
        new("Rácio risco/retorno", "https://www.investopedia.com/terms/r/riskrewardratio.asp", "Gestão de risco", "O que é, como calcular e o que não diz (inglês)."),
        new("Ordens stop-loss", "https://www.investopedia.com/terms/s/stop-lossorder.asp", "Gestão de risco", "Tipos de stop, vantagens e riscos de execução (inglês)."),
        new("StockCharts ChartSchool", "https://chartschool.stockcharts.com/", "Cursos", "Enciclopédia gratuita e muito completa de análise técnica e indicadores (inglês)."),
        new("XTB Educação", "https://www.xtb.com/en/education", "Cursos", "Material formativo da corretora que usas, com cursos e glossário (inglês)."),
        new("Investor.gov: introdução ao investimento", "https://www.investor.gov/introduction-investing", "Cursos", "Guia independente do regulador norte-americano (SEC) para quem investe (inglês)."),
        new("r/Daytrading", "https://www.reddit.com/r/Daytrading/", "Fóruns", "Comunidade de day trading; lê com espírito crítico e ignora promessas de lucro fácil (inglês)."),
        new("r/algotrading", "https://www.reddit.com/r/algotrading/", "Fóruns", "Discussão técnica sobre sistemas, backtests e estatística de trading (inglês)."),
        new("Elite Trader", "https://www.elitetrader.com/et/", "Fóruns", "Fórum veterano de traders ativos, com secções por mercado (inglês).")
    ];
}
