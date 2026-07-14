using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Safety;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Validation;
using System.Text.Json;

namespace RimWorldAiTranslator.App.Controls;

internal delegate bool UiWorkflowStarter(
    string label,
    Func<CancellationToken, Task> action,
    Action? completed);

internal delegate bool DurableUiWorkflowStarter(
    string label,
    Func<Task> accept,
    Func<Task, CancellationToken, Task> action,
    Action? completed);

internal sealed record TranslationProviderSelection(
    ApiProviderProfile Profile,
    ApiProviderSettings Settings,
    IReadOnlyList<string> Keys,
    bool DryRun);

internal sealed class SettingsControl : UserControl
{
    private static readonly HashSet<string> SupportedGlossaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".tsv", ".csv", ".json"
    };

    private readonly AppServices services;
    private readonly UiWorkflowStarter startWorkflow;
    private readonly DurableUiWorkflowStarter startDurableWorkflow;
    private readonly Func<string, bool> glossaryFileExists;
    private readonly Func<string, bool> directoryExists;
    private readonly Dictionary<string, ApiProviderSettings> providerDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> apiKeyDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox provider;
    private readonly Dictionary<string, Button> providerButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly FlowLayoutPanel providerList;
    private readonly Label providerTitle;
    private readonly Label providerDescription;
    private readonly Label providerNameLabel;
    private readonly TextBox providerName;
    private readonly Label keysLabel;
    private readonly TextBox keys;
    private readonly TextBox url;
    private readonly ComboBox model;
    private readonly ComboBox temperature;
    private readonly Label providerNotice;
    private readonly CheckBox dryRun;
    private readonly ComboBox themeMode;
    private readonly ComboBox designPreset;
    private readonly Label designDescription;
    private readonly ComboBox textSize;
    private readonly CheckBox highContrast;
    private readonly CheckBox autoSave;
    private readonly CheckBox rmkUseExisting;
    private readonly TextBox rmkRoot;
    private readonly Label rmkReference;
    private readonly TextBox customGlossary;
    private readonly Label customGlossaryStatus;
    private readonly Button customGlossaryChoose;
    private readonly Button customGlossaryClear;
    private readonly Panel apiPanel;
    private readonly Panel apiDetail;
    private readonly Panel appearancePanel;
    private readonly Panel glossaryPanel;
    private readonly Panel rmkPanel;
    private readonly Panel apiDivider;
    private readonly Panel rmkDivider;
    private readonly Button rmkAuto;
    private readonly Button rmkChoose;
    private readonly Button rmkOpen;
    private readonly Label rmkNote;
    private string currentProviderId = string.Empty;
    private bool loading;
    private bool unsafeEndpointRemovedOnLoad;
    private bool unsafeExtensionDataRemovedOnLoad;
    private long settingsSaveSequence;
    private bool appearanceChangePending;
    private bool disposed;
    private long glossaryStatusRevision;
    private long rmkReferenceRevision;
    private int rmkReferenceAsyncUiApplicationCount;
    private bool providerSaveBlocked;

    public SettingsControl(
        AppServices services,
        UiWorkflowStarter startWorkflow,
        DurableUiWorkflowStarter startDurableWorkflow,
        UiIoHooks? ioHooks = null)
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.ResizeRedraw,
            true);
        SuspendLayout();
        this.services = services;
        this.startWorkflow = startWorkflow;
        this.startDurableWorkflow = startDurableWorkflow;
        glossaryFileExists = ioHooks?.GlossaryFileExists ?? File.Exists;
        directoryExists = ioHooks?.DirectoryExists ?? Directory.Exists;
        Dock = DockStyle.Fill;
        TabStop = false;
        AutoScroll = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var heading = FixedLabel("설정", 24, 20, 180, 30, 14f, FontStyle.Bold);
        apiPanel = new Panel();
        appearancePanel = new Panel();
        // Keep the stored CustomGlossaryPath round-trip compatible without adding
        // the post-Golden picker surface to the canonical settings visual tree.
        glossaryPanel = new Panel { Visible = false, TabStop = false };
        rmkPanel = new Panel();
        apiPanel.TabIndex = 0;
        appearancePanel.TabIndex = 1;
        rmkPanel.TabIndex = 2;

        var apiHeading = FixedLabel("번역 API", 0, 0, 220, 28, 11f, FontStyle.Bold);
        var apiHint = FixedLabel("사용할 서비스를 선택하세요. 키는 저장되지 않습니다.", 0, 28, 360, 20, 8.3f, tag: "muted");
        provider = new ComboBox { Visible = false, TabStop = false, DisplayMember = nameof(ApiProviderProfile.Name) };
        foreach (var profile in ApiProviderCatalog.All) provider.Items.Add(profile);
        provider.SelectedIndexChanged += (_, _) => SelectProvider();

        providerList = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            AccessibleName = "번역 공급자 목록",
            AccessibleDescription = "사용할 AI 또는 Google 번역 공급자를 선택합니다.",
            TabIndex = 0
        };
        var providerTabIndex = 0;
        foreach (var profile in ApiProviderCatalog.All)
        {
            var button = Ui.Button(profile.Name, null, 172);
            button.Size = new Size(172, 34);
            button.Margin = new Padding(0, 0, 0, 5);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(10, 0, 4, 0);
            button.Font = new Font("Malgun Gothic", 8.7f, FontStyle.Bold);
            button.AccessibleName = $"{profile.Name} 번역 공급자";
            button.AccessibleDescription = profile.NeedsKey
                ? $"{profile.Description}. API 키가 필요한 공급자입니다."
                : $"{profile.Description}. API 키가 필요하지 않습니다.";
            button.TabIndex = providerTabIndex++;
            button.Click += (_, _) => provider.SelectedItem = profile;
            providerButtons[profile.Id] = button;
            providerList.Controls.Add(button);
        }
        apiDivider = new Panel { Tag = "divider" };
        apiDetail = new Panel();
        providerTitle = FixedLabel("Cerebras", 0, 0, 300, 28, 11f, FontStyle.Bold);
        providerDescription = FixedLabel("Gemma 4와 초고속 추론 모델", 0, 30, 430, 20, 8.3f, tag: "muted");
        providerNameLabel = FixedLabel("표시 이름", 0, 0, 82, 28, 8.3f, FontStyle.Bold, "muted");
        providerNameLabel.TextAlign = ContentAlignment.MiddleLeft;
        providerNameLabel.Visible = false;
        providerName = Ui.TextBox("사용자 지정 공급자 이름");
        providerName.SetBounds(88, 0, 372, 28);
        providerName.MaxLength = 80;
        providerName.Visible = false;
        providerName.AccessibleName = "사용자 지정 공급자 표시 이름";
        providerName.AccessibleDescription = "설정과 번역 화면에 표시할 이름입니다. 1자에서 80자까지 입력합니다.";
        providerName.TabIndex = 0;
        providerName.TextChanged += (_, _) => ValidateProvider();
        providerName.Leave += (_, _) => Save(false);

        keysLabel = FixedLabel("API 키 · 한 줄에 하나씩", 0, 58, 220, 20, 8.3f, FontStyle.Bold, "muted");
        keys = Ui.TextBox(string.Empty, true);
        keys.SetBounds(0, 80, 460, 82);
        keys.Margin = Padding.Empty;
        keys.Visible = false;
        keys.AccessibleName = "API 키";
        keys.AccessibleDescription = "한 줄에 하나씩 입력합니다. 키는 현재 실행 중인 메모리에만 보관되고 설정 파일에는 저장되지 않습니다.";
        keys.TabIndex = 1;
        keys.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(currentProviderId)) apiKeyDrafts[currentProviderId] = keys.Text;
            ValidateProvider();
        };
        var urlLabel = FixedLabel("API URL", 0, 172, 120, 20, 8.3f, FontStyle.Bold, "muted");
        url = Ui.TextBox();
        url.SetBounds(0, 194, 460, 30);
        url.Margin = Padding.Empty;
        url.AccessibleName = "공급자 API URL";
        url.AccessibleDescription = "HTTPS 공급자 주소를 입력합니다. 저장 및 번역 실행에서는 HTTP 주소를 허용하지 않습니다.";
        url.TabIndex = 3;
        url.TextChanged += (_, _) =>
        {
            if (!loading) unsafeEndpointRemovedOnLoad = false;
            ValidateProvider();
        };
        url.Leave += (_, _) => Save(false);
        var modelLabel = FixedLabel("모델", 0, 234, 100, 20, 8.3f, FontStyle.Bold, "muted");
        model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, IntegralHeight = false, MaxDropDownItems = 12, Font = new Font("Malgun Gothic", 9.5f) };
        model.SetBounds(0, 256, 300, 30);
        model.AccessibleName = "번역 모델";
        model.AccessibleDescription = "선택한 공급자에서 사용할 모델 이름입니다.";
        model.TabIndex = 4;
        model.TextChanged += (_, _) => ValidateProvider();
        model.Leave += (_, _) => Save(false);
        var temperatureLabel = FixedLabel("Temperature", 316, 234, 140, 20, 8.3f, FontStyle.Bold, "muted");
        temperature = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Font = new Font("Malgun Gothic", 9.5f) };
        temperature.Items.AddRange(["모델 기본값", "0", "0.1", "0.2"]);
        temperature.SetBounds(316, 256, 144, 30);
        temperature.AccessibleName = "번역 Temperature";
        temperature.AccessibleDescription = "공급자 기본값 또는 0에서 2 사이의 생성 온도를 선택합니다.";
        temperature.TabIndex = 5;
        temperature.TextChanged += (_, _) => ValidateProvider();
        temperature.Leave += (_, _) => Save(false);
        providerNotice = FixedLabel("키가 비어 있으면 Google 번역을 사용합니다.", 0, 298, 460, 78, 8.3f, tag: "muted");
        providerNotice.AccessibleName = "API 설정 상태 안내";
        providerNotice.UseMnemonic = false;
        dryRun = new CheckBox { Text = "Dry run", Bounds = new Rectangle(0, 380, 120, 26), BackColor = Color.Transparent };
        dryRun.AccessibleName = "번역 Dry run";
        dryRun.AccessibleDescription = "외부 번역 요청이나 결과 적용 없이 요청 준비 단계만 검증합니다.";
        dryRun.TabIndex = 6;
        var batchHint = FixedLabel("배치 크기 40 · 여러 키는 입력 순서대로 순환", 132, 380, 360, 24, 8.3f, tag: "muted");
        apiDetail.Controls.AddRange([
            providerTitle, providerDescription, providerNameLabel, providerName, keysLabel, keys,
            urlLabel, url, modelLabel, model, temperatureLabel, temperature, providerNotice, dryRun, batchHint
        ]);
        apiPanel.Controls.AddRange([apiHeading, apiHint, provider, providerList, apiDivider, apiDetail]);

        var appearanceDivider = new Panel { Bounds = new Rectangle(0, 0, 350, 1), Tag = "divider" };
        var appearanceHeading = FixedLabel("화면 및 편집", 0, 0, 240, 28, 11f, FontStyle.Bold);
        var designLabel = FixedLabel("디자인 컨셉", 0, 46, 160, 20, 8.5f, FontStyle.Bold, "muted");
        designPreset = NewChoice([
            new SettingChoice("프로페셔널", "Professional", "중립적인 회색과 청록 포인트를 사용한 정돈된 업무 화면"),
            new SettingChoice("사이파이", "SciFi", "차가운 금속색과 시안 포인트를 사용한 기술적인 화면"),
            new SettingChoice("비비드", "Vivid", "코랄, 청록, 파랑 상태색이 선명한 활기 있는 화면"),
            new SettingChoice("스튜디오", "Studio", "부드러운 중성색과 로즈 포인트를 사용한 차분한 화면"),
            new SettingChoice("프런티어", "Frontier", "황동색과 자연색을 사용한 RimWorld풍 작업 화면")
        ]);
        designPreset.SetBounds(0, 70, 240, 30);
        designPreset.AccessibleName = "디자인 컨셉";
        designPreset.AccessibleDescription = "작업 화면의 색상과 시각적 분위기를 선택합니다.";
        designPreset.TabIndex = 0;
        designPreset.SelectedIndexChanged += (_, _) =>
        {
            UpdateDesignDescription();
            Save(true);
        };
        designDescription = FixedLabel(string.Empty, 0, 108, 320, 38, 8.3f, tag: "muted");
        var themeLabel = FixedLabel("밝기", 0, 158, 120, 20, 8.5f, FontStyle.Bold, "muted");
        themeMode = NewChoice([
            new SettingChoice("시스템 설정 따름", "System", string.Empty),
            new SettingChoice("밝게", "Light", string.Empty),
            new SettingChoice("어둡게", "Dark", string.Empty)
        ]);
        themeMode.SetBounds(0, 182, 220, 30);
        themeMode.AccessibleName = "화면 밝기";
        themeMode.AccessibleDescription = "시스템 설정, 밝은 화면 또는 어두운 화면을 선택합니다.";
        themeMode.TabIndex = 1;
        themeMode.SelectedIndexChanged += (_, _) => Save(true);
        var textSizeLabel = FixedLabel("본문 글자 크기", 0, 230, 160, 20, 8.5f, FontStyle.Bold, "muted");
        textSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Malgun Gothic", 9.5f) };
        textSize.Items.AddRange(["9", "10", "11", "12"]);
        textSize.SetBounds(0, 254, 220, 30);
        textSize.AccessibleName = "본문 글자 크기";
        textSize.AccessibleDescription = "작업 화면의 본문 글자 크기를 선택합니다.";
        textSize.TabIndex = 2;
        textSize.SelectedIndexChanged += (_, _) => Save(true);
        highContrast = new CheckBox { Text = "고대비", Bounds = new Rectangle(0, 302, 150, 26), BackColor = Color.Transparent };
        highContrast.AccessibleName = "고대비 화면 사용";
        highContrast.AccessibleDescription = "텍스트와 배경의 대비가 더 큰 화면을 사용합니다.";
        highContrast.TabIndex = 3;
        highContrast.CheckedChanged += (_, _) => Save(true);
        autoSave = new CheckBox { Text = "편집 내용 자동 저장", Bounds = new Rectangle(0, 336, 210, 26), BackColor = Color.Transparent };
        autoSave.AccessibleName = "편집 내용 자동 저장";
        autoSave.AccessibleDescription = "검수 화면에서 변경한 번역과 메모를 자동으로 저장합니다.";
        autoSave.TabIndex = 4;
        autoSave.CheckedChanged += (_, _) => Save(false);
        var diagnostics = Ui.Button("진단 번들 저장", null, 170);
        diagnostics.SetBounds(0, 374, 170, 32);
        diagnostics.Margin = Padding.Empty;
        diagnostics.AccessibleDescription = "민감한 값을 제외한 로컬 진단 정보를 파일로 저장합니다.";
        diagnostics.TabIndex = 5;
        diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        appearancePanel.Controls.AddRange([
            appearanceDivider, appearanceHeading, designLabel, designPreset, designDescription, themeLabel,
            themeMode, textSizeLabel, textSize, highContrast, autoSave, diagnostics
        ]);

        var glossaryDivider = new Panel { Tag = "divider" };
        var glossaryHeading = FixedLabel("사용자 용어집", 0, 16, 240, 26, 10f, FontStyle.Bold);
        var glossaryHint = FixedLabel("TXT · TSV · CSV · JSON 파일을 번역 요청에 함께 사용합니다.", 0, 42, 520, 20, 8.3f, tag: "muted");
        customGlossary = Ui.TextBox();
        customGlossary.ReadOnly = true;
        customGlossary.SetBounds(0, 66, 650, 32);
        customGlossary.Margin = Padding.Empty;
        customGlossary.AccessibleName = "사용자 용어집 파일";
        customGlossary.AccessibleDescription = "현재 번역 요청에 사용할 로컬 TXT, TSV, CSV 또는 JSON 용어집 경로입니다.";
        customGlossary.TabIndex = 0;
        customGlossary.TextChanged += (_, _) => UpdateGlossaryStatus();
        customGlossaryChoose = Ui.Button("파일 선택", null, 94);
        customGlossaryChoose.SetBounds(662, 64, 94, 34);
        customGlossaryChoose.Margin = Padding.Empty;
        customGlossaryChoose.AccessibleName = "사용자 용어집 파일 선택";
        customGlossaryChoose.AccessibleDescription = "지원되는 로컬 용어집 파일을 선택합니다. 파일 내용은 선택 시 읽지 않습니다.";
        customGlossaryChoose.TabIndex = 1;
        customGlossaryChoose.Click += (_, _) => BrowseCustomGlossary();
        customGlossaryClear = Ui.Button("사용 안 함", null, 94);
        customGlossaryClear.SetBounds(764, 64, 94, 34);
        customGlossaryClear.Margin = Padding.Empty;
        customGlossaryClear.AccessibleName = "사용자 용어집 선택 해제";
        customGlossaryClear.AccessibleDescription = "현재 용어집 경로를 설정에서 제거합니다. 원본 파일은 삭제하지 않습니다.";
        customGlossaryClear.TabIndex = 2;
        customGlossaryClear.Click += (_, _) => ClearCustomGlossary();
        customGlossaryStatus = FixedLabel("사용자 용어집을 사용하지 않습니다.", 0, 102, 850, 22, 8.3f, tag: "muted");
        customGlossaryStatus.AccessibleName = "사용자 용어집 상태";
        glossaryPanel.Controls.AddRange([
            glossaryDivider, glossaryHeading, glossaryHint, customGlossary, customGlossaryChoose,
            customGlossaryClear, customGlossaryStatus
        ]);

        rmkDivider = new Panel { Tag = "divider" };
        var rmkHeading = FixedLabel("RMK 로컬 연동", 0, 18, 240, 26, 10f, FontStyle.Bold);
        var rmkWorkspaceLabel = FixedLabel("작업 클론 (bus 브랜치)", 0, 54, 220, 20, 8.5f, FontStyle.Bold, "muted");
        rmkRoot = Ui.TextBox();
        rmkRoot.ReadOnly = true;
        rmkRoot.SetBounds(0, 78, 650, 32);
        rmkRoot.Margin = Padding.Empty;
        rmkRoot.AccessibleName = "RMK 작업 클론 경로";
        rmkRoot.AccessibleDescription = "내보내기 대상으로 선택한 로컬 RMK 작업 클론 경로입니다.";
        rmkRoot.TabIndex = 0;
        rmkAuto = Ui.Button("자동 찾기", null, 94);
        rmkAuto.SetBounds(662, 76, 94, 34);
        rmkAuto.Margin = Padding.Empty;
        rmkAuto.AccessibleDescription = "쓰기 대상이 될 수 있는 로컬 RMK 작업 클론을 자동으로 찾습니다.";
        rmkAuto.TabIndex = 1;
        rmkAuto.Click += (_, _) => startWorkflow("RMK 작업 클론 자동 찾기", AutoFindRmkAsync, null);
        rmkChoose = Ui.Button("폴더 선택", null, 94);
        rmkChoose.SetBounds(764, 76, 94, 34);
        rmkChoose.Margin = Padding.Empty;
        rmkChoose.AccessibleDescription = "쓰기 대상으로 사용할 로컬 RMK 작업 클론 폴더를 선택합니다.";
        rmkChoose.TabIndex = 2;
        rmkChoose.Click += (_, _) => BrowseRmk();
        rmkOpen = Ui.Button("폴더 열기", null, 94);
        rmkOpen.SetBounds(866, 76, 94, 34);
        rmkOpen.Margin = Padding.Empty;
        rmkOpen.AccessibleDescription = "선택한 로컬 RMK 작업 클론 폴더를 탐색기에서 엽니다.";
        rmkOpen.TabIndex = 3;
        rmkOpen.Click += (_, _) => startWorkflow("RMK 작업 클론 폴더 열기", OpenRmkAsync, null);
        rmkReference = FixedLabel("RMK 구독본을 찾는 중입니다.", 0, 118, 1040, 24, 8.5f, tag: "muted");
        rmkUseExisting = new CheckBox
        {
            Text = "원문 갱신과 AI 번역에서 RMK 기존 번역 자동 사용",
            Bounds = new Rectangle(0, 144, 420, 26),
            BackColor = Color.Transparent,
            AccessibleName = "RMK 기존 번역 자동 사용",
            AccessibleDescription = "원문 갱신과 AI 번역에서 읽기 전용 RMK 참조 번역을 자동으로 사용합니다.",
            TabIndex = 4
        };
        rmkUseExisting.CheckedChanged += (_, _) => Save(false);
        rmkNote = FixedLabel("Steam 구독본은 읽기 전용 참조입니다. 내보내기는 bus 브랜치 작업 클론에만 기록하며 커밋·푸시는 하지 않습니다.", 438, 144, 650, 30, 8.3f, tag: "muted");
        rmkPanel.Controls.AddRange([
            rmkDivider, rmkHeading, rmkWorkspaceLabel, rmkRoot, rmkAuto, rmkChoose, rmkOpen,
            rmkReference, rmkUseExisting, rmkNote
        ]);

        Controls.AddRange([heading, apiPanel, appearancePanel, rmkPanel]);
        Resize += (_, _) =>
        {
            heading.Height = DeviceDpi > 96 ? 36 : 30;
            ResizeLayout();
        };
        LoadSettings();
        ResizeLayout();
        ResumeLayout(false);
    }

    public event EventHandler? SettingsSaved;
    public event EventHandler? AppearanceChanged;
    public event EventHandler? DiagnosticsRequested;

    public TranslationProviderSelection GetSelection()
    {
        CaptureProvider();
        var catalogProfile = provider.SelectedItem as ApiProviderProfile ?? ApiProviderCatalog.Get("Cerebras");
        var settings = providerDrafts.TryGetValue(catalogProfile.Id, out var configured)
            ? CloneProviderSettings(configured)
            : CloneProviderSettings(ApiProviderCatalog.CreateDefaultSettings()[catalogProfile.Id]);
        var profile = catalogProfile.Id.Equals("Custom", StringComparison.OrdinalIgnoreCase)
            ? catalogProfile with { Name = GetProviderDisplayName(catalogProfile, settings) }
            : catalogProfile;
        var parsed = profile.NeedsKey
            ? ProviderValidator.SplitKeys(apiKeyDrafts.TryGetValue(profile.Id, out var text) ? text : string.Empty)
            : [];
        return new TranslationProviderSelection(profile, settings, parsed, dryRun.Checked);
    }

    public void Reload() => LoadSettings();

    internal void ResumeAfterCloseCancellation()
    {
        if (disposed || IsDisposed) return;
        rmkAuto.Enabled = true;
        UpdateGlossaryStatus();
        QueueRmkReferenceUpdate();
    }

    private void LoadSettings()
    {
        loading = true;
        try
        {
            unsafeEndpointRemovedOnLoad = false;
            unsafeExtensionDataRemovedOnLoad = services.StoredSettingsCredentialCorrectionRequired;
            providerDrafts.Clear();
            foreach (var pair in services.Settings.ApiProviders)
            {
                var draft = CloneProviderSettings(pair.Value);
                var endpointError = ProviderValidator.GetEndpointErrorCode(
                    draft.Url,
                    allowLoopbackHttp: false,
                    allowEmpty: true);
                if (endpointError is "UrlContainsCredential" or "UrlQueryNotAllowed" or "UrlFragmentNotAllowed")
                {
                    draft.Url = string.Empty;
                    unsafeEndpointRemovedOnLoad = true;
                }
                providerDrafts[pair.Key] = draft;
            }
            apiKeyDrafts.Clear();
            foreach (var pair in services.ApiKeys)
                apiKeyDrafts[pair.Key] = pair.Value;
            SelectChoice(themeMode, services.Settings.ThemeMode);
            SelectChoice(designPreset, services.Settings.DesignPreset);
            textSize.SelectedItem = Math.Clamp(services.Settings.TextSize, 9, 12).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (textSize.SelectedIndex < 0) textSize.SelectedIndex = 1;
            highContrast.Checked = services.Settings.HighContrast;
            autoSave.Checked = services.Settings.AutoSave;
            rmkUseExisting.Checked = services.Settings.RmkUseExisting;
            var glossaryPath = services.Settings.CustomGlossaryPath;
            var glossaryPathChanged = !customGlossary.Text.Equals(glossaryPath, StringComparison.Ordinal);
            customGlossary.Text = glossaryPath;
            rmkRoot.Text = services.Settings.RmkWorkspaceRoot;
            var selected = ApiProviderCatalog.All.FirstOrDefault(item => item.Id.Equals(services.Settings.ApiProviderId, StringComparison.OrdinalIgnoreCase));
            provider.SelectedItem = selected ?? ApiProviderCatalog.All[0];
            UpdateDesignDescription();
            if (!glossaryPathChanged) UpdateGlossaryStatus();
            QueueRmkReferenceUpdate();
        }
        finally { loading = false; }
        ValidateProvider();
    }

    private void SelectProvider()
    {
        CaptureProvider();
        if (provider.SelectedItem is not ApiProviderProfile profile) return;
        currentProviderId = profile.Id;
        if (!providerDrafts.TryGetValue(profile.Id, out var settings))
        {
            settings = CloneProviderSettings(ApiProviderCatalog.CreateDefaultSettings()[profile.Id]);
            providerDrafts[profile.Id] = settings;
        }
        var wasLoading = loading;
        loading = true;
        try
        {
            keys.Text = profile.NeedsKey && apiKeyDrafts.TryGetValue(profile.Id, out var saved) ? saved : string.Empty;
            keys.Enabled = profile.NeedsKey;
            url.Text = settings.Url;
            model.Items.Clear();
            foreach (var value in profile.Models) model.Items.Add(value);
            model.Text = settings.Model;
            temperature.Text = settings.Temperature < 0
                ? "모델 기본값"
                : settings.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var isCustom = profile.Id.Equals("Custom", StringComparison.OrdinalIgnoreCase);
            var displayName = GetProviderDisplayName(profile, settings);
            providerTitle.Text = displayName;
            providerTitle.Visible = !isCustom;
            providerNameLabel.Visible = isCustom;
            providerName.Visible = isCustom;
            providerName.TabStop = isCustom;
            providerName.Text = displayName;
            providerDescription.Text = profile.Description;
            UpdateKeyEditorVisibility(profile);
            UpdateProviderButtons(profile.Id);
            ThemeManager.Apply(providerList, ThemeManager.Current, services.Settings.TextSize);
        }
        finally { loading = wasLoading; }
        ValidateProvider();
        if (!wasLoading) Save(false);
    }

    private void CaptureProvider()
    {
        if (loading || string.IsNullOrWhiteSpace(currentProviderId)) return;
        var profile = ApiProviderCatalog.Get(currentProviderId);
        if (profile.NeedsKey) apiKeyDrafts[currentProviderId] = keys.Text;
        else apiKeyDrafts.Remove(currentProviderId);
        if (!providerDrafts.TryGetValue(currentProviderId, out var settings))
        {
            settings = CloneProviderSettings(ApiProviderCatalog.CreateDefaultSettings()[currentProviderId]);
            providerDrafts[currentProviderId] = settings;
        }
        if (profile.Id.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            settings.Name = providerName.Text.Trim();
        }
        else
        {
            settings.Name = profile.Name;
        }
        settings.Url = url.Text.Trim();
        settings.Model = model.Text.Trim();
        settings.Temperature = ParseTemperature();
        UpdateProviderButtons(currentProviderId);
    }

    private void ValidateProvider()
    {
        if (loading || provider.SelectedItem is not ApiProviderProfile profile) return;
        CaptureProvider();
        var customNameValid = !profile.Id.Equals("Custom", StringComparison.OrdinalIgnoreCase)
            || TryGetCustomProviderName(out _);
        var settings = new ApiProviderSettings
        {
            Name = profile.Id.Equals("Custom", StringComparison.OrdinalIgnoreCase) ? providerName.Text.Trim() : profile.Name,
            Url = url.Text.Trim(),
            Model = model.Text.Trim(),
            Temperature = ParseTemperature()
        };
        var keyCount = profile.NeedsKey ? ProviderValidator.SplitKeys(keys.Text).Count : 0;
        var result = ProviderValidator.Validate(profile, settings, keyCount);
        var draftsValid = TryValidateProviderDrafts(profile.Id, out var draftErrors);
        providerSaveBlocked = !customNameValid || !result.Valid || !draftsValid;
        providerNotice.Text = unsafeExtensionDataRemovedOnLoad
            ? "저장 파일/백업의 위험한 API·인증 설정을 화면과 요청에서 제외했습니다.\r\n"
              + "settings.json과 .bak을 직접 정리한 뒤 안전한 설정을 다시 저장하세요.\r\n"
              + "두 파일이 안전해야 이 경고가 해제됩니다."
            : unsafeEndpointRemovedOnLoad
            ? "저장된 API URL의 안전하지 않은 쿼리·fragment를 화면과 요청에서 제외했습니다.\r\n"
              + "새 HTTPS URL을 확인한 뒤 안전한 설정으로 다시 저장하세요."
            : draftErrors.Any(error => error is "UrlContainsCredential" or "UrlQueryNotAllowed" or "UrlFragmentNotAllowed")
            ? "API URL에서는 허용된 api-version/format/version 쿼리만 사용하고 fragment와 인증 정보를 제거해야 합니다."
            : !customNameValid
            ? "사용자 지정 공급자 표시 이름을 1자 이상 입력하세요."
            : !draftsValid
            ? "공급자 설정을 수정해야 저장할 수 있습니다: " + string.Join(", ", draftErrors.Distinct(StringComparer.Ordinal))
            : result.Valid
            ? profile.NeedsKey ? $"사용 가능한 키 {result.KeyCount:N0}개 · {profile.Description}" : profile.Description
            : string.Join(" · ", result.ErrorCodes);
        providerNotice.AccessibleDescription = providerNotice.Text;
    }

    private double ParseTemperature() => temperature.Text.Equals("모델 기본값", StringComparison.OrdinalIgnoreCase)
        ? -1
        : double.TryParse(
            temperature.Text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? Math.Clamp(parsed, 0, 2)
            : 0.1;

    private void UpdateKeyEditorVisibility(ApiProviderProfile profile)
    {
        keysLabel.Visible = profile.NeedsKey;
        keys.Enabled = profile.NeedsKey;
        keys.Visible = profile.NeedsKey;
        keys.TabStop = profile.NeedsKey;
    }

    private bool TryGetCustomProviderName(out string displayName)
    {
        displayName = providerName.Text.Trim();
        return displayName.Length is >= 1 and <= 80 && !displayName.Any(char.IsControl);
    }

    private static bool IsValidProviderDisplayName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length is >= 1 and <= 80 && !normalized.Any(char.IsControl);
    }

    private static string GetProviderDisplayName(ApiProviderProfile profile, ApiProviderSettings settings) =>
        profile.Id.Equals("Custom", StringComparison.OrdinalIgnoreCase) && IsValidProviderDisplayName(settings.Name)
            ? settings.Name.Trim()
            : profile.Name;

    private void UpdateProviderButtons(string selectedProviderId)
    {
        foreach (var profile in ApiProviderCatalog.All)
        {
            if (!providerButtons.TryGetValue(profile.Id, out var button)) continue;
            providerDrafts.TryGetValue(profile.Id, out var settings);
            var label = settings is null ? profile.Name : GetProviderDisplayName(profile, settings);
            var selected = profile.Id.Equals(selectedProviderId, StringComparison.OrdinalIgnoreCase);
            button.Text = selected ? $"●  {label}" : label;
            button.Tag = selected ? "primary" : null;
            button.AccessibleName = selected
                ? $"{label} 번역 공급자, 현재 선택됨"
                : $"{label} 번역 공급자";
            var description = profile.NeedsKey
                ? $"{profile.Description}. API 키가 필요한 공급자입니다."
                : $"{profile.Description}. API 키가 필요하지 않습니다.";
            button.AccessibleDescription = selected
                ? $"{description} 현재 선택됨."
                : description;
        }
    }

    private bool TryValidateProviderDrafts(
        string selectedProviderId,
        out IReadOnlyList<string> errors)
    {
        var found = new List<string>();
        var enteredApiKeys = apiKeyDrafts.Values
            .SelectMany(ProviderValidator.SplitKeys)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pair in providerDrafts)
        {
            if (ProviderValidator.IsCredentialLikeProviderText(pair.Value.Name)
                || ProviderValidator.IsCredentialLikeProviderText(pair.Value.Model)
                || enteredApiKeys.Contains(pair.Value.Name.Trim())
                || enteredApiKeys.Contains(pair.Value.Model.Trim()))
            {
                found.Add("CredentialLikeProviderText");
            }
            var endpointError = ProviderValidator.GetEndpointErrorCode(
                pair.Value.Url,
                allowLoopbackHttp: false,
                allowEmpty: true);
            if (endpointError is not null) found.Add(endpointError);

            if (pair.Key.Equals("Custom", StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrWhiteSpace(pair.Value.Url)
                    || !string.IsNullOrWhiteSpace(pair.Value.Model)
                    || pair.Key.Equals(selectedProviderId, StringComparison.OrdinalIgnoreCase))
                && !IsValidProviderDisplayName(pair.Value.Name))
            {
                found.Add("CustomNameInvalid");
            }
        }

        var selectedProfile = ApiProviderCatalog.All.FirstOrDefault(
            item => item.Id.Equals(selectedProviderId, StringComparison.OrdinalIgnoreCase));
        if (selectedProfile is null
            || !providerDrafts.TryGetValue(selectedProviderId, out var selectedSettings))
        {
            found.Add("ProviderMissing");
        }
        else
        {
            var keyCount = selectedProfile.NeedsKey
                ? ProviderValidator.SplitKeys(
                    apiKeyDrafts.TryGetValue(selectedProfile.Id, out var text) ? text : string.Empty).Count
                : 0;
            found.AddRange(ProviderValidator.Validate(selectedProfile, selectedSettings, keyCount).ErrorCodes);
        }

        errors = found.Distinct(StringComparer.Ordinal).ToArray();
        return errors.Count == 0;
    }

    private AppSettingsDocument CreateSettingsSnapshot(string selectedProviderId)
    {
        var size = int.TryParse(
            Convert.ToString(textSize.SelectedItem, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedSize)
            ? parsedSize
            : 10;
        return new AppSettingsDocument
        {
            Version = AppSettingsDocument.CurrentVersion,
            ThemeMode = SelectedValue(themeMode, "System"),
            DesignPreset = SelectedValue(designPreset, "Professional"),
            TextSize = size,
            HighContrast = highContrast.Checked,
            AutoSave = autoSave.Checked,
            RmkUseExisting = rmkUseExisting.Checked,
            CustomGlossaryPath = customGlossary.Text.Trim(),
            RmkWorkspaceRoot = rmkRoot.Text.Trim(),
            ApiProviderId = selectedProviderId,
            ApiProviders = providerDrafts.ToDictionary(
                pair => pair.Key,
                pair => CloneProviderSettings(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            ExtensionData = CloneExtensionData(services.Settings.ExtensionData)
        };
    }

    private static ApiProviderSettings CloneProviderSettings(ApiProviderSettings value) => new()
    {
        Name = value.Name,
        Url = value.Url,
        Model = value.Model,
        Temperature = value.Temperature,
        ExtensionData = CloneExtensionData(value.ExtensionData)
    };

    private static Dictionary<string, JsonElement>? CloneExtensionData(
        Dictionary<string, JsonElement>? extensionData) =>
        extensionData?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);

    private bool Save(bool appearanceChanged)
    {
        if (loading || disposed) return false;
        CaptureProvider();
        var selectedProviderId = (provider.SelectedItem as ApiProviderProfile)?.Id ?? "Cerebras";
        if (!TryValidateProviderDrafts(selectedProviderId, out var errors))
        {
            providerSaveBlocked = true;
            providerNotice.Text = errors.Any(error => error is "UrlContainsCredential" or "UrlQueryNotAllowed" or "UrlFragmentNotAllowed")
                ? "API URL에서는 허용된 api-version/format/version 쿼리만 사용하고 fragment와 인증 정보를 제거해야 합니다."
                : "공급자 설정을 수정해야 저장할 수 있습니다: " + string.Join(", ", errors.Distinct(StringComparer.Ordinal));
            providerNotice.AccessibleDescription = providerNotice.Text;
            return false;
        }

        providerSaveBlocked = false;
        var settingsSnapshot = CreateSettingsSnapshot(selectedProviderId);
        var apiKeySnapshot = new Dictionary<string, string>(apiKeyDrafts, StringComparer.OrdinalIgnoreCase);
        appearanceChangePending |= appearanceChanged;
        var sequence = ++settingsSaveSequence;
        return startDurableWorkflow(
            "설정 저장",
            () =>
            {
                services.ApiKeys.Clear();
                foreach (var pair in apiKeySnapshot) services.ApiKeys[pair.Key] = pair.Value;
                return services.SaveSettingsAsync(settingsSnapshot, CancellationToken.None);
            },
            (acceptedSave, cancellationToken) => RunSaveBoundaryAsync(sequence, acceptedSave, cancellationToken),
            null);
    }

    private async Task RunSaveBoundaryAsync(
        long sequence,
        Task acceptedSave,
        CancellationToken cancellationToken)
    {
        try
        {
            await acceptedSave;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested || disposed) return;
            LogSettingsSaveFailure(exception);
            if (disposed || sequence != settingsSaveSequence) return;
            MessageBox.Show(
                FindForm(),
                "설정을 저장하지 못했습니다. 저장 위치의 권한과 사용 가능 여부를 확인한 뒤 다시 시도하세요.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (cancellationToken.IsCancellationRequested || disposed || sequence != settingsSaveSequence) return;
        unsafeExtensionDataRemovedOnLoad = services.StoredSettingsCredentialCorrectionRequired;
        ValidateProvider();
        var notifyAppearanceChanged = appearanceChangePending;
        appearanceChangePending = false;
        NotifySaved(SettingsSaved, "설정 반영");
        if (notifyAppearanceChanged) NotifySaved(AppearanceChanged, "화면 모양 반영");
    }

    private void NotifySaved(EventHandler? handlers, string operation)
    {
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                LogWarning($"{operation} 실패 ({exception.GetType().Name}).");
            }
        }
    }

    private void LogSettingsSaveFailure(Exception exception)
    {
        try
        {
            services.Logger.Error($"설정 저장 실패 ({exception.GetType().Name}).");
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Settings failure logging was unavailable ({loggingException.GetType().Name}).");
        }
    }

    private void ResizeLayout()
    {
        var width = ClientSize.Width;
        var inner = Math.Max(620, width - 56);
        if (width >= 1080)
        {
            var apiWidth = Math.Clamp((int)(inner * 0.68), 660, 760);
            apiPanel.SetBounds(28, 66, apiWidth, 480);
            appearancePanel.SetBounds(28 + apiWidth + 28, 66, Math.Max(260, inner - apiWidth - 28), 410);
            rmkPanel.SetBounds(28, 576, inner, 190);
            AutoScrollMinSize = new Size(0, 790);
        }
        else
        {
            apiPanel.SetBounds(28, 66, inner, 480);
            appearancePanel.SetBounds(28, 572, inner, 410);
            rmkPanel.SetBounds(28, 1010, inner, 220);
            AutoScrollMinSize = new Size(0, 1260);
        }
        ResizeApiLayout();
        ResizeRmkLayout();
    }

    private void ResizeApiLayout()
    {
        var listWidth = apiPanel.ClientSize.Width < 700 ? 164 : 190;
        var detailX = listWidth + 28;
        var detailWidth = Math.Max(360, apiPanel.ClientSize.Width - detailX);
        var fieldWidth = Math.Max(300, detailWidth - 10);
        providerList.SetBounds(0, 58, listWidth, 410);
        foreach (var button in providerButtons.Values) button.Width = Math.Max(146, listWidth - 24);
        apiDivider.SetBounds(listWidth + 12, 58, 1, 410);
        apiDetail.SetBounds(detailX, 58, detailWidth, 410);
        providerTitle.Width = fieldWidth;
        providerDescription.Width = fieldWidth;
        providerName.Width = Math.Max(180, fieldWidth - providerName.Left);
        keys.Width = fieldWidth;
        url.Width = fieldWidth;
        providerNotice.Width = fieldWidth;
        var modelWidth = Math.Max(190, (int)(fieldWidth * 0.65));
        var temperatureX = modelWidth + 14;
        model.Width = modelWidth;
        foreach (Control item in apiDetail.Controls)
            if (item.Text == "Temperature") item.SetBounds(temperatureX, 234, Math.Max(94, fieldWidth - temperatureX), 20);
        temperature.SetBounds(temperatureX, 256, Math.Max(94, fieldWidth - temperatureX), 30);
    }

    private void ResizeGlossaryLayout()
    {
        var width = glossaryPanel.ClientSize.Width;
        var divider = glossaryPanel.Controls.OfType<Panel>().First(control => control.Tag as string == "divider");
        divider.SetBounds(0, 0, width, 1);
        var pathWidth = Math.Max(220, width - 210);
        customGlossary.Width = pathWidth;
        var buttonX = pathWidth + 14;
        customGlossaryChoose.Left = buttonX;
        customGlossaryClear.Left = buttonX + 102;
        customGlossaryStatus.Width = width;
    }

    private void ResizeRmkLayout()
    {
        var width = rmkPanel.ClientSize.Width;
        rmkDivider.SetBounds(0, 0, width, 1);
        var pathWidth = Math.Max(260, width - 312);
        rmkRoot.Width = pathWidth;
        var buttonX = pathWidth + 14;
        rmkAuto.Left = buttonX;
        rmkChoose.Left = buttonX + 102;
        rmkOpen.Left = buttonX + 204;
        rmkReference.Width = width;
        var noteX = width > 760 ? 438 : 0;
        var noteY = width > 760 ? 144 : 174;
        rmkNote.SetBounds(noteX, noteY, Math.Max(280, width - noteX), 34);
    }

    private void BrowseCustomGlossary()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "사용자 용어집 선택",
            Filter = "지원되는 용어집 (*.txt;*.tsv;*.csv;*.json)|*.txt;*.tsv;*.csv;*.json|TXT 파일 (*.txt)|*.txt|TSV 파일 (*.tsv)|*.tsv|CSV 파일 (*.csv)|*.csv|JSON 파일 (*.json)|*.json",
            FilterIndex = 1,
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            ValidateNames = true,
            FileName = customGlossary.Text.Trim()
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (!IsSupportedGlossaryPath(dialog.FileName))
        {
            MessageBox.Show(
                FindForm(),
                "TXT, TSV, CSV 또는 JSON 용어집 파일만 선택할 수 있습니다.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var selectedPath = dialog.FileName.Trim();
        var pathChanged = !customGlossary.Text.Equals(selectedPath, StringComparison.Ordinal);
        customGlossary.Text = selectedPath;
        if (!pathChanged) UpdateGlossaryStatus();
        Save(false);
    }

    private void ClearCustomGlossary()
    {
        if (string.IsNullOrWhiteSpace(customGlossary.Text)) return;
        customGlossary.Text = string.Empty;
        Save(false);
    }

    private void UpdateGlossaryStatus()
    {
        var revision = ++glossaryStatusRevision;
        var path = customGlossary.Text.Trim();
        customGlossaryClear.Enabled = !string.IsNullOrWhiteSpace(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            customGlossaryStatus.Text = "사용자 용어집을 사용하지 않습니다.";
            customGlossaryStatus.AccessibleDescription = customGlossaryStatus.Text;
            return;
        }

        if (PathSafety.IsNetworkPath(path))
        {
            customGlossaryStatus.Text = "네트워크 용어집 경로는 자동으로 열거나 확인하지 않습니다.";
            customGlossaryStatus.AccessibleDescription = customGlossaryStatus.Text;
            return;
        }

        string fileName;
        string extension;
        try
        {
            fileName = Path.GetFileName(path);
            extension = Path.GetExtension(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            customGlossaryStatus.Text = "저장된 용어집 경로 형식을 확인하세요.";
            customGlossaryStatus.AccessibleDescription = customGlossaryStatus.Text;
            return;
        }

        if (!SupportedGlossaryExtensions.Contains(extension))
        {
            customGlossaryStatus.Text = $"지원하지 않는 용어집 형식 · {fileName}";
            customGlossaryStatus.AccessibleDescription = customGlossaryStatus.Text;
            return;
        }

        customGlossaryStatus.Text = $"용어집 파일 확인 중 · {fileName}";
        customGlossaryStatus.AccessibleDescription = customGlossaryStatus.Text;
        if (!startWorkflow(
                "사용자 용어집 파일 확인",
                token => UpdateGlossaryStatusAsync(path, fileName, extension, revision, token),
                null)
            && !disposed
            && revision == glossaryStatusRevision)
        {
            customGlossaryStatus.Text = $"용어집 파일 확인 대기 · {fileName}";
            customGlossaryStatus.AccessibleDescription = customGlossaryStatus.Text;
        }
    }

    private async Task UpdateGlossaryStatusAsync(
        string path,
        string fileName,
        string extension,
        long revision,
        CancellationToken cancellationToken)
    {
        try
        {
            var exists = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = glossaryFileExists(path);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || IsDisposed || revision != glossaryStatusRevision) return;
            customGlossaryStatus.Text = exists
                ? $"활성 용어집 · {fileName} · {extension.TrimStart('.').ToUpperInvariant()}"
                : $"용어집 파일을 찾을 수 없음 · {fileName}";
            customGlossaryStatus.AccessibleDescription = customGlossaryStatus.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (disposed || IsDisposed || revision != glossaryStatusRevision) return;
            customGlossaryStatus.Text = $"용어집 파일 확인 실패 · {fileName}";
            customGlossaryStatus.AccessibleDescription = customGlossaryStatus.Text;
        }
    }

    private static bool IsSupportedGlossaryPath(string path)
    {
        try { return SupportedGlossaryExtensions.Contains(Path.GetExtension(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    internal void SetGlossaryPathForTesting(string path) => customGlossary.Text = path;

    internal string GlossaryStatusForTesting => customGlossaryStatus.Text;

    internal string ProviderNoticeForTesting => providerNotice.Text;

    internal bool ProviderNoticeFullyVisibleForTesting
    {
        get
        {
            if (providerNotice.ClientSize.Width <= 0 || providerNotice.ClientSize.Height <= 0) return false;
            var preferred = TextRenderer.MeasureText(
                providerNotice.Text,
                providerNotice.Font,
                new Size(providerNotice.ClientSize.Width, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            return preferred.Height <= providerNotice.ClientSize.Height;
        }
    }

    internal bool ProviderSaveBlockedForTesting => providerSaveBlocked;

    internal bool StoredSettingsCorrectionRequiredForTesting =>
        unsafeEndpointRemovedOnLoad || unsafeExtensionDataRemovedOnLoad;

    internal bool HasRootSettingsExtensionForTesting(string name) =>
        services.Settings.ExtensionData?.ContainsKey(name) == true;

    internal bool HasProviderSettingsExtensionForTesting(string providerId, string name) =>
        services.Settings.ApiProviders.TryGetValue(providerId, out var settings)
        && settings.ExtensionData?.ContainsKey(name) == true;

    internal string GetPersistedProviderUrlForTesting(string providerId) =>
        services.Settings.ApiProviders.TryGetValue(providerId, out var settings)
            ? settings.Url
            : string.Empty;

    internal string GetDraftProviderUrlForTesting(string providerId) =>
        providerDrafts.TryGetValue(providerId, out var settings)
            ? settings.Url
            : string.Empty;

    internal void SetProviderUrlForTesting(string providerId, string value)
    {
        var profile = ApiProviderCatalog.All.First(
            item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(provider.SelectedItem, profile)) provider.SelectedItem = profile;
        url.Text = value;
    }

    internal void SetProviderModelForTesting(string providerId, string value)
    {
        var profile = ApiProviderCatalog.All.First(
            item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(provider.SelectedItem, profile)) provider.SelectedItem = profile;
        model.Text = value;
    }

    internal void SetApiKeysForTesting(string providerId, string value)
    {
        var profile = ApiProviderCatalog.All.First(
            item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(provider.SelectedItem, profile)) provider.SelectedItem = profile;
        apiKeyDrafts[providerId] = value;
        keys.Text = value;
    }

    internal bool ApiKeyEditorVisibleForTesting => keys.TabStop && keys.Enabled;

    internal string ApiKeyEditorTextForTesting => keys.Text;

    internal bool HasApiKeyEditButtonForTesting =>
        apiDetail.Controls.OfType<Button>().Any(button =>
            button.Text.Contains("키 편집", StringComparison.Ordinal));

    internal bool SaveProviderForTesting() => Save(false);

    internal bool SetAutoSaveAndSaveForTesting(bool value)
    {
        loading = true;
        try { autoSave.Checked = value; }
        finally { loading = false; }
        return Save(false);
    }

    internal bool PersistedAutoSaveForTesting => services.Settings.AutoSave;

    internal int RmkReferenceAsyncUiApplicationCountForTesting =>
        Volatile.Read(ref rmkReferenceAsyncUiApplicationCount);

    private async Task AutoFindRmkAsync(CancellationToken cancellationToken)
    {
        rmkAuto.Enabled = false;
        try
        {
            var path = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var roots = services.Discovery.GetSteamLibraryRoots();
                return services.RmkWorkspace.FindWritableWorkspace(
                    services.Discovery.GetModContainers(roots)
                        .Where(item => item.Source.Equals("Local", StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.Path),
                    cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed) return;
            if (!string.IsNullOrWhiteSpace(path)) rmkRoot.Text = path;
            Save(false);
            var referenceRevision = ++rmkReferenceRevision;
            await UpdateRmkReferenceAsync(rmkRoot.Text, referenceRevision, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested || disposed) return;
            LogWarning($"RMK 작업 클론 자동 찾기 실패 ({exception.GetType().Name}).");
            MessageBox.Show(
                FindForm(),
                "RMK 작업 클론을 자동으로 찾지 못했습니다. 폴더 선택을 사용하세요.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !disposed && !IsDisposed) rmkAuto.Enabled = true;
        }
    }

    private void BrowseRmk()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "RMK Git 작업 클론을 선택하세요.",
            SelectedPath = rmkRoot.Text.Trim()
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        rmkRoot.Text = dialog.SelectedPath;
        Save(false);
        QueueRmkReferenceUpdate();
    }

    private async Task OpenRmkAsync(CancellationToken cancellationToken)
    {
        var configuredRoot = rmkRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(configuredRoot)) return;
        if (PathSafety.IsNetworkPath(configuredRoot)) return;
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(configuredRoot)) SafeDirectoryLauncher.Open(configuredRoot);
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested || disposed || IsDisposed) return;
            LogWarning($"RMK 작업 클론 폴더 열기 실패 ({exception.GetType().Name}).");
            MessageBox.Show(
                FindForm(),
                "폴더를 열지 못했습니다. 폴더가 존재하고 접근 가능한지 확인하세요.",
                "RimWorld AI Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void QueueRmkReferenceUpdate()
    {
        var configuredRoot = rmkRoot.Text;
        var revision = ++rmkReferenceRevision;
        if (PathSafety.IsNetworkPath(configuredRoot))
        {
            rmkReference.Text = "네트워크 RMK 경로는 자동으로 열거나 확인하지 않습니다.";
            rmkOpen.Enabled = false;
            return;
        }
        rmkReference.Text = "RMK 구독본을 찾는 중입니다.";
        if (!startWorkflow(
                "RMK 구독본 확인",
                token => UpdateRmkReferenceAsync(configuredRoot, revision, token),
                null)
            && !disposed
            && revision == rmkReferenceRevision)
        {
            rmkReference.Text = "RMK 구독본 확인 대기";
        }
    }

    private async Task UpdateRmkReferenceAsync(
        string configuredRoot,
        long revision,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subscription = services.RmkWorkspace.FindSubscriptionRoot(services.Discovery.GetSteamLibraryRoots());
                return (Subscription: subscription, RootExists: directoryExists(configuredRoot));
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || IsDisposed || revision != rmkReferenceRevision) return;
            Interlocked.Increment(ref rmkReferenceAsyncUiApplicationCount);
            rmkReference.Text = string.IsNullOrWhiteSpace(result.Subscription)
                ? "RMK 구독본을 찾지 못했습니다."
                : $"RMK 구독본 · {result.Subscription}";
            rmkOpen.Enabled = result.RootExists;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested
                || disposed
                || IsDisposed
                || revision != rmkReferenceRevision) return;
            if (!IsDisposed)
            {
                Interlocked.Increment(ref rmkReferenceAsyncUiApplicationCount);
                rmkReference.Text = "RMK 구독본을 확인하지 못했습니다.";
                rmkOpen.Enabled = false;
            }
            LogWarning($"RMK 구독본 확인 실패 ({exception.GetType().Name}).");
        }
    }

    private void LogWarning(string message)
    {
        try
        {
            services.Logger.Warning(message);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Settings warning logging was unavailable ({exception.GetType().Name}).");
        }
    }

    private void UpdateDesignDescription()
    {
        designDescription.Text = designPreset.SelectedItem is SettingChoice choice ? choice.Description : "화면 성격을 선택합니다.";
    }

    private static ComboBox NewChoice(IEnumerable<SettingChoice> values)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(SettingChoice.Text), Font = new Font("Malgun Gothic", 9.5f) };
        foreach (var value in values) combo.Items.Add(value);
        return combo;
    }

    private static void SelectChoice(ComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items.Cast<SettingChoice>().FirstOrDefault(item => item.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string SelectedValue(ComboBox combo, string fallback) =>
        combo.SelectedItem is SettingChoice choice ? choice.Value : fallback;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            disposed = true;
            glossaryStatusRevision++;
            rmkReferenceRevision++;
            glossaryPanel.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Label FixedLabel(string text, int x, int y, int width, int height, float size, FontStyle style = FontStyle.Regular, string? tag = null)
    {
        var label = Ui.Label(text, size, style);
        label.AutoSize = false;
        label.SetBounds(x, y, width, height);
        label.Margin = Padding.Empty;
        label.Tag = tag;
        return label;
    }

    private sealed record SettingChoice(string Text, string Value, string Description);
}
