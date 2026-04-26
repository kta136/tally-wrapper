using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Leases;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Admin;

public partial class AdminUnlockViewModel : ObservableObject
{
    private readonly IAdminApiClient _adminApi;
    private readonly IDraftLeaseApiClient _leaseApi;
    private readonly AdminTokenStore _tokenStore;

    public AdminUnlockViewModel(
        IAdminApiClient adminApi,
        IDraftLeaseApiClient leaseApi,
        AdminTokenStore tokenStore)
    {
        _adminApi = adminApi;
        _leaseApi = leaseApi;
        _tokenStore = tokenStore;
        ActiveLeases = new ObservableCollection<DraftLeaseResponse>();

        LoadStatusCommand = new AsyncRelayCommand(LoadStatusAsync);
        UnlockCommand = new AsyncRelayCommand(UnlockAsync, () => !IsBusy);
        LockCommand = new AsyncRelayCommand(LockAsync);
        SetOrChangePasscodeCommand = new AsyncRelayCommand(SetOrChangePasscodeAsync, () => !IsBusy);
        RefreshLeasesCommand = new AsyncRelayCommand(RefreshLeasesAsync, () => _tokenStore.IsUnlocked);
        ForceReleaseSelectedLeaseCommand = new AsyncRelayCommand(ForceReleaseSelectedAsync,
            () => _tokenStore.IsUnlocked && SelectedLease is not null);

        _tokenStore.Changed += _ =>
        {
            OnPropertyChanged(nameof(IsUnlocked));
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(ShowUnlockForm));
            OnPropertyChanged(nameof(ShowInitialSetupForm));
            OnPropertyChanged(nameof(SessionSummary));
            RefreshLeasesCommand.NotifyCanExecuteChanged();
            ForceReleaseSelectedLeaseCommand.NotifyCanExecuteChanged();
        };
    }

    [ObservableProperty]
    private bool isPasscodeConfigured;

    partial void OnIsPasscodeConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(PasscodeSectionTitle));
        OnPropertyChanged(nameof(ShowUnlockForm));
        OnPropertyChanged(nameof(ShowInitialSetupForm));
    }

    [ObservableProperty]
    private string currentPasscode = string.Empty;

    [ObservableProperty]
    private string newPasscode = string.Empty;

    [ObservableProperty]
    private string confirmNewPasscode = string.Empty;

    [ObservableProperty]
    private string passcode = string.Empty;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public bool HasErrorMessage => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasErrorMessage));

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private DraftLeaseResponse? selectedLease;

    [ObservableProperty]
    private string forceReleaseReason = string.Empty;

    public ObservableCollection<DraftLeaseResponse> ActiveLeases { get; }

    public bool IsUnlocked => _tokenStore.IsUnlocked;

    public bool IsLocked => !_tokenStore.IsUnlocked;

    public bool ShowUnlockForm => !_tokenStore.IsUnlocked && IsPasscodeConfigured;

    public bool ShowInitialSetupForm => !_tokenStore.IsUnlocked && !IsPasscodeConfigured;

    public string PasscodeSectionTitle => IsPasscodeConfigured ? "Change passcode" : "Set passcode";

    public string SessionSummary
    {
        get
        {
            var session = _tokenStore.Current;
            if (session is null) return string.Empty;
            var remaining = session.ExpiresAtUtc - DateTimeOffset.UtcNow;
            var minutes = Math.Max(0, (int)remaining.TotalMinutes);
            return $"Unlocked as '{session.ActorLabel}' · expires in {minutes} min";
        }
    }

    public IAsyncRelayCommand LoadStatusCommand { get; }
    public IAsyncRelayCommand UnlockCommand { get; }
    public IAsyncRelayCommand LockCommand { get; }
    public IAsyncRelayCommand SetOrChangePasscodeCommand { get; }
    public IAsyncRelayCommand RefreshLeasesCommand { get; }
    public IAsyncRelayCommand ForceReleaseSelectedLeaseCommand { get; }

    partial void OnIsBusyChanged(bool value)
    {
        UnlockCommand.NotifyCanExecuteChanged();
        SetOrChangePasscodeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLeaseChanged(DraftLeaseResponse? value) =>
        ForceReleaseSelectedLeaseCommand.NotifyCanExecuteChanged();

    public async Task LoadStatusAsync()
    {
        try
        {
            var status = await _adminApi.GetPasscodeStatusAsync();
            IsPasscodeConfigured = status.IsConfigured;
            StatusMessage = status.IsConfigured ? null : "No admin passcode is configured — set one to enable admin unlock.";
            ErrorMessage = null;
            if (_tokenStore.IsUnlocked)
            {
                await RefreshLeasesAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not load admin status: {ex.Message}";
        }
    }

    private async Task UnlockAsync()
    {
        if (string.IsNullOrWhiteSpace(Passcode))
        {
            ErrorMessage = "Enter the passcode to unlock.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var response = await _adminApi.UnlockAsync(new AdminUnlockRequest(Passcode, Environment.UserName));
            _tokenStore.Set(response);
            Passcode = string.Empty;
            StatusMessage = "Admin session unlocked.";
            await RefreshLeasesAsync();
        }
        catch (AdminUnlockFailedException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unlock failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LockAsync()
    {
        var current = _tokenStore.Current;
        if (current is null) return;
        try
        {
            await _adminApi.LogoutAsync(new AdminLogoutRequest(current.Token));
        }
        catch
        {
            // even if remote logout fails, forget local token
        }
        _tokenStore.Clear();
        ActiveLeases.Clear();
        StatusMessage = "Admin session ended.";
    }

    private async Task SetOrChangePasscodeAsync()
    {
        var isInitialSetup = !IsPasscodeConfigured;
        if (string.IsNullOrEmpty(NewPasscode))
        {
            ErrorMessage = "Enter a new passcode.";
            return;
        }
        if (NewPasscode.Length < 6)
        {
            ErrorMessage = $"New passcode must be at least 6 characters (you entered {NewPasscode.Length}).";
            return;
        }
        if (!string.Equals(NewPasscode, ConfirmNewPasscode, StringComparison.Ordinal))
        {
            ErrorMessage = "New passcode and confirmation do not match.";
            return;
        }
        if (IsPasscodeConfigured && string.IsNullOrEmpty(CurrentPasscode))
        {
            ErrorMessage = "Enter the current passcode to change it.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _adminApi.SetPasscodeAsync(new AdminSetPasscodeRequest(
                IsPasscodeConfigured ? CurrentPasscode : null,
                NewPasscode));
            StatusMessage = IsPasscodeConfigured ? "Passcode updated." : "Passcode set.";
            IsPasscodeConfigured = true;
            if (isInitialSetup)
            {
                var unlock = await _adminApi.UnlockAsync(new AdminUnlockRequest(NewPasscode, Environment.UserName));
                _tokenStore.Set(unlock);
                StatusMessage = "Passcode set and admin session unlocked.";
                await RefreshLeasesAsync();
            }
            CurrentPasscode = string.Empty;
            NewPasscode = string.Empty;
            ConfirmNewPasscode = string.Empty;
        }
        catch (AdminUnlockFailedException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not update passcode: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshLeasesAsync()
    {
        var current = _tokenStore.Current;
        if (current is null) return;
        try
        {
            var response = await _leaseApi.ListActiveAsync(current.Token);
            ActiveLeases.Clear();
            foreach (var lease in response.Leases)
            {
                ActiveLeases.Add(lease);
            }
            if (response.Leases.Count == 0)
            {
                StatusMessage = "No active draft locks.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not load active leases: {ex.Message}";
        }
    }

    private async Task ForceReleaseSelectedAsync()
    {
        var current = _tokenStore.Current;
        var selected = SelectedLease;
        if (current is null || selected is null) return;
        if (string.IsNullOrWhiteSpace(ForceReleaseReason))
        {
            ErrorMessage = "Enter a reason before force-releasing.";
            return;
        }

        try
        {
            await _leaseApi.ForceReleaseAsync(
                selected.Id,
                new DraftLeaseForceReleaseRequest(current.ActorLabel, ForceReleaseReason),
                current.Token);
            StatusMessage = $"Lease on bill {selected.BillId} released.";
            ForceReleaseReason = string.Empty;
            await RefreshLeasesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Force-release failed: {ex.Message}";
        }
    }
}
