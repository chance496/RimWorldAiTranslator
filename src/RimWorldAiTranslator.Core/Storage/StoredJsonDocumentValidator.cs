using System.Text.Json;
using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Review;

namespace RimWorldAiTranslator.Core.Storage;

internal static class StoredJsonDocumentValidator
{
    private static readonly string[] KnownSettingsProperties =
    [
        "version",
        "themeMode",
        "designPreset",
        "textSize",
        "highContrast",
        "autoSave",
        "rmkWorkspaceRoot",
        "rmkUseExisting",
        "customGlossaryPath",
        "apiProviderId",
        "apiProviders"
    ];

    public static bool RequiresValidation(object value) =>
        value is ProjectStoreDocument or AppSettingsDocument or ReviewDecisionDocument;

    public static string? Validate(object value, JsonElement root) => value switch
    {
        ProjectStoreDocument document => ValidateProjects(document, root),
        AppSettingsDocument document => ValidateSettings(document, root),
        ReviewDecisionDocument document => ValidateReview(document, root),
        _ => null
    };

    public static string? ValidateNoDuplicateProperties(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                    return "JSON document contains duplicate object property names.";

                var nestedError = ValidateNoDuplicateProperties(property.Value);
                if (nestedError is not null)
                    return nestedError;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                var nestedError = ValidateNoDuplicateProperties(element);
                if (nestedError is not null)
                    return nestedError;
            }
        }

        return null;
    }

    private static string? ValidateProjects(ProjectStoreDocument document, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "Project store root must be a JSON object.";
        if (!TryGetProperty(root, "projects", out var projects) || projects.ValueKind != JsonValueKind.Array || document.Projects is null)
            return "Project store is missing the projects collection.";
        if (document.UpdatedAt is null)
            return "Project store contains an invalid null scalar field.";
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in document.Projects)
        {
            if (project is null)
                return "Project store contains an invalid null project entry.";
            if (string.IsNullOrWhiteSpace(project.Id))
                return "Project store contains a project with a blank ID.";
            if (!projectIds.Add(project.Id))
                return "Project store contains duplicate project IDs.";
            if (project.Name is null
                || project.ModRoot is null
                || project.SourceKind is null
                || project.PackageId is null
                || project.WorkshopId is null
                || project.SourceLanguageFolder is null
                || project.LatestReviewRoot is null
                || project.LatestReviewAt is null
                || project.LastAppliedAt is null
                || project.CreatedAt is null
                || project.UpdatedAt is null)
                return "Project store contains an invalid null scalar field.";
            if (project.Runs is null)
                return "Project store contains a project with an invalid runs collection.";
            if (project.Runs.Any(run => run is null))
                return "Project store contains an invalid null run entry.";
            if (project.Runs.Any(run => run.ReviewRoot is null || run.CreatedAt is null || run.Provider is null))
                return "Project store contains a run with an invalid null scalar field.";
        }
        var versionError = ValidateOptionalVersion(
            root,
            1,
            ProjectStoreDocument.CurrentVersion,
            "project store");
        if (versionError is not null) return versionError;
        if (TryGetProperty(root, "revision", out var revision)
            && (revision.ValueKind != JsonValueKind.Number
                || !revision.TryGetInt64(out var revisionValue)
                || revisionValue < 0))
        {
            return "Project store contains an invalid revision.";
        }
        if (TryGetProperty(root, "version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var versionValue)
            && versionValue == ProjectStoreDocument.CurrentVersion
            && (!TryGetProperty(root, "revision", out revision)
                || !revision.TryGetInt64(out var currentRevision)
                || currentRevision <= 0))
        {
            return "Project store v3 is missing a positive revision.";
        }
        return null;
    }

    private static string? ValidateSettings(AppSettingsDocument document, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "Settings root must be a JSON object.";

        var hasKnownProperty = false;
        foreach (var name in KnownSettingsProperties)
        {
            if (!TryGetProperty(root, name, out _)) continue;
            hasKnownProperty = true;
            break;
        }
        if (!hasKnownProperty)
            return "Settings document has no recognized fields.";

        if (document.ApiProviders is null)
            return "Settings document has an invalid API provider collection.";
        if (document.ApiProviders.Any(provider => provider.Value is null))
            return "Settings document contains an invalid null API provider entry.";
        if (document.ThemeMode is null
            || document.DesignPreset is null
            || document.RmkWorkspaceRoot is null
            || document.CustomGlossaryPath is null
            || document.ApiProviderId is null
            || document.ApiProviders.Any(provider => provider.Key is null
                || provider.Value.Name is null
                || provider.Value.Url is null
                || provider.Value.Model is null))
            return "Settings document contains an invalid null scalar field.";

        return ValidateOptionalVersion(root, 1, AppSettingsDocument.CurrentVersion, "settings");
    }

    private static string? ValidateReview(ReviewDecisionDocument document, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "Review decision root must be a JSON object.";
        if (!TryGetProperty(root, "items", out var items) || items.ValueKind != JsonValueKind.Array || document.Items is null)
            return "Review decision store is missing the items collection.";
        if (document.QuarantinedItems is null)
            return "Review decision store has an invalid quarantined-items collection.";
        if (document.Items.Any(item => item is null))
            return "Review decision store contains an invalid null active item.";
        if (document.QuarantinedItems.Any(item => item is null))
            return "Review decision store contains an invalid null quarantined item.";
        if (document.ReviewRoot is null
            || document.Comparison is null
            || document.ComparisonSha256 is null
            || document.UpdatedAt is null
            || document.Items.Concat(document.QuarantinedItems).Any(HasNullDecisionScalar))
            return "Review decision store contains an invalid null scalar field.";
        var versionError = ValidateOptionalVersion(root, 1, ReviewDecisionDocument.CurrentVersion, "review decision store");
        if (versionError is not null) return versionError;
        if (!TryGetProperty(root, "version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number)
            || number < ReviewDecisionDocument.CurrentVersion)
        {
            return null;
        }
        if (!TryGetProperty(root, "comparisonSha256", out var sha256)
            || sha256.ValueKind != JsonValueKind.String
            || !ReviewComparisonDocument.IsSha256(sha256.GetString()))
        {
            return "Review decision store version 6 requires a valid comparisonSha256 binding.";
        }
        if (string.IsNullOrWhiteSpace(document.Comparison))
            return "Review decision store version 6 requires a comparison path binding.";
        if (!TryGetProperty(root, "quarantinedItems", out var quarantined)
            || quarantined.ValueKind != JsonValueKind.Array)
        {
            return "Review decision store version 6 requires the quarantinedItems collection.";
        }
        var keyedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keylessIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in document.Items.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
        {
            string canonicalTarget;
            try
            {
                canonicalTarget = ReviewTargetIdentity.Canonicalize(decision.Target);
            }
            catch (InvalidDataException)
            {
                return "Review decision store version 6 contains an active keyed decision without a canonical target identity.";
            }
            if (string.IsNullOrWhiteSpace(canonicalTarget))
            {
                return "Review decision store version 6 contains an active keyed decision without a canonical target identity.";
            }
            if (!keyedIdentities.Add($"{canonicalTarget.Length}:{canonicalTarget}{decision.Key.Length}:{decision.Key}"))
                return "Review decision store version 6 contains a duplicate active target+key identity.";
        }
        foreach (var decision in document.Items.Where(item => string.IsNullOrWhiteSpace(item.Key)))
        {
            if (string.IsNullOrWhiteSpace(decision.Target)
                || string.IsNullOrWhiteSpace(decision.DefClass)
                || string.IsNullOrWhiteSpace(decision.Node))
            {
                return "Review decision store version 6 contains an active keyless decision without target, defClass, and node identity.";
            }
            string canonicalTarget;
            try
            {
                canonicalTarget = ReviewTargetIdentity.Canonicalize(decision.Target);
            }
            catch (InvalidDataException)
            {
                return "Review decision store version 6 contains an active keyless decision without a canonical target identity.";
            }
            var identity = $"{canonicalTarget.Length}:{canonicalTarget}{decision.DefClass.Length}:{decision.DefClass}{decision.Node.Length}:{decision.Node}";
            if (!keylessIdentities.Add(identity))
                return "Review decision store version 6 contains a duplicate active keyless identity.";
        }
        return null;
    }

    private static bool HasNullDecisionScalar(ReviewDecision decision) =>
        decision.Id is null
        || decision.Key is null
        || decision.Target is null
        || decision.DefClass is null
        || decision.Node is null
        || decision.Status is null
        || decision.Text is null
        || decision.Note is null
        || decision.TranslationOrigin is null
        || decision.TranslationUpdatedAt is null
        || decision.SourceHash is null
        || decision.SourceText is null
        || decision.PreviousSourceText is null
        || decision.UpdatedAt is null;

    private static string? ValidateOptionalVersion(JsonElement root, int minimum, int maximum, string storeName)
    {
        if (!TryGetProperty(root, "version", out var version)) return null;
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            return $"The {storeName} version must be an integer.";
        return number >= minimum && number <= maximum
            ? null
            : $"Unsupported {storeName} version: {number}";
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        var found = false;
        value = default;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            found = true;
        }
        return found;
    }
}
