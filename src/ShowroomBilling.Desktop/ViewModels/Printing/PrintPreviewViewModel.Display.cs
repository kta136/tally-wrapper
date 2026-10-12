namespace ShowroomBilling.Desktop.ViewModels.Printing;

/// <summary>What the Print Preview dialog says about itself: whose bill, how many pages, what Print will do.</summary>
public partial class PrintPreviewViewModel
{
    private const string ReadyMessage = "Ready to print.";

    /// <summary>The line under the title: the party, the pages, and the promise that printing changes nothing.</summary>
    public string SubtitleText
    {
        get
        {
            var party = _contents.Count switch
            {
                0 => string.Empty,
                1 => string.IsNullOrWhiteSpace(_contents[0].PartyName) ? "Walk-in Customer" : _contents[0].PartyName!,
                var count => $"{count} bills",
            };
            var pages = PreviewPages.Count;
            var size = pages == 0 ? "A4" : $"{pages} page{(pages == 1 ? string.Empty : "s")}, A4";
            return string.Join(" · ", new[] { party, size, "Printing does not change the bill's state" }.Where(part => part.Length > 0));
        }
    }

    /// <summary>"Page 1 of 1" above the preview.</summary>
    public string PageCountText => PreviewPages.Count == 0 ? string.Empty : $"Page 1 of {PreviewPages.Count}";

    /// <summary>What pressing Print does: how many copies go to which printer.</summary>
    public string PrintSummary
    {
        get
        {
            if (!HasSelectedPrinter)
            {
                return "Choose a printer to print.";
            }

            var copies = ((CopyOriginal ? 1 : 0) + (CopyDuplicate ? 1 : 0) + (CopyTriplicate ? 1 : 0)) * Math.Max(1, DocumentCount);
            return $"{copies} cop{(copies == 1 ? "y" : "ies")} to {SelectedPrinter}";
        }
    }

    /// <summary>The foot of the dialog says what is happening now; when nothing is, what Print will do.</summary>
    public string FooterText => string.IsNullOrWhiteSpace(StatusMessage) || StatusMessage == ReadyMessage ? PrintSummary : StatusMessage;

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(FooterText));

    private void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(PrintSummary));
        OnPropertyChanged(nameof(FooterText));
    }
}
