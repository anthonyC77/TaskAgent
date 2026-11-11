using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TaskAgent.Views;

/// <summary>Petites boîtes de dialogue construites en code (pas besoin de XAML pour si peu).</summary>
public static class Dialogs
{
    public static Task<string?> AskAsync(Window owner, string title, string watermark)
    {
        var box = new TextBox { Watermark = watermark, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80 };
        var ok = new Button { Content = "OK", Classes = { "accent" } };
        var cancel = new Button { Content = "Annuler" };
        var win = Build(title, box, cancel, ok);
        ok.Click += (_, _) => win.Close(box.Text ?? "");
        cancel.Click += (_, _) => win.Close(null);
        return win.ShowDialog<string?>(owner);
    }

    public static Task<bool> ConfirmAsync(Window owner, string message)
    {
        var ok = new Button { Content = "Oui", Classes = { "accent" } };
        var cancel = new Button { Content = "Non" };
        var win = Build(message, null, cancel, ok);
        ok.Click += (_, _) => win.Close(true);
        cancel.Click += (_, _) => win.Close(false);
        return win.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// Demande un jeton d'accès (saisie masquée). Renvoie le jeton, "" si l'utilisateur préfère s'en passer, null si annulé.
    /// </summary>
    public static Task<string?> AskTokenAsync(Window owner, string title, string explanation, string helpUrl)
    {
        var box = new TextBox { Watermark = "Colle ton jeton ici", PasswordChar = '•' };
        var help = new Button { Content = "↗ Créer un jeton", HorizontalAlignment = HorizontalAlignment.Left };
        help.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helpUrl) { UseShellExecute = true }); }
            catch { }
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        body.Children.Add(help);
        body.Children.Add(box);

        var ok = new Button { Content = "Enregistrer", Classes = { "accent" } };
        var skip = new Button { Content = "Sans ce service" };
        var cancel = new Button { Content = "Annuler" };
        var win = Build(title, body, cancel, skip, ok);
        win.Width = 480;
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(box.Text)) win.Close(box.Text.Trim()); };
        skip.Click += (_, _) => win.Close("");
        cancel.Click += (_, _) => win.Close(null);
        return win.ShowDialog<string?>(owner);
    }

    private static Window Build(string title, Control? body, params Button[] buttons)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (body is not null) panel.Children.Add(body);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) row.Children.Add(b);
        panel.Children.Add(row);

        return new Window
        {
            Title = "TaskAgent",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
    }
}
