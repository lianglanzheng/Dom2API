using System.Windows;
using System.Windows.Controls;
using Dom2API.Models;

namespace Dom2API;

public partial class MainWindow
{
    private bool changingAccountPreset;

    private async void AccountPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingsReady || changingAccountPreset || !viewModel.CanManageAccounts
            || AccountPresetBox.SelectedItem is not AccountPreset preset) return;
        await ApplyAccountPresetAsync(preset);
    }

    private async void RestoreAccountPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanManageAccounts || viewModel.SelectedAccountPreset is not { } preset) return;
        await ApplyAccountPresetAsync(preset);
    }

    /// <summary>先验证完整快照，再一次性更新服务，避免逐个切换时暴露中间状态。</summary>
    private async Task ApplyAccountPresetAsync(AccountPreset preset)
    {
        var accounts = viewModel.Upstreams.Select(row => row.Account with
            { IsEnabled = preset.IsAccountEnabled(row.Account.Id) }).ToArray();
        if (service.IsRunning)
        {
            try { _ = new ProxySettings(viewModel.Port, viewModel.TimeoutSeconds, viewModel.CooldownMinutes, accounts, requireEnabled: false); }
            catch (ArgumentException exception)
            {
                viewModel.ServiceError = exception.Message;
                viewModel.RefreshAccountPresetState();
                return;
            }
        }
        for (int i = 0; i < accounts.Length; i++) viewModel.Upstreams[i].Replace(accounts[i]);
        await ApplyAccountsAsync();
    }

    private void CreateAccountPreset_Click(object sender, RoutedEventArgs e) => EditAccountPreset(null);

    private void EditAccountPreset_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedAccountPreset is { } preset) EditAccountPreset(preset);
    }

    private void EditAccountPreset(AccountPreset? preset)
    {
        if (!viewModel.CanManageAccounts) return;
        var dialog = new AccountPresetEditorWindow(preset, viewModel.AccountPresets,
            viewModel.Upstreams.Count(row => row.IsEnabled), viewModel.Upstreams.Count) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var saved = (preset ?? new AccountPreset()) with
        {
            Name = dialog.PresetName,
            AccountStates = dialog.UpdateAccountStates
                ? viewModel.Upstreams.ToDictionary(row => row.Account.Id, row => row.IsEnabled)
                : new Dictionary<string, bool>(preset!.AccountStates)
        };
        // 替换集合项会触发 SelectionChanged；保存预设不应重新应用或覆盖手动状态。
        changingAccountPreset = true;
        try
        {
            if (preset is null) viewModel.AccountPresets.Add(saved);
            else viewModel.AccountPresets[viewModel.AccountPresets.IndexOf(preset)] = saved;
            viewModel.SelectedAccountPreset = saved;
        }
        finally { changingAccountPreset = false; }
        SaveRememberedSettings();
    }

    private void DeleteAccountPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanManageAccounts || viewModel.SelectedAccountPreset is not { } preset) return;
        changingAccountPreset = true;
        try
        {
            viewModel.SelectedAccountPreset = null;
            viewModel.AccountPresets.Remove(preset);
        }
        finally { changingAccountPreset = false; }
        SaveRememberedSettings();
    }
}
