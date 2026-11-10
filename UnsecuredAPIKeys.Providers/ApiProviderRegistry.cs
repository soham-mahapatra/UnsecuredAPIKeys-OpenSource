using System.Reflection;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers
{
    public static class ApiProviderRegistry
    {
        /// <summary>
        /// Optional comma-separated provider name filter, e.g. "Groq" or "Groq,OpenAI".
        /// Set via env var UNSECURED_APIKEYS_PROVIDER_FILTER.
        /// </summary>
        private static readonly HashSet<string>? _providerNameFilter = ParseProviderFilter(
            Environment.GetEnvironmentVariable("UNSECURED_APIKEYS_PROVIDER_FILTER"));

        private static HashSet<string>? ParseProviderFilter(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var names = raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => n.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return names.Count == 0 ? null : names;
        }

        private static bool PassesNameFilter(IApiKeyProvider provider) =>
            _providerNameFilter is null || _providerNameFilter.Contains(provider.ProviderName);

        private static List<IApiKeyProvider> CreateProviders(Func<Type, ApiProviderAttribute, bool> predicate)
        {
            return [.. Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(type =>
                {
                    var attr = type.GetCustomAttribute<ApiProviderAttribute>();
                    return attr != null
                           && predicate(type, attr)
                           && typeof(IApiKeyProvider).IsAssignableFrom(type)
                           && !type.IsInterface
                           && !type.IsAbstract;
                })
                .Select(type => (IApiKeyProvider)Activator.CreateInstance(type)!)
                .Where(PassesNameFilter)];
        }

        private static readonly Lazy<List<IApiKeyProvider>> _allProviders = new(() =>
            CreateProviders((_, _) => true));

        private static readonly Lazy<List<IApiKeyProvider>> _scraperProviders = new(() =>
            CreateProviders((_, attr) => attr.ScraperUse));

        private static readonly Lazy<List<IApiKeyProvider>> _verifierProviders = new(() =>
            CreateProviders((_, attr) => attr.VerificationUse));

        /// <summary>
        /// Gets all providers with ApiProvider attribute (backward compatibility)
        /// </summary>
        public static IReadOnlyList<IApiKeyProvider> Providers => _allProviders.Value;

        /// <summary>
        /// Gets providers that are enabled for scraper use
        /// </summary>
        public static IReadOnlyList<IApiKeyProvider> ScraperProviders => _scraperProviders.Value;

        /// <summary>
        /// Gets providers that are enabled for verifier use
        /// </summary>
        public static IReadOnlyList<IApiKeyProvider> VerifierProviders => _verifierProviders.Value;

        /// <summary>
        /// Gets providers for a specific bot type
        /// </summary>
        /// <param name="botType">The type of bot (Scraper or Verifier)</param>
        /// <returns>List of providers enabled for the specified bot type</returns>
        public static IReadOnlyList<IApiKeyProvider> GetProvidersForBot(BotType botType)
        {
            return botType switch
            {
                BotType.Scraper => ScraperProviders,
                BotType.Verifier => VerifierProviders,
                _ => Providers
            };
        }
    }

    /// <summary>
    /// Enumeration of bot types for provider filtering
    /// </summary>
    public enum BotType
    {
        Scraper,
        Verifier
    }
}
