using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Validation;

public static partial class ProviderValidator
{
    public static ProviderValidationResult Validate(ApiProviderProfile profile, ApiProviderSettings config, int keyCount)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var isGoogle = profile.ProviderKind.Equals("Google", StringComparison.OrdinalIgnoreCase);
        var model = (config.Model ?? string.Empty).Trim();
        var urlText = (config.Url ?? string.Empty).Trim();
        var knownModel = profile.Models.Count == 0 || profile.Models.Contains(model, StringComparer.Ordinal);

        if (!isGoogle)
        {
            if (string.IsNullOrWhiteSpace(urlText))
            {
                errors.Add("UrlMissing");
            }
            else if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
            {
                errors.Add("UrlInvalid");
            }
            else
            {
                var loopback = uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1" or "::1";
                if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && loopback))
                {
                    errors.Add("HttpsRequired");
                }

                if (!string.IsNullOrWhiteSpace(uri.UserInfo) || CredentialQueryRegex().IsMatch(uri.Query))
                {
                    errors.Add("UrlContainsCredential");
                }
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                errors.Add("ModelMissing");
            }

            if (config.Temperature is < -1 or > 2)
            {
                errors.Add("TemperatureOutOfRange");
            }

            if (keyCount == 0 && profile.NeedsKey)
            {
                warnings.Add("NoKeyUsesGoogleFallback");
            }

            if (!knownModel)
            {
                warnings.Add("ManualModel");
            }

            warnings.Add("OfflineAvailabilityNotVerified");
        }
        else
        {
            warnings.Add("GooglePromptFeaturesUnavailable");
        }

        return new ProviderValidationResult(
            errors.Count == 0,
            errors,
            warnings,
            keyCount,
            model,
            new ProviderCapabilities(
                "bundled-profile",
                profile.ProviderKind,
                knownModel,
                profile.ResponseFormat,
                profile.TokenParameter,
                profile.MaxOutputTokens,
                profile.RequestsPerMinute,
                profile.InputTokensPerMinute,
                profile.DailyTokens));
    }

    public static IReadOnlyList<string> SplitKeys(string? text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    [GeneratedRegex("(?i)(api[_-]?key|token|authorization|secret)=", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialQueryRegex();
}
