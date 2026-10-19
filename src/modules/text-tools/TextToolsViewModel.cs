using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Modules.TextTools.Passwords;

namespace MultiTool.Modules.TextTools;

/// <summary>A tool in the list on the left.</summary>
/// <remarks>
/// The entry carries its own highlight: the buttons come from a template whose data context is this
/// entry, so <c>Section</c> is not reachable from there. Without <see cref="IsActive"/> there would be
/// nothing for a class binding to bind to.
/// </remarks>
/// <param name="Hint">Shown as a line above the input field.</param>
public sealed partial class ToolEntry(int index, string name, string hint) : ObservableObject
{
    public int Index { get; } = index;

    public string Name { get; } = name;

    public string Hint { get; } = hint;

    [ObservableProperty] public partial bool IsActive { get; set; }
}

public sealed partial class TextToolsViewModel : ObservableObject
{
    private readonly IModuleContext _context;

    public TextToolsViewModel(IModuleContext context)
    {
        _context = context;

        // The former password generator keeps its own settings (see SectionContext).
        Passwords = new PasswordViewModel(new SectionContext(context, "password-generator"));

        // Opens on passwords, the most used entry; the index stays 7 so the text tools keep theirs.
        Section = 7;

        // Section starts at 0, but OnSectionChanged doesn't fire for the initial value; without this
        // call the list would open with no highlight.
        ApplyActive();
    }

    /// <summary>Set by the view once it is attached to a window.</summary>
    public IClipboard? Clipboard { get; set; }

    public IReadOnlyList<ToolEntry> Tools { get; } =
    [
        new(7, "Passwords", ""),
        new(0, "Checksum", "Type text or drop a file."),
        new(1, "Base64", "Encode text or decode Base64. Missing padding is added."),
        new(2, "URL", "Percent-encoding for URLs and query parameters."),
        new(3, "JSON", "Indent or put on one line. Comments and trailing commas are tolerated."),
        new(4, "Timestamp", "Enter a number or a date; the direction follows from it."),
        new(5, "IDs", "UUID version 4, cryptographically random."),
        new(6, "Count", "Characters, words, lines and bytes."),
    ];

    /// <summary>The password generator, shown instead of the input and output fields.</summary>
    public PasswordViewModel Passwords { get; }

    [ObservableProperty] public partial int Section { get; set; }

    [ObservableProperty] public partial string Input { get; set; } = "";

    [ObservableProperty] public partial string Output { get; set; } = "";

    [ObservableProperty] public partial string? Message { get; set; }

    [ObservableProperty] public partial bool MessageIsError { get; set; }

    /// <summary>How many IDs at once.</summary>
    [ObservableProperty] public partial int Count { get; set; } = 5;

    public ToolEntry Current => Tools.FirstOrDefault(t => t.Index == Section) ?? Tools[0];

    public string Hint => Current.Hint;

    public bool IsHash => Section == 0;
    public bool IsBase64 => Section == 1;
    public bool IsUrl => Section == 2;
    public bool IsJson => Section == 3;
    public bool IsTime => Section == 4;
    public bool IsUuid => Section == 5;
    public bool IsCount => Section == 6;
    public bool IsPasswords => Section == 7;

    /// <summary>The text tools' own work area; the password generator brings its own layout.</summary>
    public bool IsTextTool => !IsPasswords;

    /// <summary>IDs need no input; an empty field there would be misleading.</summary>
    public bool HasInput => Section != 5;

    public bool HasOutput => Output.Length > 0;

    partial void OnSectionChanged(int value)
    {
        foreach (var name in new[]
                 {
                     nameof(Current), nameof(Hint), nameof(HasInput),
                     nameof(IsHash), nameof(IsBase64), nameof(IsUrl), nameof(IsJson),
                     nameof(IsTime), nameof(IsUuid), nameof(IsCount), nameof(IsPasswords), nameof(IsTextTool),
                 })
        {
            OnPropertyChanged(name);
        }

        ApplyActive();

        // The previous tool's output answers a different question; leaving it would show an answer
        // that doesn't match the label above.
        Output = "";
        Message = null;

        // Count recalculates instantly; waiting for a click would be one too many.
        if (Section is 6) Recalculate();
    }

    partial void OnInputChanged(string value)
    {
        if (Section is 6) Recalculate();
    }

    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(HasOutput));

    /// <remarks>
    /// <c>int</c>, not <c>string</c>: the list passes <c>ToolEntry.Index</c>, and a
    /// <c>RelayCommand&lt;string&gt;</c> rejects an <c>int</c> in <c>CanExecute</c>, so no entry could be clicked.
    /// </remarks>
    [RelayCommand]
    private void SelectSection(int index) => Section = index;

    /// <summary>Highlights exactly the current entry.</summary>
    private void ApplyActive()
    {
        foreach (var tool in Tools) tool.IsActive = tool.Index == Section;
    }

    /// <summary>Count recalculates while typing.</summary>
    private void Recalculate()
    {
        if (Section is 6) Output = TextOps.Count(Input);
    }

    // --------------------------------------------------------------- Checksum

    [RelayCommand]
    private void HashText(string kind)
    {
        if (!Enum.TryParse<HashKind>(kind, out var which)) return;

        Run(() => TextOps.Hash(Input, which));
    }

    [RelayCommand]
    private async Task HashFileAsync(string kind)
    {
        if (!Enum.TryParse<HashKind>(kind, out var which)) return;

        var picked = await _context.Shell.PickFilesAsync("Choose file", multiple: false);
        if (picked is not [var path, ..]) return;

        await HashPathAsync(path, which);
    }

    /// <summary>Also called by the view when a file is dropped.</summary>
    public async Task HashPathAsync(string path, HashKind kind)
    {
        try
        {
            Show($"Reading {Path.GetFileName(path)}…");

            var digest = await Task.Run(
                () => TextOps.HashFileAsync(path, kind, _context.Shutdown),
                _context.Shutdown);

            Output = digest;
            Input = path;
            Section = 0;

            Show($"{Path.GetFileName(path)} · {Humanize.Bytes(new FileInfo(path).Length)}");
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
    }

    // --------------------------------------------------------------- The others

    [RelayCommand]
    private void Encode() => Run(() => Section switch
    {
        1 => TextOps.ToBase64(Input),
        2 => TextOps.UrlEncode(Input),
        3 => TextOps.FormatJson(Input, indented: true),
        _ => Output,
    });

    [RelayCommand]
    private void Decode() => Run(() => Section switch
    {
        1 => TextOps.FromBase64(Input),
        2 => TextOps.UrlDecode(Input),
        3 => TextOps.FormatJson(Input, indented: false),
        _ => Output,
    });

    [RelayCommand]
    private void Convert() => Run(() => TextOps.DescribeTime(Input));

    [RelayCommand]
    private void Now() => Run(TextOps.Now);

    [RelayCommand]
    private void Generate() => Run(() => TextOps.Uuids(Count));

    // --------------------------------------------------------------- Helpers

    /// <summary>Runs a conversion and catches its error.</summary>
    /// <remarks>
    /// Every tool can fail on input that looks reasonable (Base64 with a space, JSON missing a comma).
    /// The reason belongs in the message line, not in a crash.
    /// </remarks>
    private void Run(Func<string> work)
    {
        try
        {
            Output = work();
            Message = null;
        }
        catch (Exception ex)
        {
            Output = "";
            Show(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (Clipboard is null || Output.Length == 0) return;

        await Clipboard.SetTextAsync(Output);
        Show("Copied.");
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (Clipboard is null) return;

        var text = await Clipboard.TryGetTextAsync();
        if (text is { Length: > 0 }) Input = text;
    }

    [RelayCommand]
    private void Clear()
    {
        Input = "";
        Output = "";
        Message = null;
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }
}
