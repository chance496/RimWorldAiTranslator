using System.Diagnostics;
using System.Text;
using RimWorldAiTranslator.Core.Logging;
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

        var provider = FindProviderRequest(exception);
        var guidance = provider is not null
            ? provider.StatusCode switch
            {
                System.Net.HttpStatusCode.BadRequest =>
                    "번역 서비스가 현재 모델 또는 요청 형식을 거부했습니다(HTTP 400).",
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    "번역 서비스가 API 키 인증을 거부했습니다.",
                System.Net.HttpStatusCode.NotFound =>
                    "설정한 번역 모델 또는 API 엔드포인트를 찾지 못했습니다.",
                System.Net.HttpStatusCode.RequestTimeout =>
                    "번역 서비스 응답이 지연되어 요청 시간이 초과됐습니다.",
                System.Net.HttpStatusCode.TooManyRequests =>
                    "번역 서비스의 호출 한도 또는 무료 할당량을 초과했습니다.",
                _ when (int)provider.StatusCode >= 500 =>
                    $"번역 서비스가 일시적으로 과부하 상태이거나 사용할 수 없습니다(HTTP {(int)provider.StatusCode}).",
                _ => "번역 서비스가 요청을 처리하지 못했습니다."
            }
            : exception switch
            {
                UnauthorizedAccessException => "파일 또는 폴더에 접근할 권한이 없습니다.",
                IOException => "파일을 읽거나 쓰는 중 문제가 발생했습니다.",
                System.Text.Json.JsonException or InvalidDataException or FormatException =>
                    "작업 데이터가 손상되었거나 지원하지 않는 형식입니다.",
                HttpRequestException => "번역 서비스와 통신하지 못했습니다.",
                TimeoutException => "작업이 제한 시간 안에 끝나지 않았습니다.",
                _ => "작업을 완료하지 못했습니다."
            };
        var diagnostic = provider is null ? string.Empty : " " + CreateProviderUserDiagnostic(provider);

        return Bound(
            $"{guidance}{diagnostic} {CreateRecoveryGuidance(exception)}",
            MaximumUserDetailLength);
    }

    public static string CreateLogMessage(string operationTitle, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var title = string.IsNullOrWhiteSpace(operationTitle) ? "작업" : operationTitle.Trim();
        title = Bound(title.Replace('\r', ' ').Replace('\n', ' '), 96);
        var outcome = ClassifyOutcome(exception);
        var stack = CreateSafeStackTrace(exception);
        var provider = FindProviderRequest(exception);
        var providerDiagnostic = provider is null
            ? string.Empty
            : $" · Provider={SafeDiagnosticField(provider.Provider, 48)}"
              + $" · Model={SafeDiagnosticField(provider.Model, 96)}"
              + $" · HTTP={(int)provider.StatusCode}"
              + $" · Code={SafeDiagnosticField(provider.ErrorCode, 96, "-")}"
              + $" · RequestId={SafeDiagnosticField(provider.RequestId, 128, "-")}"
              + $" · Detail={SafeDiagnosticField(provider.ProviderMessage, 500, "-")}";
        return Bound(
            $"{title} 실패 · {SafeTypeName(exception)} · HResult=0x{exception.HResult:X8} · Outcome={outcome}{providerDiagnostic} · Stack={stack}",
            MaximumLogMessageLength);
    }

    public static string CreateRecoveryGuidance(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var provider = FindProviderRequest(exception);
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
            _ when provider is not null
                && provider.StatusCode == System.Net.HttpStatusCode.BadRequest =>
                "설정에서 공급자와 모델을 다시 선택하세요. 활동 로그의 공급자 오류 상세를 확인할 수 있습니다.",
            _ when provider is not null
                && provider.StatusCode == System.Net.HttpStatusCode.TooManyRequests =>
                "공급자가 안내한 대기 시간이 지난 뒤 다시 시도하거나 다른 무료 모델을 선택하세요.",
            _ when provider is not null
                && (int)provider.StatusCode >= 500 =>
                "설정을 바꾸지 말고 잠시 후 다시 시도하거나 설정에서 다른 사용 가능 모델을 선택하세요.",
            _ when provider is not null =>
                "API 키, 모델과 공급자 URL 설정을 확인한 뒤 다시 시도하세요.",
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
            "translate" when total > 0 => $"AI translation · {Math.Min(current, total):N0} of {total:N0} entries.",
            "translate" => "AI translation is in progress.",
            "retry" when total > 0 => $"Retrying a translation request · {Math.Min(current, total):N0} of {total:N0} entries completed.",
            "retry" => "Retrying a translation request.",
            "rate-limit" when TryReadWaitSeconds(progress.Message, out var waitSeconds) =>
                $"Waiting {waitSeconds:0.0}s for the provider rate limit.",
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

    private static bool TryReadWaitSeconds(string? message, out double seconds)
    {
        seconds = 0;
        const string prefix = "Waiting ";
        if (string.IsNullOrWhiteSpace(message)
            || !message.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffixIndex = message.IndexOf('s', prefix.Length);
        return suffixIndex > prefix.Length
               && double.TryParse(
                   message.AsSpan(prefix.Length, suffixIndex - prefix.Length),
                   System.Globalization.NumberStyles.AllowDecimalPoint,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out seconds)
               && double.IsFinite(seconds)
               && seconds is >= 0 and <= 300;
    }

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

    private static ProviderRequestException? FindProviderRequest(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProviderRequestException provider) return provider;
        }

        return null;
    }

    private static string CreateProviderUserDiagnostic(ProviderRequestException provider)
    {
        var identity = string.Join(" / ", new[]
        {
            SafeDiagnosticField(provider.Provider, 48),
            SafeDiagnosticField(provider.Model, 96)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(identity)) parts.Add(identity);
        parts.Add($"HTTP {(int)provider.StatusCode}");
        if (!string.IsNullOrWhiteSpace(provider.ErrorCode))
            parts.Add(SafeDiagnosticField(provider.ErrorCode, 96));
        if (!string.IsNullOrWhiteSpace(provider.RequestId))
            parts.Add($"요청 ID {SafeDiagnosticField(provider.RequestId, 128)}");
        if (ShouldShowProviderMessage(provider)
            && !string.IsNullOrWhiteSpace(provider.ProviderMessage))
            parts.Add(SafeDiagnosticField(provider.ProviderMessage, 180));
        return string.Join(" · ", parts);
    }

    private static bool ShouldShowProviderMessage(ProviderRequestException provider) =>
        provider.StatusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.NotFound;

    private static string SafeDiagnosticField(string? value, int maximumLength, string fallback = "")
    {
        var safe = AppLogger.Redact(value)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return string.IsNullOrWhiteSpace(safe) ? fallback : Bound(safe, maximumLength);
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
