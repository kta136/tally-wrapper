using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.Views.Invoice;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The Discount and Bank charges boxes on the real Invoice screen, typed into a key at a time as the
/// counter types. A view-model test sets the amount in one go, so it cannot see the box rewriting its
/// text between keys: bound with a keystroke update, "100." was rewritten to "100" before the paise
/// were typed, and a discount typed as 100.5 took ₹1,005 off the bill.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class InvoiceAmountBoxViewTests
{
    private static readonly Size ViewSize = new(1280, 800);

    [Fact]
    public void Paise_typed_a_key_at_a_time_are_kept()
    {
        var (amount, text, grandTotal) = WpfTestHost.Run(() =>
        {
            var (invoice, view) = HostedInvoice();
            invoice.BankChargesEnabled = true;
            var box = BoxBoundTo(view, nameof(InvoiceViewModel.BankCharges));

            TypeKeys(box, "50.25");
            Commit(box);

            return (invoice.BankCharges, box.Text, invoice.GrandTotal);
        });

        Assert.Equal(50.25m, amount);
        Assert.Equal("50.25", text);
        Assert.Equal(1050m, grandTotal);
    }

    [Fact]
    public void An_emptied_box_takes_the_bank_charges_off()
    {
        var (amount, grandTotal) = WpfTestHost.Run(() =>
        {
            var (invoice, view) = HostedInvoice();
            invoice.BankChargesEnabled = true;
            var box = BoxBoundTo(view, nameof(InvoiceViewModel.BankCharges));
            TypeKeys(box, "50.25");
            Commit(box);

            box.SelectAll();
            box.SelectedText = string.Empty;
            Commit(box);

            return (invoice.BankCharges, invoice.GrandTotal);
        });

        Assert.Equal(0m, amount);
        Assert.Equal(1000m, grandTotal);
    }

    [Fact]
    public void Text_that_is_no_amount_is_marked_and_changes_nothing()
    {
        var (amount, hasError) = WpfTestHost.Run(() =>
        {
            var (invoice, view) = HostedInvoice();
            invoice.BankCharges = 40m;
            var box = BoxBoundTo(view, nameof(InvoiceViewModel.BankCharges));

            box.SelectAll();
            box.SelectedText = "4o";
            Commit(box);

            return (invoice.BankCharges, Validation.GetHasError(box));
        });

        Assert.Equal(40m, amount);
        Assert.True(hasError);
    }

    [Fact]
    public void A_discount_with_paise_typed_a_key_at_a_time_is_kept()
    {
        var (amount, text, grandTotal) = WpfTestHost.Run(() =>
        {
            var (invoice, view) = HostedInvoice();
            invoice.DiscountEnabled = true;
            var box = BoxBoundTo(view, nameof(InvoiceViewModel.Discount));

            TypeKeys(box, "100.5");
            Commit(box);

            return (invoice.Discount, box.Text, invoice.GrandTotal);
        });

        Assert.Equal(100.50m, amount);
        Assert.Equal("100.5", text);
        // 999.99 − 100.50 = 899.49.
        Assert.Equal(899m, grandTotal);
    }

    [Fact]
    public void An_emptied_discount_box_takes_the_discount_off()
    {
        var (amount, grandTotal) = WpfTestHost.Run(() =>
        {
            var (invoice, view) = HostedInvoice();
            invoice.Discount = 100m;
            var box = BoxBoundTo(view, nameof(InvoiceViewModel.Discount));

            box.SelectAll();
            box.SelectedText = string.Empty;
            Commit(box);

            return (invoice.Discount, invoice.GrandTotal);
        });

        Assert.Equal(0m, amount);
        Assert.Equal(1000m, grandTotal);
    }

    [Fact]
    public void A_discount_worked_out_from_the_final_amount_shows_its_paise()
    {
        var text = WpfTestHost.Run(() =>
        {
            var (invoice, view) = HostedInvoice();
            var box = BoxBoundTo(view, nameof(InvoiceViewModel.Discount));

            invoice.FinalAmount = 850m;
            WpfTestHost.DrainDispatcher();

            return box.Text;
        });

        // 999.99 − 850; the box used to show it rounded, as 150.
        Assert.Equal("149.99", text);
    }

    [Fact]
    public void Typing_a_five_digit_final_amount_keeps_the_digits_in_order()
    {
        var (text, final, grandTotal) = WpfTestHost.Run(() =>
        {
            var (invoice, view) = HostedInvoice();
            invoice.Lines[0].DiamondRate = 10000m;
            WpfTestHost.DrainDispatcher();
            var box = BoxBoundTo(view, nameof(InvoiceViewModel.FinalAmount));
            TypeKeys(box, "12345");
            Commit(box);
            return (box.Text, invoice.FinalAmount, invoice.GrandTotal);
        });

        Assert.Equal("12,345", text);
        Assert.Equal(12345m, final);
        Assert.Equal(final, grandTotal);
    }

    /// <summary>WPF handles each input event, including caret movement after formatting.</summary>
    private static void TypeKeys(TextBox box, string keys)
    {
        box.SelectAll();
        foreach (var key in keys)
        {
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, key.ToString()));
            WpfTestHost.DrainDispatcher();
        }
    }

    /// <summary>What leaving the box, or Ctrl+S with the cursor still in it, does.</summary>
    private static void Commit(TextBox box)
    {
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        WpfTestHost.DrainDispatcher();
    }

    private static (InvoiceViewModel Invoice, InvoiceView View) HostedInvoice()
    {
        var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), null);
        var line = invoice.Lines[0];
        line.ItemMaster = TestMasters.Item("Diamond Ring", ItemUnits.Carat, category: ItemCategories.Diamond);
        line.GrossWeight = 10m;
        line.DiamondRate = 100m;

        var view = new InvoiceView { DataContext = invoice };
        _ = new Window { Content = view };
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(ViewSize);
            view.Arrange(new Rect(ViewSize));
            view.UpdateLayout();
            WpfTestHost.DrainDispatcher();
        }

        return (invoice, view);
    }

    private static TextBox BoxBoundTo(DependencyObject root, string path)
    {
        var found = Find(root);
        return found ?? throw new InvalidOperationException($"No text box bound to {path}.");

        TextBox? Find(DependencyObject node)
        {
            if (node is TextBox box && BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path?.Path == path)
            {
                return box;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                if (Find(VisualTreeHelper.GetChild(node, i)) is { } match)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
