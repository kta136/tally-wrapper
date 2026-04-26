using System.Net.Http;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal interface ISettingsEditWorkflowHost
{
    SettingsDraft Draft { get; }
    EffectiveCloudSettingsDto? Settings { get; set; }
    bool IsEditing { get; set; }
    bool IsDirty { get; set; }
    bool IsSaving { get; set; }
    string StatusMessage { get; set; }
    string SettingsSource { get; set; }
    string Summary { get; set; }
    DateTimeOffset? UpdatedAtUtc { get; set; }
}

internal sealed class SettingsEditWorkflow(
    ISettingsApiClient? settingsApi,
    ISettingsEditWorkflowHost host)
{
    public void BeginEdit()
    {
        if (host.Settings is null) return;
        host.Draft.LoadFrom(host.Settings);
        host.IsDirty = false;
        host.IsEditing = true;
        host.StatusMessage = "Editing — Save to persist, Discard to cancel.";
    }

    public void DiscardChanges()
    {
        if (host.Settings is null) return;
        host.Draft.LoadFrom(host.Settings);
        host.IsDirty = false;
        host.IsEditing = false;
        host.StatusMessage = "Changes discarded.";
    }

    public void MarkDirtyIfEditing()
    {
        if (host.IsEditing) host.IsDirty = true;
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (settingsApi is null || host.Settings is null) return;

        if (!host.Draft.TryBuildDto(host.Settings, out var dto, out var error))
        {
            host.StatusMessage = error;
            return;
        }

        host.IsSaving = true;
        host.StatusMessage = "Saving settings…";
        try
        {
            var response = await settingsApi.SaveEffectiveSettingsAsync(
                new UpdateEffectiveSettingsRequest(dto), cancellationToken);
            host.SettingsSource = response.SettingsSource;
            host.Summary = response.Summary;
            host.UpdatedAtUtc = response.UpdatedAtUtc;
            host.Settings = dto;
            host.Draft.LoadFrom(dto);
            host.IsDirty = false;
            host.IsEditing = false;
            host.StatusMessage = $"Saved · {response.SavedSections.Count} section(s)";
        }
        catch (HttpRequestException ex)
        {
            host.StatusMessage = $"Save failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            host.IsSaving = false;
        }
    }
}
