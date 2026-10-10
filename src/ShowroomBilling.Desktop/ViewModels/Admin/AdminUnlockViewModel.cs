using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Admin;

public partial class AdminUnlockViewModel : ObservableObject
{
    private readonly IAdminApiClient _adminApi;
    private readonly AdminTokenStore _tokenStore;

    public AdminUnlockViewModel(
        IAdminApiClient adminApi,
        AdminTokenStore tokenStore)
    {
        _adminApi = adminApi;
        _tokenStore = tokenStore;

        LoadStatusCommand = new AsyncRelayCommand(LoadStatusAsync);
        UnlockCommand = new AsyncRelayCommand(UnlockAsync, () => !IsBusy);
        LockCommand = new AsyncRelayCommand(LockAsync);
        SetOrChangePasscodeCommand = new AsyncRelayCommand(SetOrChangePasscodeAsync, () => !IsBusy);

        _tokenStore.Changed += _ =>
        {
            OnPropertyChanged(nameof(IsUnlocked));
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(ShowUnlockForm));
            OnPropertyChanged(nameof(ShowInitialSetupForm));
            OnPropertyChanged(nameof(SessionSummary));
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

    partial void OnIsBusyChanged(bool value)
    {
        UnlockCommand.NotifyCanExecuteChanged();
        SetOrChangePasscodeCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadStatusAsync()
    {
        try
        {
            var status = await _adminApi.GetPasscodeStatusAsync();
            IsPasscodeConfigured = status.IsConfigured;
            StatusMessage = status.IsConfigured ? null : "No admin passcode is configured — set one to enable admin unlock.";
            ErrorMessage = null;
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
        if (NewPasscode.Length < SettingLimits.MinAdminPasscodeLength)
        {
            ErrorMessage = $"New passcode must be at least {SettingLimits.MinAdminPasscodeLength} characters (you entered {NewPasscode.Length}).";
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
            await _adminApi.SetPasscodeAsync(
                new AdminSetPasscodeRequest(
                    IsPasscodeConfigured ? CurrentPasscode : null,
                    NewPasscode),
                IsPasscodeConfigured ? _tokenStore.Current?.Token : null);
            StatusMessage = IsPasscodeConfigured ? "Passcode updated." : "Passcode set.";
            IsPasscodeConfigured = true;
            if (isInitialSetup)
            {
                var unlock = await _adminApi.UnlockAsync(new AdminUnlockRequest(NewPasscode, Environment.UserName));
                _tokenStore.Set(unlock);
                StatusMessage = "Passcode set and admin session unlocked.";
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
}
