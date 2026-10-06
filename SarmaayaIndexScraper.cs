using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ModelContextProtocol.Server;

namespace SarmayaFinancialsMcp;

public sealed class SarmaayaIndexScraper : IAsyncDisposable
{
    private static readonly Regex IndexIdPattern = new("^[A-Z0-9]{2,12}$", RegexOptions.Compiled);
    private static readonly Regex QuoteChangePattern = new(
        @"^(?<change>[-+\u2212]?\d[\d,]*(?:\.\d+)?)\s+\((?<percent>[-+\u2212]?\d[\d,]*(?:\.\d+)?%)\)$",
        RegexOptions.Compiled);
    private static readonly Regex SummaryCurrentValuePattern = new(
        @"The Current \w+ Index is (?<value>[\d,]+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(1);
    private const string DataDisclaimer =
        "For informational and educational purposes only; not investment advice. Sarmaaya disclaims guarantees regarding data accuracy, completeness, and timeliness.";

    private readonly Lazy<Task<BrowserSession>> _browserSession;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTimeOffset _lastRequestStarted = DateTimeOffset.MinValue;

    public SarmaayaIndexScraper()
    {
        _browserSession = new Lazy<Task<BrowserSession>>(StartBrowserAsync);
    }

    [McpServerToolType]
    public sealed class IndexTools
    {
        [McpServerTool(Name = "get_index_analysis")]
        [Description("Scrapes the public Sarmaaya index page for one index ID (for example KSE100 or KMI30) and returns its latest displayed quote, performance, key statistics, and constituent table.")]
        public static Task<IndexAnalysis> GetIndexAnalysis(
            SarmaayaIndexScraper scraper,
            [Description("The Sarmaaya index ID, such as KSE100, KMI30, KSE30, or ALLSHR.")] string indexId,
            CancellationToken cancellationToken = default) =>
            scraper.GetIndexAnalysisAsync(indexId, cancellationToken);
    }

    public async Task<IndexAnalysis> GetIndexAnalysisAsync(
        string indexId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);

        var normalizedIndexId = indexId.Trim().ToUpperInvariant();
        if (!IndexIdPattern.IsMatch(normalizedIndexId))
        {
            throw new ArgumentException(
                "Index ID must contain 2 to 12 letters or digits, for example KSE100.",
                nameof(indexId));
        }

        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            var waitTime = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestStarted);
            if (waitTime > TimeSpan.Zero)
            {
                await Task.Delay(waitTime, cancellationToken);
            }

            _lastRequestStarted = DateTimeOffset.UtcNow;
            var session = await _browserSession.Value;
            var page = session.Page;
            var sourceUrl = $"https://sarmaaya.pk/indexes/{Uri.EscapeDataString(normalizedIndexId)}";
            var response = await page.GotoAsync(sourceUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000,
            });

            if (response is null || !response.Ok)
            {
                var status = response is null ? "no HTTP response" : $"HTTP {(int)response.Status}";
                throw new InvalidOperationException(
                    $"Sarmaaya did not return the index page for '{normalizedIndexId}' ({status}).");
            }

            try
            {
                await page.WaitForFunctionAsync(
                    "() => { const main = document.querySelector('main'); return Boolean(main?.querySelector('h1') && (main.querySelector('table tbody tr') || main.querySelector('div.text-end'))); }",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 30_000 });
            }
            catch (TimeoutException)
            {
                var title = await page.TitleAsync();
                var mainText = await page.Locator("main").InnerTextAsync();
                var excerpt = mainText.Length > 500 ? mainText[..500] : mainText;
                throw new InvalidOperationException(
                    $"Sarmaaya did not render index data for '{normalizedIndexId}'. Page title: '{title}'. Page text: '{excerpt}'.");
            }

            var json = await page.EvaluateAsync<string>(CapturePageJson);
            var pageData = JsonSerializer.Deserialize<IndexPageCapture>(json, JsonOptions)
                ?? throw new InvalidOperationException("Sarmaaya returned an unreadable index page.");

            if (!string.Equals(pageData.IndexId, normalizedIndexId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Sarmaaya returned index '{pageData.IndexId}' when '{normalizedIndexId}' was requested.");
            }

            if (pageData.Quote.Count == 0 &&
                pageData.Statistics.Count == 0 &&
                pageData.Constituents.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Sarmaaya's page for '{normalizedIndexId}' did not contain readable public index metrics.");
            }

            var currentValue = pageData.Quote.FirstOrDefault() ?? ParseSummaryCurrentValue(pageData.Summary);
            var (change, changePercent) = ParseQuoteChange(pageData.Quote.ElementAtOrDefault(1));
            if (changePercent is null && pageData.Returns.TryGetValue("1D", out var dailyReturn))
            {
                changePercent = dailyReturn;
            }

            return new IndexAnalysis(
                pageData.IndexId,
                pageData.IndexName,
                currentValue,
                change,
                changePercent,
                pageData.Quote.ElementAtOrDefault(2),
                pageData.Summary,
                pageData.Returns,
                pageData.Statistics,
                pageData.Constituents,
                sourceUrl,
                DateTimeOffset.UtcNow,
                DataDisclaimer);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static async Task<BrowserSession> StartBrowserAsync()
    {
        var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        try
        {
            var executablePath = Environment.GetEnvironmentVariable("SARMAAYA_CHROME_EXECUTABLE");
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
            };
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                launchOptions.Channel = "chrome";
            }
            else
            {
                launchOptions.ExecutablePath = executablePath;
            }

            browser = await playwright.Chromium.LaunchAsync(launchOptions);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "en-PK",
            });
            var page = await context.NewPageAsync();
            return new BrowserSession(playwright, browser, context, page);
        }
        catch
        {
            if (browser is not null)
            {
                await browser.CloseAsync();
            }

            playwright.Dispose();
            throw;
        }
    }

    private static (string? Change, string? ChangePercent) ParseQuoteChange(string? quoteChange)
    {
        if (string.IsNullOrWhiteSpace(quoteChange))
        {
            return (null, null);
        }

        var match = QuoteChangePattern.Match(quoteChange);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Sarmaaya returned an unrecognized index-change value: '{quoteChange}'.");
        }

        return (match.Groups["change"].Value, match.Groups["percent"].Value);
    }

    private static string? ParseSummaryCurrentValue(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var match = SummaryCurrentValuePattern.Match(summary);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private const string CapturePageJson = """
        () => {
          const main = document.querySelector("main");
          if (!main) throw new Error("Sarmaaya index page did not render its main content.");

          const heading = main.querySelector("h1")?.innerText
            .split("\n").map(value => value.trim()).filter(Boolean) ?? [];
          const indexId = heading[0] ?? "";
          const summary = [...main.querySelectorAll("p")]
            .map(element => element.innerText.trim())
            .find(value => value.startsWith("The Current ")) ?? "";

          const quoteContainer = [...main.querySelectorAll("div.text-end")]
            .find(element => element.querySelectorAll(":scope > p").length >= 3);
          const quote = quoteContainer
            ? [...quoteContainer.querySelectorAll(":scope > p")].map(element => element.innerText.trim())
            : [];

          const pairs = elements => [...elements].map(element => {
            const values = [...element.querySelectorAll(":scope > p")].map(p => p.innerText.trim());
            return values.length >= 2 ? [values[0], values[1]] : null;
          }).filter(Boolean);

          const returnsHeading = [...main.querySelectorAll("h2")]
            .find(element => element.innerText.trim() === "Returns");
          const returns = returnsHeading
            ? Object.fromEntries(pairs(returnsHeading.parentElement.querySelectorAll("div.grid > div")))
            : {};

          const statistics = Object.fromEntries(pairs(main.querySelectorAll("div.flex.justify-between")));
          const constituents = [...main.querySelectorAll("table tbody tr")].map(row => {
            const cells = [...row.querySelectorAll("td")].map(cell => cell.innerText.trim());
            return cells.length >= 10 ? {
              symbol: cells[0],
              points: cells[1],
              weightPercent: cells[2],
              currentPrice: cells[3],
              change: cells[4],
              changePercent: cells[5],
              high52Week: cells[6],
              low52Week: cells[7],
              volume: cells[8],
              marketCap: cells[9]
            } : null;
          }).filter(Boolean);

          return JSON.stringify({
            indexId,
            indexName: heading.slice(1).join(" "),
            summary,
            quote,
            returns,
            statistics,
            constituents
          });
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async ValueTask DisposeAsync()
    {
        if (_browserSession.IsValueCreated && _browserSession.Value.IsCompletedSuccessfully)
        {
            var session = await _browserSession.Value;
            await session.Context.CloseAsync();
            await session.Browser.CloseAsync();
            session.Playwright.Dispose();
        }

        _requestLock.Dispose();
    }

    private sealed record BrowserSession(
        IPlaywright Playwright,
        IBrowser Browser,
        IBrowserContext Context,
        IPage Page);

    private sealed record IndexPageCapture(
        string IndexId,
        string IndexName,
        string? Summary,
        List<string> Quote,
        Dictionary<string, string> Returns,
        Dictionary<string, string> Statistics,
        List<IndexConstituent> Constituents);
}

public sealed record IndexAnalysis(
    string IndexId,
    string IndexName,
    string? CurrentValue,
    string? Change,
    string? ChangePercent,
    string? MarketTime,
    string? Summary,
    IReadOnlyDictionary<string, string> Returns,
    IReadOnlyDictionary<string, string> Statistics,
    IReadOnlyList<IndexConstituent> Constituents,
    string SourceUrl,
    DateTimeOffset RetrievedAtUtc,
    string Disclaimer);

public sealed record IndexConstituent(
    string Symbol,
    string Points,
    string WeightPercent,
    string CurrentPrice,
    string Change,
    string ChangePercent,
    string High52Week,
    string Low52Week,
    string Volume,
    string MarketCap);
