using System.Windows;
using System.Windows.Controls;

namespace OllamaToolkit.App.Services;

public sealed class CodingToolRowControls
{
    public required OllamaLaunchIntegrations.Integration Integration { get; init; }

    public required ComboBox ActionCombo { get; init; }

    public required ComboBox YesCombo { get; init; }

    public required ComboBox ExtraArgsCombo { get; init; }

    public required TextBox CustomExtraArgsBox { get; init; }

    public required Button RunButton { get; init; }

    public CodingToolLaunchArgsBuilder.Request ToRequest(string? model) =>
        new(
            Integration.Id,
            ActionCombo.SelectedItem as string ?? OllamaLaunchIntegrations.ActionLaunch,
            YesCombo.SelectedItem as string ?? OllamaLaunchIntegrations.YesOff,
            model,
            ExtraArgsCombo.SelectedItem as string ?? OllamaLaunchIntegrations.ExtraNone,
            CustomExtraArgsBox.Text);

    public void SyncExtraArgsVisibility()
    {
        var isCustom = string.Equals(
            ExtraArgsCombo.SelectedItem as string,
            OllamaLaunchIntegrations.ExtraCustom,
            StringComparison.Ordinal);
        CustomExtraArgsBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SyncYesEnabled()
    {
        var isRestore = string.Equals(
            ActionCombo.SelectedItem as string,
            OllamaLaunchIntegrations.ActionRestore,
            StringComparison.Ordinal);
        YesCombo.IsEnabled = !isRestore;
    }
}