using System.Net;
using System.Net.Http.Headers;
// For potential dynamic loading later
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection; // Added for DI
// Added for IHttpClientFactory
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using System.Diagnostics;
using System.IO;

// Added for search providers

namespace UnsecuredAPIKeys.Bots.Scraper
{
    // Circuit Breaker for external API calls to improve durability
    internal class CircuitBreaker(int threshold = 5, TimeSpan? retryTimeout = null)
    {
        private readonly TimeSpan _retryTimeout = retryTimeout ?? TimeSpan.FromSeconds(30);
        private volatile int _failureCount;
        private long _lastFailureTimeTicks;
        private volatile bool _isOpen;

        public bool IsOpen => _isOpen && (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastFailureTimeTicks))) < _retryTimeout;

        public void RecordSuccess()
        {
            Interlocked.Exchange(ref _failureCount, 0);
            _isOpen = false;
        }

        public void RecordFailure()
        {
            Interlocked.Increment(ref _failureCount);
            Interlocked.Exchange(ref _lastFailureTimeTicks, DateTime.UtcNow.Ticks);
            if (_failureCount >= threshold)
            {
                _isOpen = true;
            }
        }
    }

    // Enhanced key similarity checker for better duplicate detection
    internal static class KeySimilarityChecker
    {
        private const double SimilarityThreshold = 0.85;

        public static bool AreSimilar(string key1, string key2)
        {
            if (string.IsNullOrEmpty(key1) || string.IsNullOrEmpty(key2))
                return false;

            if (key1.Equals(key2, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check for common prefixes and suffixes that might indicate same key with different formatting
            var similarity = CalculateJaccardSimilarity(key1, key2);
            return similarity >= SimilarityThreshold;
        }

        private static double CalculateJaccardSimilarity(string str1, string str2)
        {
            var set1 = new HashSet<string>(GetNGrams(str1, 3));
            var set2 = new HashSet<string>(GetNGrams(str2, 3));
            
            var intersection = set1.Intersect(set2).Count();
            var union = set1.Union(set2).Count();
            
            return union == 0 ? 0 : (double)intersection / union;
        }

        private static IEnumerable<string> GetNGrams(string input, int n)
        {
            for (int i = 0; i <= input.Length - n; i++)
            {
                yield return input.Substring(i, n);
            }
        }
    }

    internal static class Scraper_Program
    {
        // Adaptive configuration based on system resources for better performance
        private const int PerPage = 100; // Max allowed by GitHub
        private const int MaxRetries = 3; // Max retries for fetching file content (not proxy testing)
        private const long MaxMemoryUsageBytes = 1L * 1024 * 1024 * 1024; // 1GB max memory usage
        private static volatile int OptimalBatchSize = Math.Min(Environment.ProcessorCount * 10, 100);
        private static volatile int MaxConcurrentRequests = Math.Min(Environment.ProcessorCount * 2, 20);

        // Declare these as class-level variables with volatile for thread safety
        private static volatile int _newKeysFound = 0;
        private static volatile int _duplicateKeysFound = 0;
        private static volatile int _errorCount = 0;

        private static readonly IReadOnlyList<IApiKeyProvider> _providers = ApiProviderRegistry.ScraperProviders;
        private static readonly CircuitBreaker _circuitBreaker = new();
        private static CancellationTokenSource? _cancellationTokenSource;
        
        // Static HttpClient instance to prevent socket exhaustion
        private static readonly HttpClient _sharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
            DefaultRequestHeaders =
            {
                UserAgent = { new ProductInfoHeaderValue("DotNetScanner", "1.0") }
            }
        };

        static Scraper_Program()
        {
            // Setup graceful shutdown
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                _cancellationTokenSource?.Cancel();
                Console.WriteLine("\nGraceful shutdown initiated...");
            };
        }

        private static async Task Main()
        {
            _cancellationTokenSource = new CancellationTokenSource();

            var configuration = BuildConfiguration();
            
            // --- Setup Dependency Injection ---
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection, configuration);
            await using var serviceProvider = serviceCollection.BuildServiceProvider();

            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("Scraper_Program");

            var providerFilter = Environment.GetEnvironmentVariable("UNSECURED_APIKEYS_PROVIDER_FILTER");
            Console.WriteLine("========================================");
            Console.WriteLine(" API Key Scanner (Enhanced Version)    ");
            Console.WriteLine("========================================");
            Console.WriteLine($"System Info - CPU Cores: {Environment.ProcessorCount}, Max Concurrency: {MaxConcurrentRequests}, Batch Size: {OptimalBatchSize}");
            Console.WriteLine($"Provider filter: {(string.IsNullOrWhiteSpace(providerFilter) ? "(all providers)" : providerFilter)}");
            logger?.LogInformation("System Info - CPU Cores: {CpuCores}, Max Concurrency: {MaxConcurrency}, Batch Size: {BatchSize}",
                Environment.ProcessorCount, MaxConcurrentRequests, OptimalBatchSize);
            logger?.LogInformation("Provider filter: {ProviderFilter}", string.IsNullOrWhiteSpace(providerFilter) ? "(all providers)" : providerFilter);

            // Start memory monitoring task
            _ = Task.Run(async () => await MonitorResourcesAsync(logger));

            // Check for continuous mode setting
            bool continuousMode;
            using (var scope = serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                continuousMode = bool.Parse((await dbContext.ApplicationSettings
                    .SingleOrDefaultAsync(x => x.Key == "ScraperContinuousMode", _cancellationTokenSource.Token))?.Value ?? "false");
            }

            if (continuousMode)
            {
                logger?.LogInformation("Running in CONTINUOUS MODE - will loop every 10 minutes");
                Console.WriteLine("Running in CONTINUOUS MODE - will loop every 10 minutes");
            }
            else
            {
                logger?.LogInformation("Running in SINGLE RUN MODE - will exit after completion");
                Console.WriteLine("Running in SINGLE RUN MODE - will exit after completion");
            }

            do
            {
                try
                {
                    await RunScrapingCycle(serviceProvider, logger);
                    
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        break;
                }
                catch (OperationCanceledException)
                {
                    logger?.LogInformation("Scraping cycle cancelled by user request");
                    break;
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error during scraping cycle. Will retry next cycle if in continuous mode.");
                    Console.WriteLine($"Error during scraping cycle: {ex.Message}");
                    Interlocked.Increment(ref _errorCount);
                }

                if (continuousMode && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    logger?.LogInformation("Sleeping for 10 minutes before next scraping cycle...");
                    Console.WriteLine("Sleeping for 10 minutes before next scraping cycle...");
                    
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10), _cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    
                    // Reset counters for next cycle
                    _newKeysFound = 0;
                    _duplicateKeysFound = 0;
                    _errorCount = 0;
                    logger?.LogInformation("Starting next scraping cycle...");
                    Console.WriteLine("Starting next scraping cycle...");
                }

            } while (continuousMode && !_cancellationTokenSource.Token.IsCancellationRequested);

            logger?.LogInformation("Scraper shutdown completed");
            Console.WriteLine("Scraper shutdown completed");
        }

        private static async Task RunScrapingCycle(IServiceProvider serviceProvider, ILogger? logger)
        {

            SearchProviderToken? activeToken;
            ISearchProvider searchProvider;

            using (var scope = serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

                bool canRunScraper = bool.Parse(((await dbContext.ApplicationSettings.SingleOrDefaultAsync(x => x.Key == "AllowScraper"))!).Value);
                if (!canRunScraper)
                {
                    logger?.LogInformation("Scraper is disabled via ApplicationSettings (AllowScraper). Skipping cycle.");
                    Console.WriteLine("Scraper is disabled. Skipping cycle.");
                    return;
                }

                logger?.LogInformation("Proxy usage has been removed from this version of the scraper.");

                activeToken = await dbContext.SearchProviderTokens
                                        .Where(t => t.IsEnabled)
                                        .OrderBy(t => t.LastUsedUTC ?? DateTime.MinValue)
                                        .FirstOrDefaultAsync();

                if (activeToken != null)
                {
                    activeToken.LastUsedUTC = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                    searchProvider = GetSearchProvider(serviceProvider, activeToken.SearchProvider);
                    logger?.LogInformation("Using {ProviderName} provider with token ID {TokenId}", activeToken.SearchProvider, activeToken.Id);
                }
                else
                {
                    logger?.LogError("No enabled search provider tokens found in the database.");
                    Console.WriteLine("Error: No enabled search provider tokens found.");
                    return;
                }
            }

            SearchQuery? query;
            using (var scope = serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                query = await dbContext.SearchQueries
                    .Where(x => x.IsEnabled && x.LastSearchUTC < DateTime.UtcNow.AddHours(-2))
                    .OrderBy(x => x.LastSearchUTC)
                    .FirstOrDefaultAsync();
            }

            if (query == null)
            {
                logger?.LogWarning("No enabled queries found that are due for a search.");
                Console.WriteLine("Info: No enabled queries found that are due for a search.");
                return;
            }

            logger?.LogInformation("Starting scan for query: {QueryQuery} (ID: {QueryId})", query.Query, query.Id);
            Console.WriteLine($"Starting scan for query: {query.Query}");

            var processedCount = 0;
            var apiKeysToAdd = new List<APIKey>();

            query.LastSearchUTC = DateTime.UtcNow;
            using (var scope = serviceProvider.CreateScope())
            {
                var updateContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                updateContext.SearchQueries.Update(query);
                await updateContext.SaveChangesAsync();
            }

            Console.WriteLine($"Executing search via {searchProvider.ProviderName} provider...");
            IEnumerable<RepoReference>? searchResults = null;
            try
            {
                searchResults = await searchProvider.SearchAsync(query, activeToken);
            }
            catch (ArgumentNullException ex)
            {
                logger?.LogError(ex, "Missing required argument for search provider.");
                Console.WriteLine($"Error: Missing required argument for search provider: {ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error during search provider execution.");
                Console.WriteLine($"Error during search provider execution: {ex.Message}");
                return;
            }

            if (searchResults == null)
            {
                logger?.LogError("Search provider returned null results.");
                Console.WriteLine("Error: Search provider returned null results.");
                return;
            }

            var resultsList = searchResults.ToList();
            var queryTotalCount = resultsList.Count;
            var queryProcessedCount = 0;
            var queryNewKeysFound = 0;
            var queryDuplicateKeysFound = 0;

            logger?.LogInformation("Provider returned {QueryTotalCount} potential references for query '{QueryQuery}'.", queryTotalCount, query.Query);
            Console.WriteLine($"Provider returned {queryTotalCount} potential references.");

            var tasks = new List<Task>();
            // Enhanced parallelism based on search provider capabilities and system resources
            int maxConcurrency = searchProvider.ProviderName switch
            {
                "GitHub" => Math.Min(MaxConcurrentRequests, 15),  // GitHub can handle more concurrent requests
                "SourceGraph" => Math.Min(MaxConcurrentRequests, 12),  // SourceGraph is also robust
                "GitLab" => Math.Min(MaxConcurrentRequests, 8),   // GitLab may have stricter rate limits
                _ => Math.Min(MaxConcurrentRequests, 8)
            };
            var itemProcessingSemaphore = new SemaphoreSlim(maxConcurrency);
            logger?.LogInformation("Processing items with max concurrency of {MaxConcurrency} for {ProviderName}", maxConcurrency, searchProvider.ProviderName);

            foreach (var repoRef in resultsList)
            {
                // Check for cancellation
                if (_cancellationTokenSource?.Token.IsCancellationRequested == true)
                    break;

                await itemProcessingSemaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // Check circuit breaker before processing
                        if (_circuitBreaker.IsOpen)
                        {
                            logger?.LogWarning("Circuit breaker is open, skipping item processing");
                            return;
                        }

                        // Pass service provider for DI
                        (int newKeys, int duplicateKeys) = await ProcessItemAsync(serviceProvider, activeToken, repoRef, query, activeToken.SearchProvider, _providers, apiKeysToAdd, logger);

                        Interlocked.Increment(ref queryProcessedCount);
                        Interlocked.Increment(ref processedCount);
                        Interlocked.Add(ref queryNewKeysFound, newKeys);
                        Interlocked.Add(ref queryDuplicateKeysFound, duplicateKeys);
                        Interlocked.Add(ref _newKeysFound, newKeys);
                        Interlocked.Add(ref _duplicateKeysFound, duplicateKeys);

                        if (queryProcessedCount % 10 == 0 || queryProcessedCount == queryTotalCount) // Log more frequently
                        {
                            logger?.LogInformation(
                                "Progress: Query='{QueryQuery}' | Processed={QueryProcessedCount}/{QueryTotalCount} | New={QueryNewKeysFound} | Dup={QueryDuplicateKeysFound}", query.Query, queryProcessedCount, queryTotalCount, queryNewKeysFound, queryDuplicateKeysFound);
                            Console.WriteLine(
                                $"Progress: Query='{query.Query}' | Processed={queryProcessedCount}/{queryTotalCount} | New={queryNewKeysFound} | Dup={queryDuplicateKeysFound}");
                        }

                        _circuitBreaker.RecordSuccess();
                    }
                    catch (Exception ex)
                    {
                        _circuitBreaker.RecordFailure();
                        Interlocked.Increment(ref _errorCount);
                        logger?.LogError(ex, "Error processing item {RepoRefFilePath}.", repoRef.FileURL ?? repoRef.FilePath);
                        Console.WriteLine($"Error processing item {repoRef.FileURL ?? repoRef.FilePath}: {ex.Message}");
                    }
                    finally
                    {
                        itemProcessingSemaphore.Release();
                    }
                }));

                if (apiKeysToAdd.Count >= OptimalBatchSize)
                {
                    List<APIKey> keysToSave;
                    lock (apiKeysToAdd)
                    {
                        keysToSave = [.. apiKeysToAdd];
                        apiKeysToAdd.Clear();
                    }
                    await SaveApiKeysBatchAsync(serviceProvider, keysToSave, logger);
                }
            }

            await Task.WhenAll(tasks);

            logger?.LogInformation("Completed processing {QueryProcessedCount} items for query '{QueryQuery}'.", queryProcessedCount, query.Query);
            Console.WriteLine($"Info: Completed processing {queryProcessedCount} items for query '{query.Query}'");

            if (apiKeysToAdd.Count > 0)
            {
                List<APIKey> keysToSave;
                lock (apiKeysToAdd)
                {
                    keysToSave = [.. apiKeysToAdd];
                    apiKeysToAdd.Clear();
                }
                await SaveApiKeysBatchAsync(serviceProvider, keysToSave, logger);
            }

            Console.WriteLine("\n--- Query Summary ---");
            Console.WriteLine($"Query: {query.Query}");
            Console.WriteLine($"Total Results (API): {queryTotalCount}");
            Console.WriteLine($"Processed Items: {queryProcessedCount}");
            Console.WriteLine($"New Keys Found: {queryNewKeysFound}");
            Console.WriteLine($"Duplicate Keys: {queryDuplicateKeysFound}");
            Console.WriteLine("---------------------");
            logger?.LogInformation("Query Summary for '{QueryQuery}': Total API Results={QueryTotalCount}, Processed={QueryProcessedCount}, NewKeys={QueryNewKeysFound}, DuplicateKeys={QueryDuplicateKeysFound}", query.Query, queryTotalCount, queryProcessedCount, queryNewKeysFound, queryDuplicateKeysFound);


            Console.WriteLine("\n--- Overall Run Summary ---");
            Console.WriteLine($"Total Results Processed: {processedCount}");
            Console.WriteLine($"Total New Keys Found: {_newKeysFound}");
            Console.WriteLine($"Total Duplicate Keys: {_duplicateKeysFound}");
            Console.WriteLine("-------------------------");
            Console.WriteLine("Scan cycle finished.");
            logger?.LogInformation("Overall Run Summary: Processed={ProcessedCount}, NewKeys={NewKeysFound}, DuplicateKeys={DuplicateKeysFound}. Scan cycle finished.", processedCount, _newKeysFound, _duplicateKeysFound);
        }

        private static IConfiguration BuildConfiguration()
        {
            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
                .AddUserSecrets(typeof(Scraper_Program).Assembly, optional: true)
                .AddEnvironmentVariables()
                .Build();
        }

        private static string ResolveConnectionString(IConfiguration configuration)
        {
            var connectionString =
                configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Database connection string not found. Set ConnectionStrings:DefaultConnection via user-secrets, appsettings, or env var ConnectionStrings__DefaultConnection.");
            }

            return connectionString;
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton(configuration);
            services.AddLogging(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Information) // Default to Information
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("UnsecuredAPIKeys.Bots.Scraper.Scraper_Program", LogLevel.Debug) // More detailed for self
                    .AddConsole();
            });

            var connectionString = ResolveConnectionString(configuration);

            services.AddHttpClient();
            services.AddDbContextPool<DBContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                    npgsqlOptions.EnableRetryOnFailure(3);
                });
                options.EnableSensitiveDataLogging(false);
                options.EnableServiceProviderCaching();
            });
            services.AddTransient<GitHubSearchProvider>();
            services.AddTransient<GitLabSearchProvider>();
            services.AddTransient<SourceGraphSearchProvider>();
        }

        private static ISearchProvider GetSearchProvider(IServiceProvider serviceProvider, SearchProviderEnum providerEnum)
        {
            return providerEnum switch
            {
                SearchProviderEnum.GitHub => serviceProvider.GetRequiredService<GitHubSearchProvider>(),
                SearchProviderEnum.GitLab => serviceProvider.GetRequiredService<GitLabSearchProvider>(),
                SearchProviderEnum.SourceGraph => serviceProvider.GetRequiredService<SourceGraphSearchProvider>(),
                _ => throw new ArgumentOutOfRangeException(nameof(providerEnum), $"Unsupported search provider: {providerEnum}")
            };
        }

        private static async Task<(int newKeys, int duplicateKeys)> ProcessItemAsync(
            IServiceProvider serviceProvider,
            SearchProviderToken? token,
            RepoReference repoRef,
            SearchQuery query,
            SearchProviderEnum searchProviderEnum,
            IReadOnlyList<IApiKeyProvider> apiKeyProviders,
            List<APIKey> apiKeysToAdd,
            ILogger? logger) // Added logger
        {

            var rawUrl = repoRef.ApiContentUrl;

            if (searchProviderEnum == SearchProviderEnum.SourceGraph && !string.IsNullOrWhiteSpace(rawUrl) && rawUrl.StartsWith('/'))
            {
                rawUrl = $"https://sourcegraph.com{rawUrl}";
            }
            if (searchProviderEnum == SearchProviderEnum.SourceGraph &&
                !string.IsNullOrWhiteSpace(rawUrl) &&
                rawUrl.Contains("sourcegraph.com/") && rawUrl.Contains("/-/blob/") &&
                !rawUrl.Contains("api.github.com") &&
                !rawUrl.Contains("raw.githubusercontent.com") &&
                !rawUrl.Contains("gist.githubusercontent.com"))
            {
                int blobIndex = rawUrl.LastIndexOf("/-/blob/");
                if (blobIndex != -1)
                {
                    rawUrl = string.Concat(rawUrl.AsSpan(0, blobIndex), "/-/raw/", rawUrl.AsSpan(blobIndex + "/-/blob/".Length));
                }
            }

            bool treatAsGitHubApi = false;
            if (searchProviderEnum == SearchProviderEnum.SourceGraph && !string.IsNullOrWhiteSpace(rawUrl))
            {
                if (rawUrl.Contains("api.github.com")) treatAsGitHubApi = true;
            }

            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                logger?.LogWarning("ApiContentUrl is missing for item {RepoRefFilePath}. Skipping.", repoRef.FilePath ?? repoRef.Id.ToString());
                return (0, 0);
            }

            for (int retry = 0; retry < MaxRetries; retry++)
            {
                HttpResponseMessage? fileResponse = null;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, rawUrl);
                    request.Headers.Accept.Clear();

                    if (token != null && !string.IsNullOrWhiteSpace(token.Token))
                    {
                        if (searchProviderEnum == SearchProviderEnum.GitHub || (searchProviderEnum == SearchProviderEnum.SourceGraph && treatAsGitHubApi))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("token", token.Token);
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
                        }
                        else if (searchProviderEnum == SearchProviderEnum.GitLab)
                        {
                            request.Headers.Add("PRIVATE-TOKEN", token.Token);
                        }
                    }
                    else
                    {
                         logger?.LogWarning("Token is null or empty for provider {SearchProviderEnum} when fetching {RawUrl}. Proceeding unauthenticated.", searchProviderEnum, rawUrl);
                    }

                    fileResponse = await _sharedHttpClient.SendAsync(request);

                    if (fileResponse.StatusCode == HttpStatusCode.TooManyRequests || fileResponse.StatusCode == HttpStatusCode.Forbidden)
                    {
                        var retryAfter = fileResponse.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, retry + 2));
                        logger?.LogWarning("Rate limited ({FileResponseStatusCode}) getting file content for {RawUrl}. Waiting {RetryAfterTotalSeconds:F0}s...", fileResponse.StatusCode, rawUrl, retryAfter.TotalSeconds);
                        
                        // Log rate limit event
                        await LogRateLimitAsync(serviceProvider, searchProviderEnum, rawUrl, (int)fileResponse.StatusCode, logger);
                        
                        await Task.Delay(retryAfter, _cancellationTokenSource?.Token ?? CancellationToken.None);
                        continue;
                    }
                    else if (fileResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        logger?.LogWarning("File not found (404) at {RawUrl}. Skipping item.", rawUrl);
                        return (0, 0);
                    }

                    fileResponse.EnsureSuccessStatusCode();
                    
                    // Process content based on provider type
                    return await ProcessFileContentAsync(
                        serviceProvider,
                        fileResponse,
                        repoRef,
                        query,
                        searchProviderEnum,
                        treatAsGitHubApi,
                        rawUrl,
                        apiKeyProviders,
                        apiKeysToAdd,
                        logger);
                }
                catch (JsonException jsonEx)
                {
                    logger?.LogError(jsonEx, "Error parsing JSON response for {RawUrl}. Skipping item.", rawUrl);
                    return (0, 0);
                }
                catch (HttpRequestException httpEx)
                {
                    logger?.LogWarning(httpEx, "HTTP error fetching file content for {RawUrl} (Retry {Retry}/{I}).", rawUrl, retry + 1, MaxRetries);
                    if (retry == MaxRetries - 1) return (0, 0);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry + 1)), _cancellationTokenSource?.Token ?? CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Unexpected error fetching file content for {RawUrl} (Retry {Retry}/{I}).", rawUrl, retry + 1, MaxRetries);
                    if (retry == MaxRetries - 1) return (0, 0);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry + 1)), _cancellationTokenSource?.Token ?? CancellationToken.None);
                }
                finally
                {
                    fileResponse?.Dispose();
                }
            }
            
            logger?.LogWarning("Failed to retrieve content for file {RawUrl} after {I} retries. Skipping item.", rawUrl, MaxRetries);
            return (0, 0);
        }

        private static async Task<(int newKeys, int duplicateKeys)> ProcessFileContentAsync(
            IServiceProvider serviceProvider,
            HttpResponseMessage fileResponse,
            RepoReference repoRef,
            SearchQuery query,
            SearchProviderEnum searchProviderEnum,
            bool treatAsGitHubApi,
            string rawUrl,
            IReadOnlyList<IApiKeyProvider> apiKeyProviders,
            List<APIKey> apiKeysToAdd,
            ILogger? logger)
        {
            int newKeys = 0;
            int duplicateKeys = 0;
            var foundKeysInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var potentialNewKeys = new Dictionary<string, (ApiTypeEnum ApiType, string CodeContext, int LineNumber)>();

            // Stream processing for large files
            using var contentStream = await GetContentStreamAsync(fileResponse, searchProviderEnum, treatAsGitHubApi, rawUrl, logger);
            if (contentStream == null)
            {
                logger?.LogWarning("Could not get content stream for {RawUrl}. Skipping item.", rawUrl);
                return (0, 0);
            }

            using var reader = new StreamReader(contentStream);
            var lineNumber = 0;
            var contextBuffer = new Queue<string>(5); // Keep 5 lines for context
            
            string? line;
            while ((line = await reader.ReadLineAsync()) != null && !(_cancellationTokenSource?.Token.IsCancellationRequested ?? false))
            {
                lineNumber++;
                contextBuffer.Enqueue(line);
                if (contextBuffer.Count > 5) contextBuffer.Dequeue();

                foreach (var apiKeyProvider in apiKeyProviders)
                {
                    foreach (var pattern in apiKeyProvider.RegexPatterns)
                    {
                        try
                        {
                            foreach (Match match in Regex.Matches(line, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                            {
                                var key = match.Value;
                                if (!foundKeysInFile.Contains(key))
                                {
                                    foundKeysInFile.Add(key);
                                    var codeContext = string.Join('\n', contextBuffer);
                                    potentialNewKeys[key] = (apiKeyProvider.ApiType, codeContext, lineNumber);
                                }
                            }
                        }
                        catch (RegexMatchTimeoutException ex)
                        {
                            logger?.LogWarning(ex, "Regex timeout for pattern '{Pattern}' on line {LineNumber}. Skipping pattern.", pattern, lineNumber);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogError(ex, "Error during regex matching for pattern '{Pattern}' on line {LineNumber}.", pattern, lineNumber);
                        }
                    }
                }
            }

            if (potentialNewKeys.Count == 0) return (0, 0);

            List<string> foundKeysList = [.. potentialNewKeys.Keys];
            List<string> existingKeys;
            using (var scope = serviceProvider.CreateScope())
            {
                var checkContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                existingKeys = await checkContext.APIKeys
                    .AsNoTracking()
                    .Where(x => foundKeysList.Contains(x.ApiKey))
                    .Select(x => x.ApiKey)
                    .ToListAsync();
            }

            // Enhanced duplicate detection using similarity checking
            var similarKeys = new HashSet<string>();
            foreach (var foundKey in foundKeysList)
            {
                if (existingKeys.Contains(foundKey))
                {
                    similarKeys.Add(foundKey);
                    continue;
                }

                // Check for similar keys that might be the same with different formatting
                foreach (var existingKey in existingKeys)
                {
                    if (KeySimilarityChecker.AreSimilar(foundKey, existingKey))
                    {
                        similarKeys.Add(foundKey);
                        logger?.LogDebug("Found similar key: '{FoundKey}' matches existing '{ExistingKey}'", foundKey, existingKey);
                        break;
                    }
                }
            }

            duplicateKeys = similarKeys.Count;

            foreach (var keyValuePair in potentialNewKeys)
            {
                var key = keyValuePair.Key;
                var (apiType, codeContext, lineNum) = keyValuePair.Value;
                
                // Skip if key is duplicate or similar to existing
                if (similarKeys.Contains(key)) continue;

                var newReference = new RepoReference
                {
                    Provider = repoRef.Provider, RepoOwner = repoRef.RepoOwner, RepoName = repoRef.RepoName,
                    RepoURL = repoRef.RepoURL ?? $"https://{repoRef.Provider}.com/{repoRef.RepoOwner}/{repoRef.RepoName}", // Simplified default
                    FilePath = repoRef.FilePath, FileURL = repoRef.FileURL, Branch = repoRef.Branch, FileSHA = repoRef.FileSHA,
                    CodeContext = codeContext, LineNumber = lineNum, SearchQueryId = query.Id, FoundUTC = DateTime.UtcNow
                };
                var apiKey = new APIKey
                {
                    ApiKey = key, ApiType = apiType, SearchProvider = searchProviderEnum, Status = ApiStatusEnum.Unverified,
                    TimesDisplayed = 0, FirstFoundUTC = DateTime.UtcNow, LastFoundUTC = DateTime.UtcNow,
                    References = [newReference]
                };
                lock (apiKeysToAdd) { apiKeysToAdd.Add(apiKey); }
                newKeys++;
            }
            return (newKeys, duplicateKeys);
        }

        private static async Task SaveApiKeysBatchAsync(IServiceProvider serviceProvider, List<APIKey> apiKeys, ILogger? logger)
        {
            if (apiKeys == null || apiKeys.Count == 0) return;
            logger?.LogInformation("Attempting to save batch of {ApiKeysCount} keys...", apiKeys.Count);

            var distinctKeys = apiKeys.GroupBy(k => k.ApiKey).Select(g => g.First()).ToList();
            if (distinctKeys.Count < apiKeys.Count)
            {
                logger?.LogInformation("Reduced batch from {ApiKeysCount} to {DistinctKeysCount} unique keys for saving.", apiKeys.Count, distinctKeys.Count);
            }

            using var scope = serviceProvider.CreateScope();
            var batchContext = scope.ServiceProvider.GetRequiredService<DBContext>();
            var strategy = batchContext.Database.CreateExecutionStrategy();
            var existingKeysInDb = new List<string>();
            var keysToActuallyAdd = new List<APIKey>();

            try
            {
                var keyStringsInBatch = distinctKeys.Select(k => k.ApiKey).ToList();
                existingKeysInDb = await batchContext.APIKeys
                    .Where(k => keyStringsInBatch.Contains(k.ApiKey))
                    .Select(k => k.ApiKey)
                    .ToListAsync();
                
                keysToActuallyAdd = [.. distinctKeys.Where(k => !existingKeysInDb.Contains(k.ApiKey))];

                if (keysToActuallyAdd.Count == 0)
                {
                    logger?.LogInformation("All {DistinctKeysCount} keys in batch already exist in DB. Nothing new to save.", distinctKeys.Count);
                    return;
                }
                logger?.LogInformation("Saving {Count} new keys (filtered {I} existing keys from batch).", keysToActuallyAdd.Count, existingKeysInDb.Count);

                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await batchContext.Database.BeginTransactionAsync();
                    try
                    {
                        // Add keys in batches
                        await batchContext.APIKeys.AddRangeAsync(keysToActuallyAdd);
                        await batchContext.SaveChangesAsync();
                        await transaction.CommitAsync();
                        logger?.LogInformation("Successfully saved batch of {Count} new keys.", keysToActuallyAdd.Count);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "Error during batch save transaction. Rolling back.");
                        await transaction.RollbackAsync();
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error saving batch. Falling back to standard EF Core save.");
                // Fallback to standard EF Core save
                try
                {
                    await batchContext.APIKeys.AddRangeAsync(keysToActuallyAdd);
                    await batchContext.SaveChangesAsync();
                    logger?.LogInformation("Successfully saved {Count} keys using standard save.", keysToActuallyAdd.Count);
                }
                catch (Exception fallbackEx)
                {
                    logger?.LogError(fallbackEx, "Fallback save also failed. Attempting smaller chunks.");
                    await SaveInChunksAsync(serviceProvider, keysToActuallyAdd, logger);
                }
            }
        }

        private static async Task SaveInChunksAsync(IServiceProvider serviceProvider, List<APIKey> keysToAdd, ILogger? logger)
        {
            const int chunkSize = 50;
            int totalSaved = 0;
            
            for (int i = 0; i < keysToAdd.Count; i += chunkSize)
            {
                var chunk = keysToAdd.Skip(i).Take(chunkSize).ToList();
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<DBContext>();
                    await context.APIKeys.AddRangeAsync(chunk);
                    await context.SaveChangesAsync();
                    totalSaved += chunk.Count;
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error saving chunk of {ChunkCount} keys.", chunk.Count);
                }
            }
            
            logger?.LogInformation("Chunk save completed. Successfully saved {TotalSaved} out of {Total} keys.", totalSaved, keysToAdd.Count);
        }

        private static async Task<Stream?> GetContentStreamAsync(
            HttpResponseMessage response,
            SearchProviderEnum searchProviderEnum,
            bool treatAsGitHubApi,
            string rawUrl,
            ILogger? logger)
        {
            if (searchProviderEnum == SearchProviderEnum.GitHub || (searchProviderEnum == SearchProviderEnum.SourceGraph && treatAsGitHubApi))
            {
                var fileJson = await response.Content.ReadAsStringAsync();
                using var fileDoc = JsonDocument.Parse(fileJson);
                if (fileDoc.RootElement.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                {
                    string? contentBase64 = contentProp.GetString();
                    if (!string.IsNullOrEmpty(contentBase64))
                    {
                        var bytes = Convert.FromBase64String(contentBase64.Replace("\n", ""));
                        return new MemoryStream(bytes);
                    }
                }
                return null;
            }
            else
            {
                // For direct content providers, stream the content
                return await response.Content.ReadAsStreamAsync();
            }
        }

        private static async Task MonitorResourcesAsync(ILogger? logger)
        {
            var process = Process.GetCurrentProcess();
            
            while (!(_cancellationTokenSource?.Token.IsCancellationRequested ?? false))
            {
                try
                {
                    var workingSet = process.WorkingSet64;
                    var gcMemory = GC.GetTotalMemory(false);
                    
                    // Adaptive batch size based on memory pressure
                    if (workingSet > MaxMemoryUsageBytes * 0.8)
                    {
                        OptimalBatchSize = Math.Max(10, OptimalBatchSize / 2);
                        MaxConcurrentRequests = Math.Max(2, MaxConcurrentRequests / 2);
                        logger?.LogWarning("High memory usage detected ({WorkingSet:F2}GB). Reducing batch size to {BatchSize} and concurrency to {Concurrency}",
                            workingSet / (1024.0 * 1024.0 * 1024.0), OptimalBatchSize, MaxConcurrentRequests);
                        
                        // Force garbage collection
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                    else if (workingSet < MaxMemoryUsageBytes * 0.5 && OptimalBatchSize < 100)
                    {
                        OptimalBatchSize = Math.Min(100, OptimalBatchSize * 2);
                        MaxConcurrentRequests = Math.Min(Environment.ProcessorCount * 2, MaxConcurrentRequests * 2);
                        logger?.LogInformation("Memory usage normal ({WorkingSet:F2}GB). Increasing batch size to {BatchSize} and concurrency to {Concurrency}",
                            workingSet / (1024.0 * 1024.0 * 1024.0), OptimalBatchSize, MaxConcurrentRequests);
                    }

                    if (_cancellationTokenSource != null)
                        await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in resource monitoring");
                }
            }
        }

        private static async Task LogRateLimitAsync(
            IServiceProvider serviceProvider,
            SearchProviderEnum provider,
            string url,
            int statusCode,
            ILogger? logger)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DBContext>();
                
                var rateLimitLog = new RateLimitLog
                {
                    IpAddress = "Scraper",  // Since this is from the scraper, not a user IP
                    Endpoint = $"{provider}:{url.Substring(0, Math.Min(url.Length, 250))}",
                    RequestTimeUtc = DateTime.UtcNow
                };
                
                context.RateLimitLogs.Add(rateLimitLog);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to log rate limit event");
            }
        }
    }
}
