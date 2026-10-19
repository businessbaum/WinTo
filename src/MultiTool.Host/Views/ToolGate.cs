using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using MultiTool.Abstractions;
using MultiTool.Host.Services;

namespace MultiTool.Host.Views;

/// <summary>Before opening a module, checks that its tools exist and downloads them on request.</summary>
/// <remarks>
/// <para>
/// Before, not after: a module fetching its own tool can only ask once it is open, leaving the user
/// in front of a UI that can't do anything yet.
/// </para>
/// <para>
/// Nothing is forced: declining still opens the module, with a note about what won't work.
/// </para>
/// <para>Built in code like <see cref="MessageDialog"/>; the content depends on what is missing.</para>
/// </remarks>
internal static class ToolGate
{
    private const int ShadowMargin = 30;
    private const int CardWidth = 520;

    /// <summary>What is missing, and whether the shell can fetch it.</summary>
    private sealed record Missing(ToolRequirement Requirement)
    {
        public bool CanFetch => Requirement.Url is { Length: > 0 };
    }

    /// <summary>Whether anything is missing. A few <c>File.Exists</c>, nothing more.</summary>
    public static bool IsMissingAnything(ModuleManifest manifest) => Find(manifest).Count > 0;

    /// <summary>Checks the module's mandatory tools.</summary>
    /// <returns><c>false</c> if the user cancelled and does not want to open.</returns>
    public static async Task<bool> EnsureAsync(ModuleManifest manifest, Window? owner)
    {
        var missing = Find(manifest);
        if (missing.Count == 0) return true;

        return await ShowAsync(manifest, missing, AppPaths.ModuleData(manifest.Id), owner);
    }

    private static List<Missing> Find(ModuleManifest manifest)
    {
        var dataDirectory = AppPaths.ModuleData(manifest.Id);

        return [.. manifest.Requires
            .Where(r => r.Mandatory)
            .Where(r => ExternalTool.Find(r.Tool, manifest.Directory, dataDirectory) is null)
            .Select(r => new Missing(r))];
    }

    private static async Task<bool> ShowAsync(
        ModuleManifest manifest, List<Missing> missing, string dataDirectory, Window? owner)
    {
        var proceed = false;

        var dialog = new Window
        {
            Title = manifest.Name,
            SizeToContent = SizeToContent.Height,
            Width = CardWidth + 2 * ShadowMargin,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };

        var rows = new StackPanel { Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };
        var lines = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in missing)
        {
            var state = new TextBlock
            {
                Text = item.CanFetch ? "required" : item.Requirement.Hint ?? "required",
                Classes = { "caption", "muted" },
                TextWrapping = TextWrapping.Wrap,
            };

            lines[item.Requirement.Tool] = state;

            rows.Children.Add(new Border
            {
                Background = Resource("BgSurfaceBrush"),
                BorderBrush = Resource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = item.Requirement.Tool, Classes = { "mono" }, FontWeight = FontWeight.SemiBold },
                        state,
                    },
                },
            });
        }

        var fetchable = missing.Where(m => m.CanFetch).ToList();

        var explanation = new TextBlock
        {
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
            Text = fetchable.Count > 0
                ? $"\"{manifest.Name}\" needs an additional tool. It is downloaded to "
                  + $"{dataDirectory} and used only by this module."
                : $"\"{manifest.Name}\" needs a tool that can't be fetched automatically.",
        };

        var urls = fetchable.Count > 0
            ? new SelectableTextBlock
            {
                Text = string.Join(Environment.NewLine, fetchable.Select(m => m.Requirement.Url)),
                Classes = { "mono", "caption", "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            }
            : null;

        var progress = new ProgressBar
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            IsIndeterminate = true,
            IsVisible = false,
            Foreground = Resource("AccentBrush"),
            Background = Resource("BgElevatedBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
        };

        var skip = new Button { Content = "Open without", MinWidth = 120 };
        var cancel = new Button { Content = "Cancel", MinWidth = 110 };
        var fetch = new Button
        {
            Content = fetchable.Count == 1 ? $"Get {fetchable[0].Requirement.Tool}" : "Get",
            MinWidth = 130,
            IsDefault = true,
            Classes = { "accent" },
            IsVisible = fetchable.Count > 0,
        };

        cancel.Click += (_, _) => { proceed = false; dialog.Close(); };
        skip.Click += (_, _) => { proceed = true; dialog.Close(); };

        var fetching = false;

        fetch.Click += async (_, _) =>
        {
            fetching = true;
            fetch.IsEnabled = false;
            skip.IsEnabled = false;
            cancel.IsEnabled = false;
            progress.IsVisible = true;

            var allOk = true;

            foreach (var item in fetchable)
            {
                var line = lines[item.Requirement.Tool];
                line.Text = "downloading…";

                var problem = await FetchAsync(item.Requirement, dataDirectory);

                if (problem is null)
                {
                    line.Text = "downloaded and ready";
                    line.Foreground = Resource("SuccessBrush");
                }
                else
                {
                    line.Text = problem;
                    line.Foreground = Resource("DangerBrush");
                    allOk = false;
                }
            }

            progress.IsVisible = false;
            fetching = false;

            if (allOk)
            {
                explanation.Text = "Done. Opening the module.";

                // Leave it visible briefly; a dialog vanishing as the message appears never showed it.
                await Task.Delay(TimeSpan.FromMilliseconds(600));

                proceed = true;
                dialog.Close();
                return;
            }

            explanation.Text = "Not everything could be fetched. Opening is still possible.";

            skip.IsEnabled = true;
            cancel.IsEnabled = true;
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(skip);
        buttons.Children.Add(fetch);

        var body = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 22),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Tool missing", Classes = { "h1" }, FontSize = 17 },
                explanation,
            },
        };

        if (urls is not null) body.Children.Add(urls);
        body.Children.Add(rows);
        body.Children.Add(progress);
        body.Children.Add(buttons);

        dialog.Content = new Border
        {
            Margin = new Thickness(ShadowMargin),
            Width = CardWidth,
            Background = Resource("BgElevatedBrush") ?? Brushes.Black,
            BorderBrush = Resource("BorderStrongBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            BoxShadow = BoxShadows.Parse("0 18 44 0 #99000000"),
            Child = body,
        };

        dialog.KeyDown += (_, e) =>
        {
            // Not mid-download: the dialog would be gone and the download continue invisibly.
            if (e.Key == Key.Escape && !fetching) dialog.Close();
        };

        await MessageDialog.ShowAndWaitAsync(dialog, owner);
        return proceed;
    }

    /// <summary>Downloads a file into the module's data folder.</summary>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    private static async Task<string?> FetchAsync(ToolRequirement requirement, string dataDirectory)
    {
        if (requirement.Url is not { Length: > 0 } url) return "No URL given.";

        var name = requirement.Tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? requirement.Tool
            : requirement.Tool + ".exe";

        var target = Path.Combine(AppPaths.EnsureDirectory(dataDirectory), name);
        var temporary = target + ".part";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            using (var stream = await http.GetStreamAsync(url))
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            {
                await stream.CopyToAsync(file);
            }

            if (new FileInfo(temporary).Length == 0) throw new IOException("The response was empty.");

            // Check before renaming: whatever fails here never becomes a tool and never runs.
            if (await VerifyAsync(http, requirement, url, temporary) is { } mismatch)
                throw new IOException(mismatch);

            // Rename only once complete; an aborted download would otherwise leave half an EXE that
            // counts as present.
            File.Move(temporary, target, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
            return ex.Message;
        }
    }

    /// <summary>Compares the downloaded file with the checksum from the manifest.</summary>
    /// <returns><c>null</c> if it matches or none is given; otherwise the reason.</returns>
    /// <remarks>
    /// HTTPS proves the server, not that the file is the one the project published. If the checksum
    /// list can't be fetched, the file counts as <b>unverified</b> and is discarded; "Open without"
    /// remains.
    /// </remarks>
    private static async Task<string?> VerifyAsync(
        HttpClient http, ToolRequirement requirement, string url, string file)
    {
        string? expected = requirement.Sha256?.Trim();

        if (expected is null && requirement.ChecksumsUrl is { Length: > 0 } listUrl)
        {
            string list;
            try
            {
                list = await http.GetStringAsync(listUrl);
            }
            catch (Exception ex)
            {
                return $"Checksum list not available: {ex.Message}";
            }

            var name = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? Path.GetFileName(uri.LocalPath)
                : Path.GetFileName(url);

            expected = ChecksumFor(list, name);
            if (expected is null) return $"{name} is missing from the checksum list.";
        }

        if (expected is null) return null;

        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : "Checksum mismatch. File discarded.";
    }

    /// <summary>Finds the line for a file in a <c>sha256sum</c> list.</summary>
    internal static string? ChecksumFor(string list, string fileName)
    {
        foreach (var raw in list.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space <= 0) continue;

            var hash = line[..space];

            // An asterisk before the name means "read in binary mode" in sha256sum.
            var name = line[space..].Trim().TrimStart('*');

            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)
                && hash.Length == 64 && hash.All(Uri.IsHexDigit))
            {
                return hash;
            }
        }

        return null;
    }

    private static IBrush? Resource(string key) =>
        Application.Current?.FindResource(key) as IBrush;
}
