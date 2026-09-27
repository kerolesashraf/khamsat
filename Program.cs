using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using System.Globalization;
using System.Net.Http;
using System.Text;

class RequestInfo
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public double MinutesAgo { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}

class SentRequest
{
    public string Url { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
}

class Settings
{
    public List<string> Keywords { get; set; } = new List<string>();
    public string Username { get; set; } = "";
    public int MaxMinutes { get; set; }
    public string WhapiToken { get; set; } = "";
    public string WhatsappTo { get; set; } = "";
    public int ScanIntervalSeconds { get; set; }
}

class Program
{
    static readonly HttpClient httpClient = new HttpClient();

    static readonly string sentRequestsFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "sent_requests.json"
    );

    // ==========================================
    // Entry point - just calls the steps in order
    // ==========================================

    static async Task Main()
    {
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("===== UNHANDLED EXCEPTION - APP CRASHED =====");
            Console.WriteLine(ex.ToString());
            Console.WriteLine("==============================================");
        }
    }

    static async Task RunAsync()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Note: Could not set Console.OutputEncoding (this is fine): {ex.Message}");
        }

        Console.WriteLine("Step: Loading configuration...");

        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        Settings settings = configuration.Get<Settings>() ?? new Settings();

        Console.WriteLine($"Done: Configuration loaded. WhapiToken set: {!string.IsNullOrEmpty(settings.WhapiToken)}, WhatsappTo set: {!string.IsNullOrEmpty(settings.WhatsappTo)}");

        Console.WriteLine("Step: Launching browser...");

        var (playwright, browser, page) = await SetupBrowser();

        Console.WriteLine("Done: Browser launched.");

        Console.WriteLine("Step: Loading requests page (first time)...");

        await GoToRequestsPage(page);

        Console.WriteLine("Done: Requests page loaded.");

        Console.WriteLine("Step: Loading sent-requests file...");

        List<SentRequest> sentRequests = LoadSentRequests(sentRequestsFilePath);

        Console.WriteLine($"Done: Loaded {sentRequests.Count} previously-sent entries.");

        while (true)
        {
            Console.WriteLine("Step: Scanning for matching requests...");

            List<RequestInfo> requests = await GetRequests(
                page,
                settings.Keywords,
                settings.MaxMinutes
            );

            Console.WriteLine($"Done: Found {requests.Count} matching requests.");
            Console.WriteLine("================================");

            if (requests.Count > 0)
            {
                Console.WriteLine("Step: Processing requests (sending WhatsApp messages)...");

                await ProcessRequests(requests, settings.WhapiToken, settings.WhatsappTo, sentRequests);

                Console.WriteLine("Done: Finished processing requests.");
            }

            Console.WriteLine("Step: Cleaning up and saving sent-requests file...");

            // Remove entries older than MaxMinutes and save
            sentRequests = sentRequests
                .Where(sent => (DateTimeOffset.UtcNow - sent.PublishedAt).TotalMinutes < settings.MaxMinutes)
                .ToList();

            SaveSentRequests(sentRequestsFilePath, sentRequests);

            Console.WriteLine("Done: Sent-requests file saved.");
            Console.WriteLine();

            Console.WriteLine($"Step: Waiting {settings.ScanIntervalSeconds} seconds before the next scan...");

            await Task.Delay(TimeSpan.FromSeconds(settings.ScanIntervalSeconds));

            Console.WriteLine("Done: Wait finished.");

            Console.WriteLine("Step: Reloading requests page...");

            await GoToRequestsPage(page);

            Console.WriteLine("Done: Requests page reloaded.");
        }

        await browser.CloseAsync();
        playwright.Dispose();
    }

    // ==========================================
    // Navigate to the khamsat requests page, with
    // an explicit timeout and clear diagnostic
    // messages so a failure never hangs silently
    // ==========================================

    static async Task GoToRequestsPage(IPage page)
    {
        Console.WriteLine("Navigating to khamsat.com/community/requests...");

        try
        {
            await page.GotoAsync(
                "https://khamsat.com/community/requests",
                new PageGotoOptions { Timeout = 30000 }
            );

            Console.WriteLine("Page navigation done. Waiting for network idle...");

            await page.WaitForLoadStateAsync(
                LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 30000 }
            );

            Console.WriteLine("Page fully loaded.");
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"Timed out while loading the requests page: {ex.Message}");
        }
        catch (PlaywrightException ex)
        {
            Console.WriteLine($"Playwright error while loading the requests page: {ex.Message}");
        }
    }

    // ==========================================
    // Launch a plain (non-persistent) browser
    // using Playwright's internal Chromium, and
    // return the page we'll work on
    // ==========================================

    static async Task<(IPlaywright playwright, IBrowser browser, IPage page)> SetupBrowser()
    {
        Console.WriteLine("Creating Playwright instance...");
        var playwright = await Playwright.CreateAsync();
        Console.WriteLine("Playwright instance created.");

        Console.WriteLine("Launching Chromium (Headless=false, under Xvfb)...");
        var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled"
                },
                IgnoreDefaultArgs = new[] { "--enable-automation" }
            });
        Console.WriteLine("Chromium launched.");

        Console.WriteLine("Opening new page...");
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });
        Console.WriteLine("New page opened.");

        await page.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            Object.defineProperty(navigator, 'languages', { get: () => ['ar', 'en-US'] });
            Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3] });
        ");

        return (playwright, browser, page);
    }

    // ==========================================
    // Load the list of previously-sent requests
    // from disk. Returns an empty list if the file
    // doesn't exist yet (first run).
    // ==========================================

    static List<SentRequest> LoadSentRequests(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new List<SentRequest>();
        }

        string json = File.ReadAllText(filePath);

        var loaded = System.Text.Json.JsonSerializer.Deserialize<List<SentRequest>>(json);

        return loaded ?? new List<SentRequest>();
    }

    // ==========================================
    // Save the list of sent requests to disk,
    // overwriting the file with the full list.
    // ==========================================

    static void SaveSentRequests(string filePath, List<SentRequest> sentRequests)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(
            sentRequests,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
        );

        File.WriteAllText(filePath, json);
    }

    // ==========================================
    // Reverse a string for display only (used to
    // work around cmd.exe not rendering Arabic
    // right-to-left correctly). Never use the
    // result for anything other than printing -
    // the actual data stays as-is everywhere else.
    // ==========================================

    static string ReverseForConsole(string text)
    {
        char[] chars = text.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    // ==========================================
    // Go through each matching request and send a
    // WhatsApp message with its link, moving on to
    // the next one immediately (no waiting, no
    // opening the request page).
    // ==========================================

    static async Task ProcessRequests(
        List<RequestInfo> requests,
        string whapiToken,
        string whatsappTo,
        List<SentRequest> sentRequests)
    {
        foreach (var request in requests)
        {
            bool alreadySent = sentRequests.Any(
                sent => sent.Url == request.Url
            );

            if (alreadySent)
            {
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"Request: {ReverseForConsole(request.Name)}");
            Console.WriteLine($"Age: {request.MinutesAgo:F1} minutes");

            string messageText = $"{request.Name}\n\n{request.Url}";

            await SendWhatsAppMessage(whapiToken, whatsappTo, messageText);

            sentRequests.Add(new SentRequest
            {
                Url = request.Url,
                PublishedAt = request.PublishedAt
            });

            Console.WriteLine("Done: WhatsApp message sent. Moving to the next request.");
            Console.WriteLine("================================");
        }
    }

    // ==========================================
    // Send a WhatsApp text message via the
    // Whapi.Cloud API
    // ==========================================

    static async Task SendWhatsAppMessage(
        string token,
        string to,
        string messageText)
    {
        var payload = new
        {
            to = to,
            body = messageText,
            no_link_preview = true
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://gate.whapi.cloud/messages/text");

        request.Headers.Add("Authorization", $"Bearer {token}");

        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        try
        {
            var response = await httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"WhatsApp send failed ({(int)response.StatusCode}): {responseBody}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WhatsApp send error: {ex.Message}");
        }
    }

    // ==========================================
    // Get matching requests
    // ==========================================

    static async Task<List<RequestInfo>> GetRequests(
        IPage page,
        List<string> keywords,
        int maxMinutes)
    {
        var result = new List<RequestInfo>();

        // Request title links
        var requests = page.Locator(
            "td.details-td h3.details-head a"
        );

        int count = await requests.CountAsync();

        for (int i = 0; i < count; i++)
        {
            var request = requests.Nth(i);

            // Get request details
            var details = request.Locator(
                "xpath=ancestor::td[contains(@class,'details-td')]"
            );

            // Get request time
            var time = details.Locator(
                "span[title]"
            ).First;

            string timeText =
                await time.GetAttributeAsync("title") ?? "";

            if (string.IsNullOrEmpty(timeText))
                continue;

            // Parse request time
            DateTimeOffset requestTime =
                DateTimeOffset.ParseExact(
                    timeText,
                    "dd/MM/yyyy HH:mm:ss 'GMT'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal
                );

            // Calculate request age
            TimeSpan difference =
                DateTimeOffset.UtcNow - requestTime;

            double minutesAgo =
                difference.TotalMinutes;

            // Time condition
            if (minutesAgo < 0 ||
                minutesAgo >= maxMinutes)
            {
                continue;
            }

            // Request name
            string name =
                (await request.InnerTextAsync()).Trim();

            // Check keywords - an empty list means "match everything"
            bool hasKeyword = keywords.Count == 0 || keywords.Any(
                keyword => name.Contains(
                    keyword,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            if (!hasKeyword)
            {
                continue;
            }

            // Get request URL
            string href =
                await request.GetAttributeAsync("href") ?? "";

            if (string.IsNullOrEmpty(href))
                continue;

            // Convert relative URL to full URL
            string fullUrl = new Uri(
                new Uri("https://khamsat.com"),
                href
            ).ToString();

            // Add request to list
            result.Add(
                new RequestInfo
                {
                    Name = name,
                    Url = fullUrl,
                    MinutesAgo = minutesAgo,
                    PublishedAt = requestTime
                }
            );
        }

        return result;
    }

}