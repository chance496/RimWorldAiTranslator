function Test-RimWorldApiProviderConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Profile,

        [Parameter(Mandatory = $true)]
        [object]$Config,

        [ValidateRange(0, 1000)]
        [int]$KeyCount = 0
    )

    $errors = New-Object "System.Collections.Generic.List[string]"
    $warnings = New-Object "System.Collections.Generic.List[string]"
    $providerKind = if ($Profile.PSObject.Properties["Provider"]) { [string]$Profile.Provider } else { "" }
    $isGoogle = $providerKind -eq "Google"
    $model = if ($Config.PSObject.Properties["model"]) { ([string]$Config.model).Trim() } else { "" }
    $urlText = if ($Config.PSObject.Properties["url"]) { ([string]$Config.url).Trim() } else { "" }
    $temperature = if ($Config.PSObject.Properties["temperature"]) { [double]$Config.temperature } else { -1.0 }
    $knownModels = if ($Profile.PSObject.Properties["Models"]) { @($Profile.Models | ForEach-Object { [string]$_ }) } else { @() }
    $knownModel = $knownModels.Count -eq 0 -or $model -in $knownModels
    $uri = $null

    if (-not $isGoogle) {
        if ([string]::IsNullOrWhiteSpace($urlText)) {
            [void]$errors.Add("UrlMissing")
        } elseif (-not [Uri]::TryCreate($urlText, [UriKind]::Absolute, [ref]$uri)) {
            [void]$errors.Add("UrlInvalid")
        } else {
            $isLoopback = $uri.IsLoopback -or $uri.Host -in @("localhost", "127.0.0.1", "::1")
            if ($uri.Scheme -ne "https" -and -not ($uri.Scheme -eq "http" -and $isLoopback)) {
                [void]$errors.Add("HttpsRequired")
            }
            if (-not [string]::IsNullOrWhiteSpace($uri.UserInfo) -or $uri.Query -match '(?i)(api[_-]?key|token|authorization|secret)=') {
                [void]$errors.Add("UrlContainsCredential")
            }
        }
        if ([string]::IsNullOrWhiteSpace($model)) { [void]$errors.Add("ModelMissing") }
        if ($temperature -lt -1 -or $temperature -gt 2) { [void]$errors.Add("TemperatureOutOfRange") }
        if ($KeyCount -eq 0 -and $Profile.PSObject.Properties["NeedsKey"] -and [bool]$Profile.NeedsKey) {
            [void]$warnings.Add("NoKeyUsesGoogleFallback")
        }
        if (-not $knownModel) { [void]$warnings.Add("ManualModel") }
        [void]$warnings.Add("OfflineAvailabilityNotVerified")
    } else {
        [void]$warnings.Add("GooglePromptFeaturesUnavailable")
    }

    $capabilities = [ordered]@{
        source = "bundled-profile"
        provider = $providerKind
        knownModel = $knownModel
        responseFormat = if ($Profile.PSObject.Properties["ResponseFormat"]) { [string]$Profile.ResponseFormat } else { "" }
        tokenParameter = if ($Profile.PSObject.Properties["TokenParameter"]) { [string]$Profile.TokenParameter } else { "" }
        maxOutput = if ($Profile.PSObject.Properties["MaxOutput"]) { [int]$Profile.MaxOutput } else { 0 }
        rpm = if ($Profile.PSObject.Properties["Rpm"]) { [int]$Profile.Rpm } else { 0 }
        inputTpm = if ($Profile.PSObject.Properties["InputTpm"]) { [int]$Profile.InputTpm } else { 0 }
        dailyTokens = if ($Profile.PSObject.Properties["DailyTokens"]) { [int64]$Profile.DailyTokens } else { 0 }
    }
    return [pscustomobject]@{
        Valid = $errors.Count -eq 0
        ErrorCodes = $errors.ToArray()
        WarningCodes = $warnings.ToArray()
        KeyCount = $KeyCount
        Model = $model
        Capabilities = [pscustomobject]$capabilities
    }
}
