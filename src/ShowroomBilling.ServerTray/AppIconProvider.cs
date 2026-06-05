namespace ShowroomBilling.ServerTray;

internal static class AppIconProvider
{
    public static Icon CreateIcon()
    {
        try
        {
            var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted is not null)
            {
                return (Icon)extracted.Clone();
            }
        }
        catch
        {
            // Fall back to a system icon when running from unusual hosts.
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
