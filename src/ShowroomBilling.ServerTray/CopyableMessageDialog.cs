namespace ShowroomBilling.ServerTray;

internal sealed class CopyableMessageDialog : Form
{
    private readonly string _message;
    private readonly Button _copyButton;

    private CopyableMessageDialog(string caption, string message, MessageBoxIcon icon)
    {
        _message = message;

        Text = caption;
        Icon = AppIconProvider.CreateIcon();
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 620;
        Height = 340;
        MinimumSize = new Size(460, 260);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        body.Controls.Add(new PictureBox
        {
            Image = IconFor(icon).ToBitmap(),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Width = 44,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 14, 0)
        }, 0, 0);

        var messageBox = new TextBox
        {
            Text = message,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill
        };
        body.Controls.Add(messageBox, 1, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 14, 0, 0)
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = 96,
            Height = 32
        };

        _copyButton = new Button
        {
            Text = "Copy",
            Width = 96,
            Height = 32,
            Margin = new Padding(8, 0, 0, 0)
        };
        _copyButton.Click += HandleCopyClick;

        buttons.Controls.Add(okButton);
        buttons.Controls.Add(_copyButton);

        root.Controls.Add(body, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        AcceptButton = okButton;
        CancelButton = okButton;
        Shown += (_, _) => messageBox.SelectAll();
    }

    public static void ShowMessage(
        IWin32Window? owner,
        string caption,
        string message,
        MessageBoxIcon icon)
    {
        using var dialog = new CopyableMessageDialog(caption, message, icon);
        if (owner is null)
        {
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.ShowDialog();
            return;
        }

        dialog.ShowDialog(owner);
    }

    private void HandleCopyClick(object? sender, EventArgs e)
    {
        try
        {
            Clipboard.SetText(_message);
            _copyButton.Text = "Copied";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Copy failed: {ex.Message}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static Icon IconFor(MessageBoxIcon icon)
    {
        if (icon == MessageBoxIcon.Error)
        {
            return SystemIcons.Error;
        }

        if (icon == MessageBoxIcon.Warning)
        {
            return SystemIcons.Warning;
        }

        if (icon == MessageBoxIcon.Question)
        {
            return SystemIcons.Question;
        }

        return SystemIcons.Information;
    }
}
