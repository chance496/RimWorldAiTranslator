using System.Text.RegularExpressions;
using RimWorldAiTranslator.Core.Models;

namespace RimWorldAiTranslator.Core.Validation;

public static partial class ProviderValidator
{
    private static readonly HashSet<string> CredentialParameterNames = new(StringComparer.Ordinal)
    {
        "apikey", "key", "accesstoken", "token", "auth", "authorization",
        "clientsecret", "secret", "password", "credential", "xapikey",
        "xgoogapikey", "subscriptionkey", "ocpapimsubscriptionkey", "apitoken",
        "refreshtoken", "idtoken", "sessiontoken", "secrettoken", "secretkey",
        "apisecret", "authkey", "privatekey", "sharedsecret", "sastoken",
        "sharedaccesssignature", "clientassertion", "signature", "sig",
        "xamzsignature", "xamzcredential", "awsaccesskeyid", "awssecretaccesskey",
        "awssessiontoken", "pwd", "passwd", "bearer", "jwt", "accesskey",
        "secretaccesskey", "securitytoken", "xamzsecuritytoken", "clientcredential",
        "sessionid"
    };
    private static readonly string[] StrongCredentialPropertyMarkers =
    [
        "apikey", "xgoogapikey", "subscriptionkey", "accesstoken", "refreshtoken",
        "authtoken", "bearertoken", "idtoken", "sessiontoken", "clientsecret",
        "sharedsecret", "privatekey", "secretaccesskey", "awsaccesskeyid",
        "awssecretaccesskey", "awssessiontoken", "apitoken", "secrettoken",
        "secretkey", "apisecret", "authkey", "encryptionkey", "signingkey",
        "sastoken", "sharedaccesssignature", "clientassertion", "authorization",
        "password", "credential"
    ];
    private static readonly HashSet<string> CredentialPropertyQualifiers = new(StringComparer.Ordinal)
    {
        "value", "values", "text", "data", "setting", "settings", "encrypted",
        "ciphertext", "reference", "ref", "field", "property", "backup", "primary",
        "secondary", "alternate", "fallback", "previous", "current", "next", "old",
        "new", "legacy", "default", "temporary", "temp", "copy"
    };
    private static readonly HashSet<string> AllowedQueryParameterNames = new(StringComparer.Ordinal)
    {
        "apiversion", "format", "version"
    };
    private static readonly HashSet<string> AllowedFormatValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "json"
    };

    public static ProviderValidationResult Validate(ApiProviderProfile profile, ApiProviderSettings config, int keyCount)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var isGoogle = profile.ProviderKind.Equals("Google", StringComparison.OrdinalIgnoreCase);
        var model = (config.Model ?? string.Empty).Trim();
        var urlText = (config.Url ?? string.Empty).Trim();
        var knownModel = profile.Models.Count == 0 || profile.Models.Contains(model, StringComparer.Ordinal);

        var endpointError = GetEndpointErrorCode(urlText, allowLoopbackHttp: false, allowEmpty: isGoogle);
        if (endpointError is not null) errors.Add(endpointError);

        if (!isGoogle)
        {
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

    public static string? GetEndpointErrorCode(
        string? value,
        bool allowLoopbackHttp,
        bool allowEmpty)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return allowEmpty ? null : "UrlMissing";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return "UrlInvalid";
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo)
            || ContainsCredentialHost(uri.Host)
            || ContainsCredentialPath(uri.AbsolutePath)
            || ContainsCredentialParameter(uri.Query)
            || ContainsCredentialParameter(uri.Fragment))
        {
            return "UrlContainsCredential";
        }
        if (!string.IsNullOrEmpty(uri.Fragment)) return "UrlFragmentNotAllowed";
        if (!IsAllowedEndpointQuery(uri.Query)) return "UrlQueryNotAllowed";

        var loopback = uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1" or "::1";
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !(allowLoopbackHttp
                 && loopback
                 && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return "HttpsRequired";
        }

        return null;
    }

    public static void EnsureValidEndpoint(
        string? value,
        bool allowLoopbackHttp,
        bool allowEmpty = false)
    {
        var error = GetEndpointErrorCode(value, allowLoopbackHttp, allowEmpty);
        if (error is null) return;
        throw new InvalidDataException(error switch
        {
            "UrlMissing" => "API URL is required.",
            "UrlInvalid" => "API URL is invalid.",
            "HttpsRequired" => "API URL must use HTTPS.",
            "UrlContainsCredential" => "API URL must not contain credentials.",
            "UrlFragmentNotAllowed" => "API URL fragments are not allowed.",
            "UrlQueryNotAllowed" => "API URL query parameters are not on the safe endpoint allowlist.",
            _ => "API URL is not safe."
        });
    }

    private static bool IsAllowedEndpointQuery(string component)
    {
        var text = component.TrimStart('?');
        if (text.Length == 0) return true;
        if (!TryDecodeComponent(text, out text) || text.Contains(';')) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1) return false;
            var name = NormalizeParameterName(part[..separator]);
            var value = part[(separator + 1)..].Trim();
            if (!AllowedQueryParameterNames.Contains(name)
                || !seen.Add(name)
                || value.Length is < 1 or > 128
                || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                                          && character is not ('.' or '_' or '-' or '+'))
                || !IsAllowedEndpointQueryValue(name, value))
            {
                return false;
            }
        }
        return seen.Count > 0;
    }

    private static bool IsAllowedEndpointQueryValue(string name, string value) => name switch
    {
        "format" => AllowedFormatValues.Contains(value),
        "apiversion" or "version" => VersionValueRegex().IsMatch(value),
        _ => false
    };

    private static bool ContainsCredentialParameter(string component)
    {
        var text = component.TrimStart('?', '#');
        if (text.Length == 0) return false;
        if (!TryDecodeComponent(text, out text)) return true;
        foreach (var part in text.Split(['&', ';', '?', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsCredentialTokenValue(part)) return true;
            if (TrySplitAssignment(part, out var nameText, out var valueText))
            {
                if (IsCredentialName(nameText) || IsStrongCredentialPropertyName(nameText)) return true;
                if (IsCredentialTokenValue(valueText)) return true;
                continue;
            }
            if (IsCredentialName(part) || IsStrongCredentialPropertyName(part)) return true;
        }
        return false;
    }

    private static bool ContainsCredentialHost(string host)
    {
        if (!TryDecodeComponent(host, out var decodedHost, decodePlusAsSpace: false)) return true;
        var labels = decodedHost.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < labels.Length; index++)
        {
            var label = labels[index];
            if (IsCredentialTokenValue(label) || ContainsCredentialAssignment(label)) return true;
            if (index + 1 < labels.Length
                && (IsCredentialName(label) || IsStrongCredentialPropertyName(label)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsCredentialPath(string path)
    {
        if (!TryDecodeComponent(path, out var decodedPath, decodePlusAsSpace: false)) return true;
        var segments = decodedPath.Split(
            ['/', ';', '?', '&'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var text = segments[index].Trim();
            if (IsCredentialTokenValue(text)) return true;
            if (TrySplitAssignment(text, out var nameText, out var valueText))
            {
                if (IsCredentialName(nameText) || IsStrongCredentialPropertyName(nameText)) return true;
                if (IsCredentialTokenValue(valueText)) return true;
                continue;
            }
            if (index + 1 < segments.Length
                && (IsCredentialName(text) || IsStrongCredentialPropertyName(text)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryDecodeComponent(
        string value,
        out string decoded,
        bool decodePlusAsSpace = true)
    {
        decoded = decodePlusAsSpace ? value.Replace('+', ' ') : value;
        for (var pass = 0; pass < 3; pass++)
        {
            if (!HasOnlyValidPercentEscapes(decoded)) return false;
            string next;
            try
            {
                next = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (next.Equals(decoded, StringComparison.Ordinal)) return true;
            decoded = next;
        }

        return !decoded.Contains('%');
    }

    private static bool HasOnlyValidPercentEscapes(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length
                || !IsHexDigit(value[index + 1])
                || !IsHexDigit(value[index + 2]))
            {
                return false;
            }
            index += 2;
        }
        return true;
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static string NormalizeParameterName(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z') buffer[length++] = (char)(character + ('a' - 'A'));
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9') buffer[length++] = character;
        }
        return new string(buffer, 0, length);
    }

    internal static bool IsCredentialName(string value) =>
        CredentialParameterNames.Contains(NormalizeParameterName(value));

    internal static bool IsGenericKeyName(string value) =>
        NormalizeParameterName(value).Equals("key", StringComparison.Ordinal);

    internal static bool IsCredentialPropertyName(string value)
    {
        var text = value.Trim();
        if (IsCredentialTokenValue(text)) return true;
        if (TrySplitAssignment(text, out var nameText, out var assignedValue)
            && assignedValue.Length > 0
            && (IsCredentialName(nameText)
                || IsStrongCredentialPropertyName(nameText)
                || IsCredentialTokenValue(assignedValue)))
        {
            return true;
        }

        return IsStrongCredentialPropertyName(text);
    }

    internal static bool IsUnsafeGenericKeyProperty(string value, string? stringValue)
    {
        if (!IsGenericKeyName(value)) return false;
        var text = (stringValue ?? string.Empty).Trim();
        if (text.Length == 0) return true;
        return !LooksLikeKeyboardShortcut(text);
    }

    public static bool IsCredentialLikeValue(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        if (IsCredentialTokenValue(text) || ContainsCredentialAssignment(text)) return true;
        if (!Uri.TryCreate(text, UriKind.Absolute, out _)) return false;
        var endpointError = GetEndpointErrorCode(
            text,
            allowLoopbackHttp: false,
            allowEmpty: false);
        return endpointError is "UrlContainsCredential";
    }

    public static bool IsCredentialLikeProviderText(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (IsCredentialLikeValue(text)) return true;
        if (text.Length < 32) return false;
        // Provider labels/models are intentionally a narrower trust boundary than
        // arbitrary extension data. Very long opaque values are never useful here.
        if (text.Length > 512) return true;
        if (Guid.TryParse(text, out _)) return true;
        var opaqueSegments = text.Split(['.', ':'], StringSplitOptions.None);
        if (opaqueSegments.Length is >= 2 and <= 5
            && opaqueSegments.All(segment => segment.Length >= 8
                                             && segment.All(character => char.IsAsciiLetterOrDigit(character)
                                                                         || character is '-' or '_'))
            && HasOpaqueEntropy(string.Concat(opaqueSegments)))
        {
            return true;
        }
        if (text.All(Uri.IsHexDigit)) return true;
        if (!text.All(character => char.IsAsciiLetterOrDigit(character)
                                   || character is '+' or '/' or '-' or '_' or '='))
        {
            return false;
        }

        return HasOpaqueEntropy(text);
    }

    private static bool HasOpaqueEntropy(string value)
    {
        Span<int> frequencies = stackalloc int[128];
        frequencies.Clear();
        var distinct = 0;
        foreach (var character in value)
        {
            if (character >= frequencies.Length) return false;
            if (frequencies[character]++ == 0) distinct++;
        }
        if (distinct < 12) return false;

        var entropy = 0d;
        foreach (var count in frequencies)
        {
            if (count == 0) continue;
            var probability = (double)count / value.Length;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy >= 4.5d;
    }

    private static bool IsStrongCredentialPropertyName(string value)
    {
        var normalized = NormalizeParameterName(value);
        if (normalized.Length == 0 || normalized.Equals("key", StringComparison.Ordinal)) return false;
        if (CredentialParameterNames.Contains(normalized)) return true;
        foreach (var marker in StrongCredentialPropertyMarkers)
        {
            for (var index = normalized.IndexOf(marker, StringComparison.Ordinal);
                 index >= 0;
                 index = normalized.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
            {
                var qualifier = normalized[(index + marker.Length)..];
                if (HasOnlyCredentialPropertyQualifiers(qualifier))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasOnlyCredentialPropertyQualifiers(string value)
    {
        if (value.Length == 0) return true;
        // A very long property name containing a strong marker is safer to reject
        // than to allocate attacker-controlled parsing state for it.
        if (value.Length > 256) return true;

        Span<bool> reachable = stackalloc bool[value.Length + 1];
        reachable.Clear();
        reachable[0] = true;
        for (var index = 0; index < value.Length; index++)
        {
            if (!reachable[index]) continue;
            if (char.IsAsciiDigit(value[index])) reachable[index + 1] = true;
            foreach (var qualifier in CredentialPropertyQualifiers)
            {
                if (value.AsSpan(index).StartsWith(qualifier, StringComparison.Ordinal))
                    reachable[index + qualifier.Length] = true;
            }
        }
        return reachable[value.Length];
    }

    private static bool LooksLikeKeyboardShortcut(string value)
    {
        if (value.Length > 32) return false;
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) return false;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (!parts[index].Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                && !parts[index].Equals("Control", StringComparison.OrdinalIgnoreCase)
                && !parts[index].Equals("Alt", StringComparison.OrdinalIgnoreCase)
                && !parts[index].Equals("Shift", StringComparison.OrdinalIgnoreCase)
                && !parts[index].Equals("Win", StringComparison.OrdinalIgnoreCase)
                && !parts[index].Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        var key = parts[^1];
        return IsAllowedShortcutKey(key);
    }

    private static bool IsAllowedShortcutKey(string value)
    {
        if (value.Length == 1 && char.IsAsciiLetterOrDigit(value[0])) return true;
        if (value.Length is 2 or 3
            && value[0] is 'F' or 'f'
            && int.TryParse(value.AsSpan(1), out var function)
            && function is >= 1 and <= 24)
        {
            return true;
        }
        if (value.Length == 2
            && value[0] is 'D' or 'd'
            && char.IsAsciiDigit(value[1]))
        {
            return true;
        }
        if (value.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
            && value.Length == 7
            && char.IsAsciiDigit(value[^1]))
        {
            return true;
        }

        return value.Equals("Escape", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Esc", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Enter", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Return", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Tab", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Space", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Backspace", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Delete", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Del", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Insert", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Ins", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Home", StringComparison.OrdinalIgnoreCase)
               || value.Equals("End", StringComparison.OrdinalIgnoreCase)
               || value.Equals("PageUp", StringComparison.OrdinalIgnoreCase)
               || value.Equals("PageDown", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Up", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Down", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Left", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Right", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemPlus", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemMinus", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemComma", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemPeriod", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemQuestion", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemSemicolon", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemQuotes", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemOpenBrackets", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemCloseBrackets", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemPipe", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemTilde", StringComparison.OrdinalIgnoreCase)
               || value.Equals("OemBackslash", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Oem102", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCredentialTokenValue(string value)
    {
        var text = value.Trim();
        return HasAuthorizationCredential(text, "Bearer")
               || HasAuthorizationCredential(text, "Basic")
               || HasCredentialTokenPrefix(text, "sk-", 8)
               || HasCredentialTokenPrefix(text, "csk-", 8)
               || HasCredentialTokenPrefix(text, "gsk_", 8)
               || HasCredentialTokenPrefix(text, "AIza", 8)
               || HasCredentialTokenPrefix(text, "ghp_", 8)
               || HasCredentialTokenPrefix(text, "gho_", 8)
               || HasCredentialTokenPrefix(text, "ghu_", 8)
               || HasCredentialTokenPrefix(text, "ghs_", 8)
               || HasCredentialTokenPrefix(text, "ghr_", 8)
               || HasCredentialTokenPrefix(text, "github_pat_", 8)
               || HasCredentialTokenPrefix(text, "hf_", 8)
               || HasCredentialTokenPrefix(text, "glpat-", 8)
               || HasCredentialTokenPrefix(text, "xoxb-", 8)
               || HasCredentialTokenPrefix(text, "xoxp-", 8)
               || HasCredentialTokenPrefix(text, "xoxa-", 8)
               || HasCredentialTokenPrefix(text, "xoxr-", 8)
               || HasCredentialTokenPrefix(text, "xoxs-", 8)
               || HasCredentialTokenPrefix(text, "ya29.", 8)
               || IsDeepLAuthKey(text)
               || IsAwsAccessKey(text);
    }

    private static bool IsDeepLAuthKey(string value)
    {
        const string freeTierSuffix = ":fx";
        if (!value.EndsWith(freeTierSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        var identifier = value[..^freeTierSuffix.Length];
        return Guid.TryParseExact(identifier, "D", out _)
               || Guid.TryParseExact(identifier, "N", out _);
    }

    private static bool HasAuthorizationCredential(string value, string scheme)
    {
        if (value.Length <= scheme.Length
            || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(value[scheme.Length]))
        {
            return false;
        }

        var token = value[scheme.Length..].Trim();
        return token.Length > 0 && !token.Any(char.IsWhiteSpace);
    }

    private static bool HasCredentialTokenPrefix(string value, string prefix, int minimumTailLength)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || value.Length < prefix.Length + minimumTailLength)
        {
            return false;
        }

        for (var index = prefix.Length; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or '~' or '+' or '/' or '-' or '='))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAwsAccessKey(string value)
    {
        if (value.Length != 20
            || !(value.StartsWith("AKIA", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("ASIA", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        for (var index = 4; index < value.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(value[index])) return false;
        }
        return true;
    }

    private static bool ContainsCredentialAssignment(string value)
    {
        foreach (var part in value.Split(
                     ['&', ';', '?', '#', '/'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsCredentialTokenValue(part)) return true;
            if (!TrySplitAssignment(part, out var nameText, out var assignedValue)
                || assignedValue.Length == 0)
            {
                continue;
            }
            if (IsCredentialName(nameText)
                || IsStrongCredentialPropertyName(nameText)
                || IsCredentialTokenValue(assignedValue))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TrySplitAssignment(string value, out string name, out string assignedValue)
    {
        var equals = value.IndexOf('=');
        var colon = value.IndexOf(':');
        var separator = equals < 0
            ? colon
            : colon < 0 ? equals : Math.Min(equals, colon);
        if (separator <= 0 || separator == value.Length - 1)
        {
            name = string.Empty;
            assignedValue = string.Empty;
            return false;
        }

        name = value[..separator].Trim();
        assignedValue = value[(separator + 1)..].Trim();
        return name.Length > 0 && assignedValue.Length > 0;
    }

    public static IReadOnlyList<string> SplitKeys(string? text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    [GeneratedRegex("(?i)^(?:v?[0-9]{1,3}(?:\\.[0-9]{1,3}){0,3}|[0-9]{4}-[0-9]{2}-[0-9]{2}(?:-(?:preview|beta|alpha|rc)(?:\\.[0-9]{1,3})?)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionValueRegex();
}
