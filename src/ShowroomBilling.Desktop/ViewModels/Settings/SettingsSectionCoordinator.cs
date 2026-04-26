using System.Collections.ObjectModel;
using ShowroomBilling.Desktop.ViewModels.Admin;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal interface ISettingsSectionHost
{
    ObservableCollection<SettingsSectionKey> Sections { get; }
    SettingsSectionKey SelectedSection { get; set; }
    PrintLayoutViewModel PrintLayout { get; }
    SettingsPreviewViewModel Preview { get; }
    void NotifySectionPropertiesChanged();
}

internal sealed class SettingsSectionCoordinator(ISettingsSectionHost host)
{
    private bool _printLayoutLoaded;

    public bool IsConnectionVisible => host.SelectedSection == SettingsSectionKey.Connection;
    public bool IsDatabaseVisible => host.SelectedSection == SettingsSectionKey.Database;
    public bool IsInvoiceVisible => host.SelectedSection == SettingsSectionKey.Invoice;
    public bool IsPrintLayoutVisible => host.SelectedSection == SettingsSectionKey.PrintLayout;
    public bool IsLedgersVisible => host.SelectedSection == SettingsSectionKey.Ledgers;
    public bool IsMastersVisible => host.SelectedSection == SettingsSectionKey.Masters;
    public bool IsAdvancedVisible => host.SelectedSection == SettingsSectionKey.Advanced;
    public bool IsAdminVisible => host.SelectedSection == SettingsSectionKey.Admin;
    public bool IsPreviewVisible => IsInvoiceVisible || IsPrintLayoutVisible;

    public void OnSelectedSectionChanged()
    {
        host.NotifySectionPropertiesChanged();

        if (IsPreviewVisible && !_printLayoutLoaded && !host.PrintLayout.IsBusy)
        {
            _printLayoutLoaded = true;
            _ = host.PrintLayout.LoadAsync();
        }

        UpdatePreviewActivation();
    }

    public void SyncAdminSection(AdminUnlockViewModel? adminVm)
    {
        var unlocked = adminVm?.IsUnlocked == true;
        var present = host.Sections.Contains(SettingsSectionKey.Admin);
        if (unlocked && !present)
        {
            host.Sections.Add(SettingsSectionKey.Admin);
        }
        else if (!unlocked && present)
        {
            host.Sections.Remove(SettingsSectionKey.Admin);
            if (host.SelectedSection == SettingsSectionKey.Admin)
            {
                host.SelectedSection = SettingsSectionKey.Database;
            }
        }
    }

    public void UpdatePreviewActivation()
    {
        if (host.Preview is { } preview)
        {
            preview.SetActive(IsPreviewVisible);
        }
    }
}
