using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using static ShowroomBilling.Printing.BillDocumentText;

namespace ShowroomBilling.Printing;

public sealed class BillDocument : IDocument
{
    private readonly PrintDocumentOptions _options;
    private readonly PrintLayoutOptions _layout;
    private readonly int _bodyFont;
    private readonly int _smallFont;
    private readonly int _xSmallFont;
    private readonly int _largeFont;
    private readonly int _xLargeFont;
    private readonly int _termsFont;
    private readonly float _densityScale;
    private readonly IReadOnlyList<PrintSectionLayoutOptions> _sections;
    private readonly string? _watermarkSvg;

    public BillDocument(PrintDocumentOptions options)
    {
        _options = options;
        _layout = options.Layout.Clamped();
        _bodyFont = _layout.InvoiceFontSize;
        _smallFont = Math.Max(PrintLayoutLimits.TermsFontMin, _bodyFont - 1);
        _xSmallFont = Math.Max(PrintLayoutLimits.TermsFontMin, _bodyFont - 2);
        _largeFont = _bodyFont + 2;
        _xLargeFont = _bodyFont + 3;
        _termsFont = _layout.TermsFontSize;
        _densityScale = _layout.DensityScale;
        _sections = _layout.Sections ?? Array.Empty<PrintSectionLayoutOptions>();
        _watermarkSvg = BuildWatermarkSvg(_layout);
    }

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"Tax Invoice {_options.Content.InvoiceNumber}",
        Author = _options.Content.Company.Name,
    };

    public void Compose(IDocumentContainer container)
    {
        var copies = _options.Copies.Count == 0 ? new[] { CopyLabel.Original } : _options.Copies;
        foreach (var copy in copies)
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginLeft(_layout.MarginLeftMm, Unit.Millimetre);
                page.MarginTop(_layout.MarginTopMm, Unit.Millimetre);
                page.MarginRight(_layout.MarginRightMm, Unit.Millimetre);
                page.MarginBottom(_layout.MarginBottomMm, Unit.Millimetre);
                page.DefaultTextStyle(ts => ts.FontSize(_bodyFont).FontFamily("Arial").FontColor(Colors.Black));

                if (_watermarkSvg is not null)
                {
                    page.Background()
                        .PaddingLeft(_layout.WatermarkOffsetXMm, Unit.Millimetre)
                        .PaddingTop(_layout.WatermarkOffsetYMm, Unit.Millimetre)
                        .AlignLeft()
                        .AlignTop()
                        .Width(_layout.WatermarkWidthMm, Unit.Millimetre)
                        .Height(_layout.WatermarkHeightMm, Unit.Millimetre)
                        .Svg(_watermarkSvg)
                        .FitArea();
                }

                page.Content().Element(c => ComposeCopy(c, copy));
            });
        }
    }

    private void ComposeCopy(IContainer container, CopyLabel copy)
    {
        var sections = _sections.Where(ShouldRenderSection).ToList();

        container.Column(pageCol =>
        {
            // Preserve the historical default: when the copy label is first, it sits
            // outside the invoice frame. If an operator deliberately moves it, it
            // participates in the configured in-frame section flow.
            if (sections.FirstOrDefault()?.SectionKey == PrintLayoutSectionKeys.CopyLabel)
            {
                var copySection = sections[0];
                pageCol.Item()
                    .PaddingTop(copySection.SpacingBeforeMm, Unit.Millimetre)
                    .PaddingBottom(copySection.SpacingAfterMm, Unit.Millimetre)
                    .Element(c => ComposeCopyLabel(c, copy));
                sections.RemoveAt(0);
            }

            // Main invoice box. ExtendVertical is
            // outermost so the bordered frame stretches to the A4 bottom; within the
            // box the configured trailing group uses ExtendVertical+AlignBottom to pin
            // itself to the page bottom.
            pageCol.Item().ExtendVertical()
                .Border(_layout.InvoiceBorderThicknessPt).BorderColor(Colors.Black)
                .PaddingTop(V(8)).PaddingHorizontal(10).PaddingBottom(V(10))
                .Column(box =>
            {
                var pinnedIndex = _layout.BottomPinnedFromSectionKey is null
                    ? -1
                    : sections.FindIndex(section =>
                        string.Equals(
                            section.SectionKey,
                            _layout.BottomPinnedFromSectionKey,
                            StringComparison.Ordinal));
                var flowing = pinnedIndex < 0 ? sections : sections.Take(pinnedIndex).ToList();
                var pinned = pinnedIndex < 0 ? [] : sections.Skip(pinnedIndex).ToList();

                if (flowing.Count > 0)
                {
                    box.Item().Column(top => ComposeSectionSequence(top, flowing, copy));
                }

                if (pinned.Count > 0)
                {
                    box.Item().ExtendVertical().AlignBottom()
                        .Column(bottom => ComposeSectionSequence(bottom, pinned, copy));
                }
            });
        });
    }

    private void ComposeSectionSequence(
        ColumnDescriptor column,
        IReadOnlyList<PrintSectionLayoutOptions> sections,
        CopyLabel copy)
    {
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var next = index + 1 < sections.Count ? sections[index + 1] : null;

            if (section.SectionKey == PrintLayoutSectionKeys.GstBreakup
                && next?.SectionKey == PrintLayoutSectionKeys.BankDetails)
            {
                AddSection(
                    column,
                    section.SpacingBeforeMm,
                    next.SpacingAfterMm,
                    ComposeGstAndBank);
                index++;
                continue;
            }

            if (section.SectionKey == PrintLayoutSectionKeys.Terms
                && next?.SectionKey == PrintLayoutSectionKeys.Signature)
            {
                AddSection(
                    column,
                    section.SpacingBeforeMm,
                    next.SpacingAfterMm,
                    ComposeFooter);
                index++;
                continue;
            }

            AddSection(
                column,
                section.SpacingBeforeMm,
                section.SpacingAfterMm,
                c => ComposeSection(c, section.SectionKey, copy));
        }
    }

    private static void AddSection(
        ColumnDescriptor column,
        float spacingBeforeMm,
        float spacingAfterMm,
        Action<IContainer> compose)
    {
        column.Item()
            .PaddingTop(spacingBeforeMm, Unit.Millimetre)
            .PaddingBottom(spacingAfterMm, Unit.Millimetre)
            .Element(compose);
    }

    private void ComposeSection(IContainer container, string sectionKey, CopyLabel copy)
    {
        switch (sectionKey)
        {
            case PrintLayoutSectionKeys.CopyLabel:
                ComposeCopyLabel(container, copy);
                break;
            case PrintLayoutSectionKeys.Logo:
                ComposeLogo(container);
                break;
            case PrintLayoutSectionKeys.InvoiceTitle:
                ComposeInvoiceTitle(container);
                break;
            case PrintLayoutSectionKeys.CompanyAndParty:
                ComposeCompanyAndParty(container);
                break;
            case PrintLayoutSectionKeys.Notes:
                ComposeNotes(container);
                break;
            case PrintLayoutSectionKeys.ItemsTable:
                ComposeLineItemsTable(container);
                break;
            case PrintLayoutSectionKeys.Totals:
                ComposeSummary(container);
                break;
            case PrintLayoutSectionKeys.GstBreakup:
                ComposeGstBreakup(container);
                break;
            case PrintLayoutSectionKeys.BankDetails:
                ComposeBankDetails(container);
                break;
            case PrintLayoutSectionKeys.Terms:
                ComposeTerms(container);
                break;
            case PrintLayoutSectionKeys.Signature:
                ComposeSignature(container);
                break;
        }
    }

    private bool ShouldRenderSection(PrintSectionLayoutOptions section)
    {
        if (!section.IsVisible) return false;
        return section.SectionKey switch
        {
            PrintLayoutSectionKeys.Logo => _layout.LogoBytes is { Length: > 0 },
            PrintLayoutSectionKeys.Notes => !string.IsNullOrWhiteSpace(_options.Content.Notes),
            PrintLayoutSectionKeys.BankDetails => _options.Content.Company.HasAnyBankField,
            PrintLayoutSectionKeys.Terms => !string.IsNullOrWhiteSpace(_options.Content.Company.TermsAndConditions),
            PrintLayoutSectionKeys.Signature => _layout.SignatureBytes is { Length: > 0 },
            _ => true,
        };
    }

    private void ComposeCopyLabel(IContainer container, CopyLabel copy)
    {
        container.AlignRight().Text(copy.ToDisplayText())
            .FontSize(_xSmallFont).SemiBold();
    }

    private void ComposeLogo(IContainer container)
    {
        container.AlignCenter()
            .Width(PrintLayoutLimits.LogoSlotWidthMm, Unit.Millimetre)
            .Height(PrintLayoutLimits.LogoSlotHeightMm, Unit.Millimetre)
            .Element(slot =>
            {
                if (_layout.LogoBytes is { Length: > 0 } logo)
                {
                    slot.PaddingLeft(_layout.LogoOffsetXMm, Unit.Millimetre)
                        .PaddingTop(_layout.LogoOffsetYMm, Unit.Millimetre)
                        .Width(_layout.LogoWidthMm, Unit.Millimetre)
                        .Height(_layout.LogoHeightMm, Unit.Millimetre)
                        .Image(logo).FitArea();
                }
            });
    }

    private void ComposeInvoiceTitle(IContainer container)
    {
        // TAX INVOICE banner with rules top/bottom (V1 .invoice-banner).
        container.PaddingTop(V(2))
                .BorderTop(1.5f).BorderBottom(1.5f).BorderColor(Colors.Black)
                .PaddingTop(V(5)).PaddingBottom(V(4))
                .AlignCenter()
                .Text("TAX INVOICE")
                .FontSize(_bodyFont).Bold().LetterSpacing(0.24f);
    }

    private void ComposeCompanyAndParty(IContainer container)
    {
        container.PaddingTop(V(6))
            .BorderTop(1f).BorderBottom(1f).BorderColor(Colors.Black)
            .PaddingVertical(V(6))
            .Row(row =>
        {
            row.RelativeItem(52).Column(c =>
            {
                c.Item().Text("Company Details").FontSize(_smallFont);
                c.Item().PaddingTop(V(2)).Text(_options.Content.Company.Name)
                    .FontSize(_largeFont).Bold();

                var company = _options.Content.Company;
                AppendKeyRow(c, "GSTIN", company.Gstin);
                AppendKeyRow(c, "Phone", company.Phone);
                AppendKeyRow(c, "Address", company.Address);
            });

            row.ConstantItem(10);

            row.RelativeItem(48).BorderLeft(1f).BorderColor(Colors.Black).PaddingLeft(16).Column(c =>
            {
                if (!string.IsNullOrWhiteSpace(_options.Content.InvoiceNumber))
                {
                    AppendMetaRow(c, "Invoice No.", _options.Content.InvoiceNumber);
                }
                AppendMetaRow(c, "Date", _options.Content.BillDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));

                c.Item().PaddingVertical(V(5)).LineHorizontal(1f).LineColor(Colors.Black);

                // V1 parity: when the operator leaves Party blank, the headline falls
                // back to the payment-mode label (V1's sales_tab auto-defaulted Party
                // to "Cash" / "Credit and Debit"). When the operator typed a real
                // customer name, V1 lost the payment-mode signal on print; the current app keeps
                // it as a small "Payment: …" line below.
                var partyText = _options.Content.PartyName?.Trim();
                var paymentLabel = string.IsNullOrWhiteSpace(_options.Content.Payment)
                    ? null
                    : PaymentMode.Normalize(_options.Content.Payment);
                var headline = string.IsNullOrEmpty(partyText)
                    ? (paymentLabel ?? "—")
                    : partyText;
                var showPaymentLine = !string.IsNullOrEmpty(partyText)
                    && paymentLabel is not null;

                c.Item().Text("Bill To").FontSize(_smallFont);
                c.Item().PaddingTop(V(2)).Text(headline)
                    .FontSize(_xLargeFont).Bold();

                if (!string.IsNullOrWhiteSpace(_options.Content.PartyAddress))
                {
                    c.Item().PaddingTop(V(3)).Text(_options.Content.PartyAddress).FontSize(_smallFont);
                }
                if (!string.IsNullOrWhiteSpace(_options.Content.PartyGstin))
                {
                    c.Item().PaddingTop(V(1)).Text($"GSTIN {_options.Content.PartyGstin}").FontSize(_smallFont);
                }
                if (!string.IsNullOrWhiteSpace(_options.Content.PartyPhone))
                {
                    c.Item().PaddingTop(V(1)).Text($"Ph {_options.Content.PartyPhone}").FontSize(_smallFont);
                }
                if (showPaymentLine)
                {
                    c.Item().PaddingTop(V(3)).Text($"Payment: {paymentLabel}").FontSize(_smallFont);
                }
            });
        });
    }

    private void ComposeNotes(IContainer container)
    {
        container.Border(1f).BorderColor(Colors.Black)
            .PaddingVertical(V(4)).PaddingHorizontal(6)
            .Column(column =>
            {
                column.Item().Text("NOTES").FontSize(_xSmallFont).SemiBold().LetterSpacing(0.05f);
                column.Item().PaddingTop(V(2)).Text(_options.Content.Notes?.Trim() ?? string.Empty)
                    .FontSize(_smallFont);
            });
    }

    private void AppendKeyRow(ColumnDescriptor column, string label, string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        column.Item().PaddingTop(V(2)).Row(r =>
        {
            r.ConstantItem(60).Text(label).FontSize(_bodyFont);
            r.RelativeItem().Text(text).FontSize(_bodyFont);
        });
    }

    private void AppendMetaRow(ColumnDescriptor column, string label, string value)
    {
        column.Item().PaddingVertical(V(1)).Row(r =>
        {
            r.ConstantItem(80).Text(label).FontSize(_bodyFont);
            r.RelativeItem().AlignRight().Text(value).FontSize(_bodyFont).Bold();
        });
    }

    private void ComposeLineItemsTable(IContainer container)
    {
        var lines = _options.Content.Lines;
        var hasLessWt = lines.Any(l => !IsDiamond(l) && (l.LessWeight ?? 0m) != 0m);
        var hasExtra = lines.Any(l => (l.Extra ?? 0m) != 0m);

        container.PaddingTop(V(4)).Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(20);  // #
                cd.RelativeColumn(3);   // Description
                if (hasLessWt)
                {
                    cd.ConstantColumn(46); // Gross Wt
                    cd.ConstantColumn(44); // Less Wt
                }
                cd.ConstantColumn(48); // Net Wt
                cd.ConstantColumn(42); // Purity
                cd.ConstantColumn(64); // Making — enough for rare percentage+labour without reserving the old 78pt
                if (hasExtra)
                {
                    cd.ConstantColumn(44); // Extra Charges
                }
                cd.ConstantColumn(56); // Rate/g
                cd.ConstantColumn(76); // Amount — Description's RelativeColumn absorbs the delta from earlier baseline
            });

            table.Header(header =>
            {
                IContainer H(IContainer c, int font) => c.Border(1f).BorderColor(Colors.Black)
                    .PaddingVertical(V(2)).PaddingHorizontal(4)
                    .DefaultTextStyle(ts => ts.SemiBold().FontSize(font));

                void HeaderCell(string text, bool center = false, bool right = false)
                {
                    var cell = header.Cell().Element(c => H(c, _smallFont));
                    cell = center ? cell.AlignCenter() : right ? cell.AlignRight() : cell.AlignLeft();
                    cell.ScaleToFit().Text(t =>
                    {
                        t.Span(KeepOnOneLine(text)).FontSize(_xSmallFont);
                    });
                }

                HeaderCell("#", center: true);
                header.Cell().Element(c => H(c, _smallFont)).AlignLeft().Text("Description");
                if (hasLessWt)
                {
                    HeaderCell("Gross", right: true);
                    HeaderCell("Less", right: true);
                }
                HeaderCell("Net Wt", right: true);
                HeaderCell("Purity", right: true);
                HeaderCell("Making", right: true);
                if (hasExtra)
                {
                    HeaderCell("Extra", right: true);
                }
                HeaderCell("Rate/g", right: true);
                HeaderCell("Amount", right: true);
            });

            int idx = 1;
            foreach (var line in lines)
            {
                // Items-table cells use the header's _smallFont (body − 1) so 12-char
                // values like "₹1,46,359.75" don't wrap inside fixed-width Rate/g and
                // Amount columns at the default 11pt body. Headers were already on
                // _smallFont, so the table now reads at one consistent size.
                IContainer Cell(IContainer c) => c.Border(1f).BorderColor(Colors.Black)
                    .PaddingVertical(V(2)).PaddingHorizontal(4)
                    .DefaultTextStyle(ts => ts.FontSize(_smallFont));

                void ValueCell(string text, bool center = false, bool bold = false)
                {
                    var cell = table.Cell().Element(Cell);
                    cell = center ? cell.AlignCenter() : cell.AlignRight();
                    cell.ScaleToFit().Text(t =>
                    {
                        var span = t.Span(KeepOnOneLine(text)).FontSize(_xSmallFont);
                        if (bold) span.SemiBold();
                    });
                }

                var unit = string.IsNullOrWhiteSpace(line.Unit) ? "g" : line.Unit!.Trim();
                var grossWt = line.GrossWeight ?? 0m;
                var lessWt = line.LessWeight ?? 0m;
                var isDiamond = IsDiamond(line);

                ValueCell(idx.ToString(CultureInfo.InvariantCulture), center: true);
                table.Cell().Element(Cell).Text(t => t.Span(line.ItemName).SemiBold());

                if (hasLessWt)
                {
                    ValueCell(!isDiamond && grossWt > 0m ? FormatWeight(grossWt, unit) : "—");
                    ValueCell(!isDiamond && lessWt > 0m ? FormatWeight(lessWt, unit) : "—");
                }

                ValueCell(FormatWeight(line.Quantity, unit));
                ValueCell(isDiamond ? "—" : line.Karat ?? "—", center: true);
                ValueCell(FormatMaking(line));

                if (hasExtra)
                {
                    var extra = line.Extra ?? 0m;
                    ValueCell(extra != 0m ? FormatMoney(extra) : "—");
                }

                ValueCell(FormatMoney(line.Rate));
                ValueCell(FormatMoney(line.LineTotal), bold: true);
                idx++;
            }
        });
    }

    private static string KeepOnOneLine(string text) => text.Replace(" ", "\u00A0", StringComparison.Ordinal);

    private void ComposeSummary(IContainer container)
    {
        var totals = _options.Content.Totals;
        var itemsTotalIncl = _options.Content.Lines.Sum(l => l.LineTotal);

        container.PaddingTop(V(4)).Row(row =>
        {
            row.RelativeItem(52);
            row.RelativeItem(48).BorderTop(1f).BorderColor(Colors.Black).Column(c =>
            {
                c.Item().PaddingTop(V(3)).Row(r =>
                {
                    r.RelativeItem().Text("Items Total (Incl. GST)").FontSize(_bodyFont);
                    r.ConstantItem(110).AlignRight().Text(FormatMoney(itemsTotalIncl)).FontSize(_bodyFont);
                });

                if (totals.DiscountTotal != 0m)
                {
                    c.Item().PaddingTop(V(1)).Row(r =>
                    {
                        r.RelativeItem().Text("Discount").FontSize(_bodyFont);
                        r.ConstantItem(110).AlignRight().Text(FormatSigned(-totals.DiscountTotal)).FontSize(_bodyFont);
                    });
                }

                if (totals.RoundOff != 0m)
                {
                    c.Item().PaddingTop(V(1)).Row(r =>
                    {
                        r.RelativeItem().Text("Round Off").FontSize(_bodyFont);
                        r.ConstantItem(110).AlignRight().Text(FormatSigned(totals.RoundOff)).FontSize(_bodyFont);
                    });
                }

                c.Item().PaddingTop(V(3)).LineHorizontal(1f).LineColor(Colors.Black);
                c.Item().PaddingTop(V(3)).Row(r =>
                {
                    r.RelativeItem().Text("Total Amount (Incl. GST)").FontSize(_largeFont).Bold();
                    r.ConstantItem(110).AlignRight().Text(FormatMoney(totals.GrandTotal, decimals: 0))
                        .FontSize(_xLargeFont).Bold();
                });
                c.Item().PaddingTop(V(2)).LineHorizontal(2f).LineColor(Colors.Black);
            });
        });
    }

    private void ComposeGstAndBank(IContainer container)
    {
        container.PaddingTop(V(4)).Row(row =>
        {
            row.RelativeItem(60).Element(ComposeGstBreakup);

            row.ConstantItem(10);
            row.RelativeItem(40).Element(ComposeBankDetails);
        });
    }

    private void ComposeGstBreakup(IContainer container)
    {
        var totals = _options.Content.Totals;
        var gstTotal = totals.TaxTotal;
        var cgst = Math.Round(gstTotal / 2m, 2, MidpointRounding.AwayFromZero);
        var sgst = gstTotal - cgst;

        container.Border(1f).BorderColor(Colors.Black).Column(c =>
        {
            c.Item().BorderBottom(1f).BorderColor(Colors.Black)
                .PaddingVertical(V(2)).PaddingHorizontal(5)
                .Text("GST BREAKUP (INCLUDED IN TOTAL)")
                .FontSize(_smallFont).SemiBold();

            c.Item().Row(header =>
            {
                GstHeaderCell(header, "HSN Code");
                GstHeaderCell(header, "Taxable Value", width: 1.25f);
                GstHeaderCell(header, $"CGST @ {CgstRatePct:0.#}%");
                GstHeaderCell(header, $"SGST @ {SgstRatePct:0.#}%");
                GstHeaderCell(header, "Total GST", last: true);
            });

            c.Item().Row(values =>
            {
                GstValueCell(values, GetHsnCodeDisplay(), center: true);
                GstValueCell(values, FormatMoney(totals.Subtotal), width: 1.25f);
                GstValueCell(values, FormatMoney(cgst));
                GstValueCell(values, FormatMoney(sgst));
                GstValueCell(values, FormatMoney(gstTotal), last: true, bold: true);
            });
        });
    }

    private void ComposeBankDetails(IContainer container)
    {
        container.Border(1f).BorderColor(Colors.Black)
            .PaddingVertical(V(6)).PaddingHorizontal(6).Column(c =>
        {
            c.Item().Text("BANK DETAILS").FontSize(_xSmallFont).SemiBold().LetterSpacing(0.05f);
            AppendBankRow(c, "Bank Name", _options.Content.Company.BankName);
            AppendBankRow(c, "Account No.", _options.Content.Company.BankAccount);
            AppendBankRow(c, "IFSC Code", _options.Content.Company.BankIfsc);
            AppendBankRow(c, "UPI ID", _options.Content.Company.BankUpi);
        });
    }

    private string GetHsnCodeDisplay()
    {
        var codes = _options.Content.Lines
            .Select(line => string.IsNullOrWhiteSpace(line.HsnCode) ? DefaultHsnCode : line.HsnCode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return codes.Length == 0 ? DefaultHsnCode : string.Join(", ", codes);
    }

    private void GstHeaderCell(RowDescriptor row, string text, bool last = false, float width = 1f)
    {
        // Border must precede Padding in the chain so the 1pt line sits at the cell
        // edge (outside the padding), matching V1's border-collapse grid appearance.
        var cell = row.RelativeItem(width).BorderBottom(1f).BorderColor(Colors.Black);
        if (!last) cell = cell.BorderRight(1f);
        cell.PaddingVertical(V(2)).PaddingHorizontal(5).Text(text).FontSize(_xSmallFont).SemiBold();
    }

    private void GstValueCell(
        RowDescriptor row,
        string text,
        bool last = false,
        bool bold = false,
        float width = 1f,
        bool center = false)
    {
        var cell = row.RelativeItem(width);
        if (!last) cell = cell.BorderRight(1f).BorderColor(Colors.Black);
        var content = cell.PaddingVertical(V(3)).PaddingHorizontal(5);
        content = center ? content.AlignCenter() : content.AlignRight();
        content.Text(t =>
        {
            var span = t.Span(text).FontSize(_smallFont);
            if (bold) span.SemiBold();
        });
    }

    private void AppendBankRow(ColumnDescriptor column, string label, string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        column.Item().PaddingTop(V(2)).Row(r =>
        {
            r.ConstantItem(72).Text(label).FontSize(_xSmallFont).FontColor(Colors.Grey.Darken2);
            r.RelativeItem().Text(text).FontSize(_xSmallFont).SemiBold();
        });
    }

    private void ComposeFooter(IContainer container)
    {
        container.PaddingTop(V(4)).BorderTop(1f).BorderColor(Colors.Black).PaddingTop(V(4))
            .Row(row =>
        {
            row.RelativeItem(60).Element(ComposeTermsContent);

            row.ConstantItem(10);
            row.RelativeItem(40).AlignBottom().Element(ComposeSignatureContent);
        });
    }

    private void ComposeTerms(IContainer container)
    {
        container.BorderTop(1f).BorderColor(Colors.Black)
            .PaddingTop(V(4))
            .Element(ComposeTermsContent);
    }

    private void ComposeTermsContent(IContainer container)
    {
        container.Column(c =>
        {
            var terms = _options.Content.Company.TermsAndConditions;
            if (!string.IsNullOrWhiteSpace(terms))
            {
                c.Item().Text("Terms & Conditions").FontSize(_smallFont).SemiBold();
                var lines = terms.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    c.Item().PaddingTop(V(2)).Text(line.Trim())
                        .FontSize(_termsFont).LineHeight(1.45f);
                }
            }
        });
    }

    private void ComposeSignature(IContainer container)
    {
        container.BorderTop(1f).BorderColor(Colors.Black)
            .PaddingTop(V(4))
            .Element(ComposeSignatureContent);
    }

    private void ComposeSignatureContent(IContainer container)
    {
        container.Column(c =>
        {
            c.Item().AlignCenter().Text($"For {_options.Content.Company.Name}")
                .FontSize(_smallFont);

            c.Item().AlignCenter()
                .Width(PrintLayoutLimits.SignatureSlotWidthMm, Unit.Millimetre)
                .Height(PrintLayoutLimits.SignatureSlotHeightMm, Unit.Millimetre)
                .Element(slot =>
                {
                    if (_layout.SignatureBytes is { Length: > 0 } sig)
                    {
                        // Cap offset at render time so QuestPDF never sees padding + width > slot.
                        // Stored values can be larger (up to SlotSize) so they survive image resizes.
                        var offX = Math.Max(0f, Math.Min(_layout.SignatureOffsetXMm,
                            PrintLayoutLimits.SignatureSlotWidthMm - _layout.SignatureWidthMm));
                        var offY = Math.Max(0f, Math.Min(_layout.SignatureOffsetYMm,
                            PrintLayoutLimits.SignatureSlotHeightMm - _layout.SignatureHeightMm));
                        slot.PaddingLeft(offX, Unit.Millimetre)
                            .PaddingTop(offY, Unit.Millimetre)
                            .Width(_layout.SignatureWidthMm, Unit.Millimetre)
                            .Height(_layout.SignatureHeightMm, Unit.Millimetre)
                            .Image(sig).FitArea();
                    }
                });

            // Keep the signing rule just wider than the configured image instead
            // of stretching it across the entire signature slot.
            c.Item().AlignCenter()
                .Width(
                    PrintLayoutLimits.GetSignatureLineWidthMm(_layout.SignatureWidthMm),
                    Unit.Millimetre)
                .BorderTop(1f).BorderColor(Colors.Black)
                .PaddingTop(V(2)).AlignCenter().Text("Authorised Signatory").FontSize(_xSmallFont);
        });
    }

    private float V(float value) => value * _densityScale;

    private static string? BuildWatermarkSvg(PrintLayoutOptions layout)
    {
        if (layout.WatermarkBytes is not { Length: > 0 } bytes || layout.WatermarkOpacity <= 0f)
        {
            return null;
        }

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var mediaType = bytes.AsSpan().StartsWith(pngSignature)
                ? "image/png"
                : bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
                    ? "image/jpeg"
                    : null;
        if (mediaType is null)
        {
            return null;
        }

        var opacity = layout.WatermarkOpacity.ToString("0.###", CultureInfo.InvariantCulture);
        var base64 = Convert.ToBase64String(bytes);
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg"
                 xmlns:xlink="http://www.w3.org/1999/xlink"
                 viewBox="0 0 1000 1000">
              <g opacity="{opacity}">
                <image x="0" y="0" width="1000" height="1000"
                       preserveAspectRatio="xMidYMid meet"
                       href="data:{mediaType};base64,{base64}"
                       xlink:href="data:{mediaType};base64,{base64}" />
              </g>
            </svg>
            """;
    }

}
