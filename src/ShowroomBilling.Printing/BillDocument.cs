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

                page.Content().Element(c => ComposeCopy(c, copy));
            });
        }
    }

    private void ComposeCopy(IContainer container, CopyLabel copy)
    {
        container.Column(pageCol =>
        {
            // Copy label, small + right-aligned (V1 .copy-label).
            pageCol.Item().AlignRight().Text(copy.ToDisplayText())
                .FontSize(_xSmallFont).SemiBold();

            // Main invoice box with 1px border (V1 .invoice-box). ExtendVertical is
            // outermost so the bordered frame stretches to the A4 bottom; within the
            // box the bottom group (GST/footer) uses ExtendVertical+AlignBottom to pin
            // itself to the page bottom. The summary sits directly after the items table
            // (V1 parity) rather than in the bottom group.
            pageCol.Item().ExtendVertical()
                .Border(1).BorderColor(Colors.Black)
                .PaddingTop(8).PaddingHorizontal(10).PaddingBottom(10)
                .Column(box =>
            {
                box.Spacing(4);

                box.Item().Column(top =>
                {
                    top.Spacing(4);
                    ComposeHeader(top.Item());
                    ComposeCompanyAndParty(top.Item());
                    ComposeLineItemsTable(top.Item());
                    ComposeSummary(top.Item());
                });

                box.Item().ExtendVertical().AlignBottom().Column(bottom =>
                {
                    bottom.Spacing(4);
                    ComposeGstAndBank(bottom.Item());
                    ComposeFooter(bottom.Item());
                });
            });
        });
    }

    private void ComposeHeader(IContainer container)
    {
        container.Column(c =>
        {
            // Logo slot: fixed box; logo placed inside by offset + size.
            c.Item().AlignCenter()
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

            // TAX INVOICE banner with 2px rules top/bottom (V1 .invoice-banner).
            // LetterSpacing 0.24 em ≈ V1's 2.6px at 11px body.
            c.Item().PaddingTop(2)
                .BorderTop(1.5f).BorderBottom(1.5f).BorderColor(Colors.Black)
                .PaddingTop(5).PaddingBottom(4)
                .AlignCenter()
                .Text("TAX INVOICE")
                .FontSize(_bodyFont).Bold().LetterSpacing(0.24f);
        });
    }

    private void ComposeCompanyAndParty(IContainer container)
    {
        container.PaddingTop(6)
            .BorderTop(1f).BorderBottom(1f).BorderColor(Colors.Black)
            .PaddingVertical(6)
            .Row(row =>
        {
            row.RelativeItem(52).Column(c =>
            {
                c.Item().Text("Company Details").FontSize(_smallFont);
                c.Item().PaddingTop(2).Text(_options.Content.Company.Name)
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

                c.Item().PaddingVertical(5).LineHorizontal(1f).LineColor(Colors.Black);

                // V1 parity: when the operator leaves Party blank, the headline falls
                // back to the payment-mode label (V1's sales_tab auto-defaulted Party
                // to "Cash" / "Credit and Debit"). When the operator typed a real
                // customer name, V1 lost the payment-mode signal on print — V2 keeps
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
                c.Item().PaddingTop(2).Text(headline)
                    .FontSize(_xLargeFont).Bold();

                if (!string.IsNullOrWhiteSpace(_options.Content.PartyAddress))
                {
                    c.Item().PaddingTop(3).Text(_options.Content.PartyAddress).FontSize(_smallFont);
                }
                if (!string.IsNullOrWhiteSpace(_options.Content.PartyGstin))
                {
                    c.Item().PaddingTop(1).Text($"GSTIN {_options.Content.PartyGstin}").FontSize(_smallFont);
                }
                if (!string.IsNullOrWhiteSpace(_options.Content.PartyPhone))
                {
                    c.Item().PaddingTop(1).Text($"Ph {_options.Content.PartyPhone}").FontSize(_smallFont);
                }
                if (showPaymentLine)
                {
                    c.Item().PaddingTop(3).Text($"Payment: {paymentLabel}").FontSize(_smallFont);
                }
                if (!string.IsNullOrWhiteSpace(_options.Content.Notes))
                {
                    c.Item().PaddingTop(4).Text(_options.Content.Notes).FontSize(_smallFont);
                }
            });
        });
    }

    private void AppendKeyRow(ColumnDescriptor column, string label, string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        column.Item().PaddingTop(2).Row(r =>
        {
            r.ConstantItem(60).Text(label).FontSize(_bodyFont);
            r.RelativeItem().Text(text).FontSize(_bodyFont);
        });
    }

    private void AppendMetaRow(ColumnDescriptor column, string label, string value)
    {
        column.Item().PaddingVertical(1).Row(r =>
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

        container.PaddingTop(4).Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(22);  // #
                cd.RelativeColumn(3);   // Description
                cd.ConstantColumn(48);  // HSN
                if (hasLessWt)
                {
                    cd.ConstantColumn(46); // Gross Wt
                    cd.ConstantColumn(44); // Less Wt
                }
                cd.ConstantColumn(50); // Net Wt
                cd.ConstantColumn(40); // Purity (values always 3 chars e.g. 22K)
                cd.ConstantColumn(48); // Making
                if (hasExtra)
                {
                    cd.ConstantColumn(52); // Extra Charges
                }
                cd.ConstantColumn(54); // Rate/g
                cd.ConstantColumn(60); // Amount
            });

            table.Header(header =>
            {
                static IContainer H(IContainer c, int font) => c.Border(1f).BorderColor(Colors.Black)
                    .PaddingVertical(2).PaddingHorizontal(6)
                    .DefaultTextStyle(ts => ts.SemiBold().FontSize(font));

                header.Cell().Element(c => H(c, _smallFont)).AlignCenter().Text("#");
                header.Cell().Element(c => H(c, _smallFont)).AlignLeft().Text("Description");
                header.Cell().Element(c => H(c, _smallFont)).AlignCenter().Text("HSN");
                if (hasLessWt)
                {
                    header.Cell().Element(c => H(c, _smallFont)).AlignRight().Text("Gross Wt");
                    header.Cell().Element(c => H(c, _smallFont)).AlignRight().Text("Less Wt");
                }
                header.Cell().Element(c => H(c, _smallFont)).AlignRight().Text("Net Wt");
                header.Cell().Element(c => H(c, _smallFont)).AlignRight().Text("Purity");
                header.Cell().Element(c => H(c, _smallFont)).AlignRight().Text("Making");
                if (hasExtra)
                {
                    header.Cell().Element(c => H(c, _smallFont)).AlignRight().Text("Extra");
                }
                header.Cell().Element(c => H(c, _smallFont)).AlignRight().Text("Rate/g");
                header.Cell().Element(c => H(c, _smallFont)).AlignRight().Text("Amount");
            });

            int idx = 1;
            foreach (var line in lines)
            {
                IContainer Cell(IContainer c) => c.Border(1f).BorderColor(Colors.Black)
                    .PaddingVertical(2).PaddingHorizontal(6)
                    .DefaultTextStyle(ts => ts.FontSize(_bodyFont));

                var unit = string.IsNullOrWhiteSpace(line.Unit) ? "g" : line.Unit!.Trim();
                var grossWt = line.GrossWeight ?? 0m;
                var lessWt = line.LessWeight ?? 0m;
                var isDiamond = IsDiamond(line);

                table.Cell().Element(Cell).AlignCenter().Text(idx.ToString(CultureInfo.InvariantCulture));
                table.Cell().Element(Cell).Text(t => t.Span(line.ItemName).SemiBold());
                table.Cell().Element(Cell).AlignCenter().Text(
                    string.IsNullOrWhiteSpace(line.HsnCode) ? DefaultHsnCode : line.HsnCode!);

                if (hasLessWt)
                {
                    table.Cell().Element(Cell).AlignRight().Text(
                        !isDiamond && grossWt > 0m ? FormatWeight(grossWt, unit) : "—");
                    table.Cell().Element(Cell).AlignRight().Text(
                        !isDiamond && lessWt > 0m ? FormatWeight(lessWt, unit) : "—");
                }

                table.Cell().Element(Cell).AlignRight().Text(FormatWeight(line.Quantity, unit));
                table.Cell().Element(Cell).AlignCenter().Text(isDiamond ? "—" : line.Karat ?? "—");
                table.Cell().Element(Cell).AlignRight().Text(FormatMaking(line));

                if (hasExtra)
                {
                    var extra = line.Extra ?? 0m;
                    table.Cell().Element(Cell).AlignRight().Text(extra != 0m ? FormatMoney(extra) : "—");
                }

                table.Cell().Element(Cell).AlignRight().Text(FormatMoney(line.Rate));
                table.Cell().Element(Cell).AlignRight().Text(t => t.Span(FormatMoney(line.LineTotal)).SemiBold());
                idx++;
            }
        });
    }

    private void ComposeSummary(IContainer container)
    {
        var totals = _options.Content.Totals;
        var itemsTotalIncl = _options.Content.Lines.Sum(l => l.LineTotal);

        container.PaddingTop(4).Row(row =>
        {
            row.RelativeItem(52);
            row.RelativeItem(48).BorderTop(1f).BorderColor(Colors.Black).Column(c =>
            {
                c.Item().PaddingTop(3).Row(r =>
                {
                    r.RelativeItem().Text("Items Total (Incl. GST)").FontSize(_bodyFont);
                    r.ConstantItem(110).AlignRight().Text(FormatMoney(itemsTotalIncl)).FontSize(_bodyFont);
                });

                if (totals.DiscountTotal != 0m)
                {
                    c.Item().PaddingTop(1).Row(r =>
                    {
                        r.RelativeItem().Text("Discount").FontSize(_bodyFont);
                        r.ConstantItem(110).AlignRight().Text(FormatSigned(-totals.DiscountTotal)).FontSize(_bodyFont);
                    });
                }

                if (totals.RoundOff != 0m)
                {
                    c.Item().PaddingTop(1).Row(r =>
                    {
                        r.RelativeItem().Text("Round Off").FontSize(_bodyFont);
                        r.ConstantItem(110).AlignRight().Text(FormatSigned(totals.RoundOff)).FontSize(_bodyFont);
                    });
                }

                c.Item().PaddingTop(3).LineHorizontal(1f).LineColor(Colors.Black);
                c.Item().PaddingTop(3).Row(r =>
                {
                    r.RelativeItem().Text("Total Amount (Incl. GST)").FontSize(_largeFont).Bold();
                    r.ConstantItem(110).AlignRight().Text(FormatMoney(totals.GrandTotal, decimals: 0))
                        .FontSize(_xLargeFont).Bold();
                });
                c.Item().PaddingTop(2).LineHorizontal(2f).LineColor(Colors.Black);
            });
        });
    }

    private void ComposeGstAndBank(IContainer container)
    {
        var totals = _options.Content.Totals;
        var gstTotal = totals.TaxTotal;
        var cgst = Math.Round(gstTotal / 2m, 2, MidpointRounding.AwayFromZero);
        var sgst = gstTotal - cgst;

        container.PaddingTop(4).Row(row =>
        {
            row.RelativeItem(60).Border(1f).BorderColor(Colors.Black).Column(c =>
            {
                c.Item().BorderBottom(1f).BorderColor(Colors.Black)
                    .PaddingVertical(2).PaddingHorizontal(5)
                    .Text("GST BREAKUP (INCLUDED IN TOTAL)")
                    .FontSize(_smallFont).SemiBold();

                c.Item().Row(header =>
                {
                    GstHeaderCell(header, "Taxable Value Before GST");
                    GstHeaderCell(header, $"CGST @ {CgstRatePct:0.#}%");
                    GstHeaderCell(header, $"SGST @ {SgstRatePct:0.#}%");
                    GstHeaderCell(header, "Total GST Included", last: true);
                });

                c.Item().Row(values =>
                {
                    GstValueCell(values, FormatMoney(totals.Subtotal));
                    GstValueCell(values, FormatMoney(cgst));
                    GstValueCell(values, FormatMoney(sgst));
                    GstValueCell(values, FormatMoney(gstTotal), last: true, bold: true);
                });
            });

            row.ConstantItem(10);

            if (_options.Content.Company.HasAnyBankField)
            {
                row.RelativeItem(40).Border(1f).BorderColor(Colors.Black).Padding(6).Column(c =>
                {
                    c.Item().Text("BANK DETAILS").FontSize(_xSmallFont).SemiBold().LetterSpacing(0.05f);
                    AppendBankRow(c, "Bank Name", _options.Content.Company.BankName);
                    AppendBankRow(c, "Account No.", _options.Content.Company.BankAccount);
                    AppendBankRow(c, "IFSC Code", _options.Content.Company.BankIfsc);
                    AppendBankRow(c, "UPI ID", _options.Content.Company.BankUpi);
                });
            }
            else
            {
                row.RelativeItem(40);
            }
        });
    }

    private void GstHeaderCell(RowDescriptor row, string text, bool last = false)
    {
        // Border must precede Padding in the chain so the 1pt line sits at the cell
        // edge (outside the padding), matching V1's border-collapse grid appearance.
        var cell = row.RelativeItem().BorderBottom(1f).BorderColor(Colors.Black);
        if (!last) cell = cell.BorderRight(1f);
        cell.PaddingVertical(2).PaddingHorizontal(5).Text(text).FontSize(_xSmallFont).SemiBold();
    }

    private void GstValueCell(RowDescriptor row, string text, bool last = false, bool bold = false)
    {
        var cell = row.RelativeItem();
        if (!last) cell = cell.BorderRight(1f).BorderColor(Colors.Black);
        cell.PaddingVertical(3).PaddingHorizontal(5).AlignRight().Text(t =>
        {
            var span = t.Span(text).FontSize(_smallFont);
            if (bold) span.SemiBold();
        });
    }

    private void AppendBankRow(ColumnDescriptor column, string label, string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        column.Item().PaddingTop(2).Row(r =>
        {
            r.ConstantItem(72).Text(label).FontSize(_xSmallFont).FontColor(Colors.Grey.Darken2);
            r.RelativeItem().Text(text).FontSize(_xSmallFont).SemiBold();
        });
    }

    private void ComposeFooter(IContainer container)
    {
        container.PaddingTop(4).BorderTop(1f).BorderColor(Colors.Black).PaddingTop(4)
            .Row(row =>
        {
            row.RelativeItem(60).Column(c =>
            {
                var terms = _options.Content.Company.TermsAndConditions;
                if (!string.IsNullOrWhiteSpace(terms))
                {
                    c.Item().Text("Terms & Conditions").FontSize(_smallFont).SemiBold();
                    var lines = terms!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        c.Item().PaddingTop(2).Text(line.Trim())
                            .FontSize(_termsFont).LineHeight(1.45f);
                    }
                }
            });

            row.ConstantItem(10);

            row.RelativeItem(40).AlignBottom().Column(c =>
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

                // Constrain line to slot width (matches V1 .signature-block { width: 220px }).
                c.Item().AlignCenter()
                    .Width(PrintLayoutLimits.SignatureSlotWidthMm, Unit.Millimetre)
                    .BorderTop(1f).BorderColor(Colors.Black)
                    .PaddingTop(2).AlignCenter().Text("Authorised Signatory").FontSize(_xSmallFont);
            });
        });
    }

}
