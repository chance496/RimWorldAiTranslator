using RimWorldAiTranslator.Core.Models;
using RimWorldAiTranslator.Core.Translation;
using RimWorldAiTranslator.Core.Validation;

namespace RimWorldAiTranslator.App.Controls;

internal sealed record TranslationProviderSelection(
    ApiProviderProfile Profile,
    ApiProviderSettings Settings,
    IReadOnlyList<string> Keys);

internal sealed class SettingsControl : UserControl
{
    private readonly AppServices services;
    private readonly ComboBox provider;
    private readonly TextBox keys;
    private readonly TextBox url;
    private readonly ComboBox model;
    private readonly NumericUpDown temperature;
    private readonly Label providerNotice;
    private readonly ComboBox themeMode;
    private readonly ComboBox designPreset;
    private readonly NumericUpDown textSize;
    private readonly CheckBox highContrast;
    private readonly CheckBox autoSave;
    private readonly CheckBox rmkUseExisting;
    private readonly TextBox rmkRoot;
    private readonly TextBox customGlossary;
    private string currentProviderId = string.Empty;

    public SettingsControl(AppServices services)
    {
        this.services = services;
        Dock = DockStyle.Fill;
        Padding = new Padding(38, 28, 38, 28);
        AutoScroll = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 20) };
        heading.Controls.Add(Ui.Label("설정", 17f, FontStyle.Bold));
        heading.Controls.Add(Ui.Label("API 키는 저장하지 않으며 프로그램을 닫으면 메모리에서 사라집니다.", 9.5f));
        root.Controls.Add(heading, 0, 0);
        root.SetColumnSpan(heading, 2);

        var apiCard = CreateCard("번역 API");
        var api = (TableLayoutPanel)apiCard.Controls[0];
        provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, DisplayMember = nameof(ApiProviderProfile.Name) };
        foreach (var profile in ApiProviderCatalog.All) provider.Items.Add(profile);
        provider.SelectedIndexChanged += (_, _) => SelectProvider();
        keys = Ui.TextBox("API 키를 줄마다 하나씩 입력", true);
        keys.Height = 96;
        keys.UseSystemPasswordChar = true;
        keys.TextChanged += (_, _) => ValidateProvider();
        url = Ui.TextBox("https://...");
        url.TextChanged += (_, _) => ValidateProvider();
        model = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown, IntegralHeight = false, MaxDropDownItems = 12 };
        model.TextChanged += (_, _) => ValidateProvider();
        temperature = new NumericUpDown { DecimalPlaces = 2, Increment = 0.05m, Minimum = -1, Maximum = 2, Dock = DockStyle.Top };
        providerNotice = Ui.Label(string.Empty, 8.5f);
        providerNotice.MaximumSize = new Size(700, 60);
        AddField(api, "서비스", provider);
        AddField(api, "API 키", keys);
        AddField(api, "API URL", url);
        AddField(api, "모델", model);
        AddField(api, "Temperature (-1은 모델 기본값)", temperature);
        api.Controls.Add(providerNotice);

        var appearanceCard = CreateCard("화면과 작업");
        var appearance = (TableLayoutPanel)appearanceCard.Controls[0];
        themeMode = NewChoice(["System", "Light", "Dark"]);
        designPreset = NewChoice(["Professional", "SciFi", "Vivid", "Studio", "Frontier"]);
        textSize = new NumericUpDown { Minimum = 9, Maximum = 16, Dock = DockStyle.Top };
        highContrast = new CheckBox { Text = "고대비", AutoSize = true };
        autoSave = new CheckBox { Text = "검수 내용 자동 저장", AutoSize = true };
        rmkUseExisting = new CheckBox { Text = "원문 갱신과 AI 번역에서 RMK 기존 번역 자동 사용", AutoSize = true };
        rmkRoot = Ui.TextBox("RMK Git 작업 클론 경로");
        rmkRoot.Dock = DockStyle.Fill;
        var browseRmk = Ui.Button("찾기", null, 74);
        browseRmk.Click += (_, _) => BrowseRmk();
        var rmkLine = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = 40, MinimumSize = new Size(420, 40) };
        rmkLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rmkLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        rmkLine.Controls.Add(rmkRoot, 0, 0);
        rmkLine.Controls.Add(browseRmk, 1, 0);
        customGlossary = Ui.TextBox("추가 용어집 TXT/TSV/CSV/JSON 경로");
        customGlossary.Dock = DockStyle.Fill;
        var browseGlossary = Ui.Button("찾기", null, 74);
        browseGlossary.Click += (_, _) => BrowseGlossary();
        var clearGlossary = Ui.Button("지우기", null, 74);
        clearGlossary.Click += (_, _) => customGlossary.Clear();
        var glossaryLine = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, Height = 40, MinimumSize = new Size(420, 40) };
        glossaryLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        glossaryLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        glossaryLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        glossaryLine.Controls.Add(customGlossary, 0, 0);
        glossaryLine.Controls.Add(browseGlossary, 1, 0);
        glossaryLine.Controls.Add(clearGlossary, 2, 0);
        AddField(appearance, "테마", themeMode);
        AddField(appearance, "디자인 컨셉", designPreset);
        AddField(appearance, "글자 크기", textSize);
        appearance.Controls.Add(highContrast);
        appearance.Controls.Add(autoSave);
        appearance.Controls.Add(rmkUseExisting);
        AddField(appearance, "추가 용어집 (선택)", glossaryLine);
        AddField(appearance, "RMK 작업 폴더", rmkLine);
        var save = Ui.Button("설정 저장", "primary", 130);
        save.Click += (_, _) => Save();
        appearance.Controls.Add(save);
        var diagnostics = Ui.Button("진단 번들 저장", null, 150);
        diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        appearance.Controls.Add(diagnostics);

        root.Controls.Add(apiCard, 0, 1);
        root.Controls.Add(appearanceCard, 1, 1);
        Controls.Add(root);
        LoadSettings();
    }

    public event EventHandler? SettingsSaved;
    public event EventHandler? AppearanceChanged;
    public event EventHandler? DiagnosticsRequested;

    public TranslationProviderSelection GetSelection()
    {
        CaptureProvider();
        var profile = provider.SelectedItem as ApiProviderProfile ?? ApiProviderCatalog.Get("Cerebras");
        var settings = services.Settings.ApiProviders[profile.Id];
        var parsed = ProviderValidator.SplitKeys(services.ApiKeys.TryGetValue(profile.Id, out var text) ? text : string.Empty);
        return new TranslationProviderSelection(profile, settings, parsed);
    }

    private void LoadSettings()
    {
        themeMode.SelectedItem = services.Settings.ThemeMode;
        if (themeMode.SelectedIndex < 0) themeMode.SelectedIndex = 0;
        designPreset.SelectedItem = services.Settings.DesignPreset;
        if (designPreset.SelectedIndex < 0) designPreset.SelectedIndex = 0;
        textSize.Value = Math.Clamp(services.Settings.TextSize, 9, 16);
        highContrast.Checked = services.Settings.HighContrast;
        autoSave.Checked = services.Settings.AutoSave;
        rmkUseExisting.Checked = services.Settings.RmkUseExisting;
        customGlossary.Text = services.Settings.CustomGlossaryPath;
        rmkRoot.Text = services.Settings.RmkWorkspaceRoot;
        var selected = ApiProviderCatalog.All.FirstOrDefault(profile => profile.Id.Equals(services.Settings.ApiProviderId, StringComparison.OrdinalIgnoreCase));
        provider.SelectedItem = selected ?? ApiProviderCatalog.All[0];
    }

    private void SelectProvider()
    {
        CaptureProvider();
        if (provider.SelectedItem is not ApiProviderProfile profile) return;
        currentProviderId = profile.Id;
        if (!services.Settings.ApiProviders.TryGetValue(profile.Id, out var settings))
        {
            settings = ApiProviderCatalog.CreateDefaultSettings()[profile.Id];
            services.Settings.ApiProviders[profile.Id] = settings;
        }
        keys.TextChanged -= KeysChangedPlaceholder;
        keys.Text = services.ApiKeys.TryGetValue(profile.Id, out var saved) ? saved : string.Empty;
        keys.TextChanged += KeysChangedPlaceholder;
        keys.Enabled = profile.NeedsKey;
        url.Text = settings.Url;
        model.Items.Clear();
        foreach (var value in profile.Models) model.Items.Add(value);
        model.Text = settings.Model;
        temperature.Value = Math.Clamp((decimal)settings.Temperature, temperature.Minimum, temperature.Maximum);
        ValidateProvider();
    }

    private void KeysChangedPlaceholder(object? sender, EventArgs e) => ValidateProvider();

    private void CaptureProvider()
    {
        if (string.IsNullOrWhiteSpace(currentProviderId)) return;
        services.ApiKeys[currentProviderId] = keys.Text;
        if (!services.Settings.ApiProviders.TryGetValue(currentProviderId, out var settings)) return;
        settings.Url = url.Text.Trim();
        settings.Model = model.Text.Trim();
        settings.Temperature = (double)temperature.Value;
    }

    private void ValidateProvider()
    {
        if (provider.SelectedItem is not ApiProviderProfile profile) return;
        var settings = new ApiProviderSettings { Name = profile.Name, Url = url.Text.Trim(), Model = model.Text.Trim(), Temperature = (double)temperature.Value };
        var result = ProviderValidator.Validate(profile, settings, ProviderValidator.SplitKeys(keys.Text).Count);
        providerNotice.Text = result.Valid
            ? profile.NeedsKey ? $"사용 가능한 키 {result.KeyCount}개 · {profile.Description}" : profile.Description
            : string.Join(" · ", result.ErrorCodes);
    }

    private void Save()
    {
        CaptureProvider();
        services.Settings.ThemeMode = Convert.ToString(themeMode.SelectedItem) ?? "System";
        services.Settings.DesignPreset = Convert.ToString(designPreset.SelectedItem) ?? "Professional";
        services.Settings.TextSize = (int)textSize.Value;
        services.Settings.HighContrast = highContrast.Checked;
        services.Settings.AutoSave = autoSave.Checked;
        services.Settings.RmkUseExisting = rmkUseExisting.Checked;
        services.Settings.CustomGlossaryPath = customGlossary.Text.Trim();
        services.Settings.RmkWorkspaceRoot = rmkRoot.Text.Trim();
        services.Settings.ApiProviderId = (provider.SelectedItem as ApiProviderProfile)?.Id ?? "Cerebras";
        services.SaveSettings(services.Settings);
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reload() => LoadSettings();

    private void BrowseRmk()
    {
        using var dialog = new FolderBrowserDialog { Description = "RMK Git 작업 클론을 선택하세요.", SelectedPath = Directory.Exists(rmkRoot.Text) ? rmkRoot.Text : string.Empty };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) rmkRoot.Text = dialog.SelectedPath;
    }

    private void BrowseGlossary()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "추가 용어집 선택",
            Filter = "용어집 파일 (*.txt;*.tsv;*.csv;*.json)|*.txt;*.tsv;*.csv;*.json|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
            FileName = File.Exists(customGlossary.Text) ? customGlossary.Text : string.Empty
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) customGlossary.Text = dialog.FileName;
    }

    private static Panel CreateCard(string title)
    {
        var panel = new BufferedPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(22), Margin = new Padding(0, 0, 18, 0), BorderStyle = BorderStyle.FixedSingle, Tag = "surface" };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 1 };
        content.Controls.Add(Ui.Label(title, 12f, FontStyle.Bold));
        panel.Controls.Add(content);
        return panel;
    }

    private static void AddField(TableLayoutPanel panel, string label, Control control)
    {
        panel.Controls.Add(Ui.Label(label, 8.5f, FontStyle.Bold));
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 2, 0, 13);
        panel.Controls.Add(control);
    }

    private static ComboBox NewChoice(IEnumerable<string> values)
    {
        var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var value in values) combo.Items.Add(value);
        return combo;
    }
}
