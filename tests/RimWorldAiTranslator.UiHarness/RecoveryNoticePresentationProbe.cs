using System.Diagnostics;
using RimWorldAiTranslator.App;
using RimWorldAiTranslator.Core.Storage;

namespace RimWorldAiTranslator.UiHarness;

internal static class RecoveryNoticePresentationProbe
{
    private const string Mode = "--recovery-notice-presentation-probe";

    internal static bool TryRun(IReadOnlyList<string> args, out int exitCode)
    {
        if (!args.Any(value => value.Equals(Mode, StringComparison.OrdinalIgnoreCase)))
        {
            exitCode = 0;
            return false;
        }

        try
        {
            VerifyPresentation();
            exitCode = 0;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Recovery notice presentation probe failed ({exception.GetType().Name}).");
            exitCode = 1;
        }
        return true;
    }

    private static void VerifyPresentation()
    {
        const string firstPrivateParent = @"C:\Users\synthetic-private\project-alpha";
        const string secondPrivateParent = @"D:\secret-workspace\state";
        const string thirdPrivateParent = @"C:\confidential\nested";
        var notices = new JsonRecoveryNotice[]
        {
            new(
                firstPrivateParent + @"\projects.json",
                secondPrivateParent + @"\projects.json.corrupt-20260714"),
            new(secondPrivateParent + @"\settings.json", null),
            new(
                thirdPrivateParent + "\\review\r\n\u0001\u2028-decisions.json",
                thirdPrivateParent + "\\review\r\n\u0002\u2029-decisions.json.corrupt")
        };

        var presentation = RecoveryNoticePresentation.Create(notices);

        RequireContainsBoth(presentation, "projects.json");
        RequireContainsBoth(presentation, "projects.json.corrupt-20260714");
        RequireContainsBoth(presentation, "settings.json");
        RequireContainsBoth(presentation, "review-decisions.json");
        RequireContainsBoth(presentation, "review-decisions.json.corrupt");
        RequireContainsBoth(presentation, "3개");
        RequireContainsBoth(presentation, "보존본 없음");

        foreach (var privateValue in new[]
                 {
                     firstPrivateParent,
                     secondPrivateParent,
                     thirdPrivateParent,
                     "synthetic-private",
                     "secret-workspace",
                     "confidential",
                     @"C:\",
                     @"D:\"
                 })
        {
            RequireAbsentFromBoth(presentation, privateValue);
        }

        Require(!presentation.LogMessage.Any(character =>
                char.IsControl(character) || character is '\u2028' or '\u2029'),
            "The recovery log message retained a control character.");
        Require(!presentation.UserMessage.Any(character =>
                (char.IsControl(character) && character != '\n')
                || character is '\u2028' or '\u2029'),
            "The recovery user message retained an input control character.");
        Require(presentation.UserMessage.Length <= RecoveryNoticePresentation.MaximumUserMessageLength,
            "The recovery user message exceeded its length bound.");
        Require(presentation.LogMessage.Length <= RecoveryNoticePresentation.MaximumLogMessageLength,
            "The recovery log message exceeded its length bound.");

        var longFileName = new string('x', 512) + ".json";
        var bounded = RecoveryNoticePresentation.Create(
            [new JsonRecoveryNotice(@"C:\bounded-private\" + longFileName, null)]);
        Require(bounded.UserMessage.Length <= RecoveryNoticePresentation.MaximumUserMessageLength,
            "The long-name user message exceeded its length bound.");
        Require(bounded.LogMessage.Length <= RecoveryNoticePresentation.MaximumLogMessageLength,
            "The long-name log message exceeded its length bound.");
        RequireAbsentFromBoth(bounded, "bounded-private");
        RequireContainsBoth(bounded, "보존본 없음");

        var empty = RecoveryNoticePresentation.Create([]);
        RequireContainsBoth(empty, "보존본 없음");
    }

    private static void RequireContainsBoth(RecoveryNoticeMessages presentation, string expected)
    {
        Require(presentation.UserMessage.Contains(expected, StringComparison.Ordinal),
            $"The recovery user message omitted expected text: {expected}");
        Require(presentation.LogMessage.Contains(expected, StringComparison.Ordinal),
            $"The recovery log message omitted expected text: {expected}");
    }

    private static void RequireAbsentFromBoth(RecoveryNoticeMessages presentation, string forbidden)
    {
        Require(!presentation.UserMessage.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            "The recovery user message exposed a parent path.");
        Require(!presentation.LogMessage.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            "The recovery log message exposed a parent path.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
