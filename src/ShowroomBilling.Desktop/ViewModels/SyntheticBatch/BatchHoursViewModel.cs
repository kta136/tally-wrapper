using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

/// <summary>A choice in "Closed on": a weekday, or none.</summary>
public sealed record ClosedDayOption(string Label, DayOfWeek? Day);

/// <summary>
/// "Shop hours for a batch" in Settings → Admin: when the shop opens and closes, the weekday it is
/// closed, and the fewest minutes between two bills (owner, 21 November 2026). They are shared
/// settings, saved on the server with this card's own button, and a batch keeps to them.
/// </summary>
public partial class BatchHoursViewModel : ObservableObject
{
    private readonly ISyntheticBatchApiClient _batchApi;
    private readonly IDialogService _dialogs;
    private readonly AdminTokenStore? _adminTokens;

    /// <summary>Fires with the hours as the server now holds them: after a load and after a save.</summary>
    public event EventHandler<BatchHoursSettings>? Changed;

    public BatchHoursViewModel(ISyntheticBatchApiClient batchApi, IDialogService dialogs, AdminTokenStore? adminTokens)
    {
        _batchApi = batchApi;
        _dialogs = dialogs;
        _adminTokens = adminTokens;
    }

    private static readonly ClosedDayOption OpenEveryDay = new("None, open every day", null);

    public IReadOnlyList<ClosedDayOption> ClosedDayOptions { get; } =
    [
        OpenEveryDay,
        .. new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        }.Select(day => new ClosedDayOption(day.ToString(), day)),
    ];

    [ObservableProperty] private string opensText = string.Empty;
    [ObservableProperty] private string closesText = string.Empty;
    [ObservableProperty] private ClosedDayOption closedDay = OpenEveryDay;
    [ObservableProperty] private string gapText = string.Empty;

    /// <summary>
    /// True once the hours have been read from the server. Until then the boxes do not hold what
    /// is saved, and saving them would wipe it: an admin who changed only the gap would clear the
    /// opening and closing time for every counter. So nothing is saved before a read has worked.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsReading))]
    private bool isLoaded;

    /// <summary>True while the hours are being read or saved: the boxes take no keys, so nothing typed is written over.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ReadAgainCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsReading))]
    private bool isBusy;

    /// <summary>The read failed: "Read again" is offered in place of saving.</summary>
    public bool NeedsReading => !IsLoaded && !IsBusy;

    /// <summary>What the last load or save came to; "ok" or "err" in <see cref="NoticeTone"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string notice = string.Empty;

    [ObservableProperty] private string noticeTone = "ok";

    public bool HasNotice => Notice.Length > 0;

    /// <summary>Reads the hours from the server into the boxes. A failure is said on the card and leaves the boxes as they are.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        // Until this read has worked the boxes are not known to hold what is saved: another
        // counter may have changed the hours since the last read.
        IsLoaded = false;
        try
        {
            var hours = (await _batchApi.GetBatchHoursAsync(cancellationToken)).Hours;
            Show(hours);
            IsLoaded = true;
            Say(string.Empty, "ok");
            Changed?.Invoke(this, hours);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Say($"The shop hours could not be read, so they cannot be changed yet. {ex.Message}", "err");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanReadAgain() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReadAgain))]
    private Task ReadAgainAsync() => LoadAsync();

    private bool CanSave() => IsLoaded && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave()) return;
        if (!TryRead(out var hours, out var problem))
        {
            Say(problem, "err");
            return;
        }

        IsBusy = true;
        try
        {
            var token = await AdminTokenPrompt.GetAsync(_adminTokens, _dialogs.RequestAdminUnlockAsync, CancellationToken.None);

            if (string.IsNullOrWhiteSpace(token))
            {
                Say("Unlock admin to save the shop hours.", "err");
                return;
            }

            var saved = (await _batchApi.SaveBatchHoursAsync(new UpdateBatchHoursRequest(hours), token)).Hours;
            Show(saved);
            Say("Shop hours saved.", "ok");
            Changed?.Invoke(this, saved);
        }
        catch (ApiException ex)
        {
            // The server said no: nothing was changed.
            Say($"The shop hours were not saved. {ex.Message}", "err");
        }
        catch (Exception)
        {
            // No answer: the save may have gone through. The hours are read back, so the boxes
            // hold what the server holds, whichever that is.
            IsBusy = false;
            await LoadAsync();
            if (IsLoaded) Say("The answer to the save was lost. These are the shop hours the server holds now.", "err");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>What the boxes say, or what is wrong with them, in the order the card shows them.</summary>
    internal bool TryRead(out BatchHoursSettings hours, out string problem)
    {
        hours = new BatchHoursSettings();
        problem = string.Empty;

        TimeOnly? opens = null;
        TimeOnly? closes = null;
        var hasOpens = !string.IsNullOrWhiteSpace(OpensText);
        var hasCloses = !string.IsNullOrWhiteSpace(ClosesText);
        if (hasOpens != hasCloses)
        {
            problem = "Type both times, or leave both empty to date bills at any time of day.";
            return false;
        }

        if (hasOpens)
        {
            if (!BatchClock.TryParse(OpensText, out var open) || !BatchClock.TryParse(ClosesText, out var close))
            {
                problem = "Type the times as hours and minutes on the 24-hour clock, such as 10:00.";
                return false;
            }

            if (close <= open)
            {
                problem = "The closing time must be later than the opening time.";
                return false;
            }

            (opens, closes) = (open, close);
        }

        var gap = 0;
        if (!string.IsNullOrWhiteSpace(GapText)
            && (!int.TryParse(GapText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out gap) || gap > BatchHoursLimits.MaxGapMinutes))
        {
            problem = $"The minutes between bills must be a whole number from 0 to {BatchHoursLimits.MaxGapMinutes}.";
            return false;
        }

        hours = new BatchHoursSettings(opens, closes, ClosedDay.Day, gap);
        return true;
    }

    private void Show(BatchHoursSettings hours)
    {
        OpensText = hours.Opens is { } opens ? BatchClock.Format(opens) : string.Empty;
        ClosesText = hours.Closes is { } closes ? BatchClock.Format(closes) : string.Empty;
        ClosedDay = ClosedDayOptions.FirstOrDefault(option => option.Day == hours.ClosedOn) ?? OpenEveryDay;
        GapText = hours.GapMinutes > 0 ? hours.GapMinutes.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private void Say(string text, string tone)
    {
        NoticeTone = tone;
        Notice = text;
    }
}
