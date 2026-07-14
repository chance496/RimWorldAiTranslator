using System.Diagnostics;
using System.Text;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Translation;

namespace RimWorldAiTranslator.Core.Diagnostics;

public static class OperationErrorPresentation
{
    internal const int MaximumUserDetailLength = 360;
    internal const int MaximumLogMessageLength = 2_048;
    internal const int MaximumProgressMessageLength = 160;

    public static string CreateUserDetail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var guidance = exception switch
        {
            UnauthorizedAccessException => "파일 또는 폴더에 접근할 권한이 없습니다.",
            IOException => "파일을 읽거나 쓰는 중 문제가 발생했습니다.",
            System.Text.Json.JsonException or InvalidDataException or FormatException =>
                "작업 데이터가 손상되었거나 지원하지 않는 형식입니다.",
            HttpRequestException => "번역 서비스와 통신하지 못했습니다.",
            TimeoutException => "작업이 제한 시간 안에 끝나지 않았습니다.",
            _ => "작업을 완료하지 못했습니다."
        };

        return Bound(
            $"{guidance} {CreateRecoveryGuidance(exception)}",
            MaximumUserDetailLength);
    }

    public static string CreateLogMessage(string operationTitle, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var title = string.IsNullOrWhiteSpace(operationTitle) ? "작업" : operationTitle.Trim();
        title = Bound(title.Replace('\r', ' ').Replace('\n', ' '), 96);
        var outcome = ClassifyOutcome(exception);
        var stack = CreateSafeStackTrace(exception);
        return Bound(
            $"{title} 실패 · {SafeTypeName(exception)} · HResult=0x{exception.HResult:X8} · Outcome={outcome} · Stack={stack}",
            MaximumLogMessageLength);
    }

    public static string CreateRecoveryGuidance(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ClassifyOutcome(exception) switch
        {
            "ConcurrentChange" =>
                "다른 저장 작업이 먼저 변경한 파일은 덮어쓰지 않았습니다. 현재 파일과 복구 스냅샷을 보존하고 대상 내용을 다시 확인하세요.",
            "RollbackIncomplete" =>
                "자동 복구를 완료하지 못했습니다. 대상 파일과 복구 스냅샷을 변경하지 말고 로그를 확인한 뒤 수동 복구하세요.",
            "RolledBack" =>
                "이번 작업이 기록한 변경은 자동으로 되돌렸습니다. 입력과 대상 상태를 확인한 뒤 다시 시도하세요.",
            _ when exception is UnauthorizedAccessException =>
                "대상의 읽기 전용 상태와 폴더 권한을 확인한 뒤 다시 시도하세요.",
            _ when exception is IOException =>
                "대상 파일 잠금, 사용 가능한 디스크 공간, 폴더 권한과 백업 상태를 확인한 뒤 다시 시도하세요.",
            _ when exception is HttpRequestException or TimeoutException =>
                "네트워크와 공급자 설정을 확인하세요. 완료로 확인되지 않은 요청은 다시 검토한 뒤 재시도하세요.",
            _ => "입력과 대상 상태를 확인하고, 같은 오류가 반복되면 개인정보를 제외한 진단 기록을 검토하세요."
        };
    }

    public static TranslationProgress CreateSafeProgress(TranslationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var stage = NormalizeStage(progress.Stage);
        var current = Math.Max(0, progress.Current);
        var total = Math.Max(0, progress.Total);
        var message = stage switch
        {
            "initialize" => "Translation workspace initialized.",
            "scan" when total > 0 => $"Source scan completed · {current:N0} of {total:N0} entries.",
            "scan" => "Scanning source entries.",
            "source" => "Source language detection completed.",
            "prepare" when total > 0 => $"Preparing {total:N0} entries for translation.",
            "prepare" => "Preparing translation entries.",
            "translate" when total > 0 => $"Translating batch {Math.Min(current, total):N0} of {total:N0}.",
            "translate" => "Translating a batch.",
            "retry" when total > 0 => $"Retrying a translation request · {Math.Min(current, total):N0} of {total:N0}.",
            "retry" => "Retrying a translation request.",
            "rate-limit" => "Waiting for the provider request limit.",
            "warning" => "Some input could not be processed; safe items will continue.",
            "complete" => "Translation completed.",
            "cancelled" => "Translation cancelled; completed checkpoints were preserved.",
            _ when progress.IsWarning => "Translation continued with a warning.",
            _ => "Translation is in progress."
        };

        return progress with
        {
            Stage = stage,
            Message = Bound(message, MaximumProgressMessageLength),
            Current = current,
            Total = total
        };
    }

    private static string NormalizeStage(string? stage) => (stage ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "initialize" => "initialize",
        "scan" => "scan",
        "source" => "source",
        "prepare" => "prepare",
        "translate" => "translate",
        "retry" => "retry",
        "rate-limit" => "rate-limit",
        "warning" => "warning",
        "complete" => "complete",
        "cancelled" => "cancelled",
        _ => "progress"
    };

    private static string SafeTypeName(Exception exception)
    {
        var value = exception.GetType().Name;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '_' and not '`'))
        {
            return nameof(Exception);
        }

        return value;
    }

    private static string ClassifyOutcome(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConcurrentLeafChangeException) return "ConcurrentChange";
            if (current.Message.Contains("rollback was incomplete", StringComparison.OrdinalIgnoreCase))
                return "RollbackIncomplete";
            if (current.Message.Contains("were rolled back", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("was rolled back", StringComparison.OrdinalIgnoreCase))
            {
                return "RolledBack";
            }
        }

        return "Unverified";
    }

    private static string CreateSafeStackTrace(Exception exception)
    {
        const int maximumFrames = 12;
        var frames = new List<string>(maximumFrames);
        for (var current = exception; current is not null && frames.Count < maximumFrames; current = current.InnerException)
        {
            foreach (var frame in new StackTrace(current, fNeedFileInfo: false).GetFrames() ?? [])
            {
                var method = frame.GetMethod();
                if (method is null) continue;
                var declaringType = SafeFrameComponent(method.DeclaringType?.FullName, 128);
                var methodName = SafeFrameComponent(method.Name, 96);
                frames.Add($"{declaringType}.{methodName}");
                if (frames.Count >= maximumFrames) break;
            }
        }

        if (frames.Count == 0) return "unavailable";
        var builder = new StringBuilder();
        foreach (var frame in frames)
        {
            if (builder.Length > 0) builder.Append(" > ");
            builder.Append(frame);
        }
        return builder.ToString();
    }

    private static string SafeFrameComponent(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var bounded = value.Length <= maximumLength ? value : value[..maximumLength];
        return bounded.All(character => char.IsLetterOrDigit(character)
                                        || character is '_' or '.' or '+' or '`')
            ? bounded
            : "unknown";
    }

    private static string Bound(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
