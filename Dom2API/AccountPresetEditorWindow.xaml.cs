using System.Windows;
using Dom2API.Models;

namespace Dom2API;

/// <summary>验证预设名称，并让用户明确选择是否替换启用快照。</summary>
public partial class AccountPresetEditorWindow : Window
{
    private readonly HashSet<string> otherNames;

    /// <summary>保存后的预设名称。</summary>
    public string PresetName { get; private set; } = "";

    /// <summary>是否使用当前账号状态覆盖快照。</summary>
    public bool UpdateAccountStates => UpdateStatesBox.IsChecked == true;

    /// <summary>创建预设编辑对话框。</summary>
    public AccountPresetEditorWindow(AccountPreset? preset, IEnumerable<AccountPreset> presets, int enabledCount, int totalCount)
    {
        InitializeComponent();
        otherNames = presets.Where(item => item.Id != preset?.Id)
            .Select(item => item.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Title = preset is null ? "新建账号预设" : "修改账号预设";
        NameBox.Text = preset?.Name ?? "";
        UpdateStatesBox.Visibility = preset is null ? Visibility.Collapsed : Visibility.Visible;
        SnapshotText.Text = $"当前共 {totalCount} 个账号，启用 {enabledCount} 个，禁用 {totalCount - enabledCount} 个。"
            + (preset is null ? "将以此状态创建预设。" : "取消勾选可仅修改名称。");
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0) { ErrorText.Text = "请输入预设名称。"; return; }
        if (otherNames.Contains(name)) { ErrorText.Text = "已存在同名预设，请使用其他名称。"; return; }
        PresetName = name;
        DialogResult = true;
    }
}
