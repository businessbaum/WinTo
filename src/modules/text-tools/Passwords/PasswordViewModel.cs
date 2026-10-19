using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.TextTools.Passwords;

/// <summary>What goes into the module's <c>settings.json</c>.</summary>
public sealed class PasswordSettings
{
    public int Length { get; set; } = 20;
    public bool UseLower { get; set; } = true;
    public bool UseUpper { get; set; } = true;
    public bool UseDigits { get; set; } = true;
    public bool UseSymbols { get; set; } = true;
    public string Symbols { get; set; } = PasswordFactory.DefaultSymbols;
    public bool ExcludeAmbiguous { get; set; }
    public bool RequireEachGroup { get; set; } = true;
    public int Count { get; set; } = 5;
}

public sealed partial class PasswordViewModel : ObservableObject
{
    private readonly IModuleContext _context;
    private bool _loading = true;

    public PasswordViewModel(IModuleContext context)
    {
        _context = context;

        var settings = context.Settings.Load<PasswordSettings>();
        Length = Math.Clamp(settings.Length, 4, 128);
        UseLower = settings.UseLower;
        UseUpper = settings.UseUpper;
        UseDigits = settings.UseDigits;
        UseSymbols = settings.UseSymbols;
        Symbols = settings.Symbols;
        ExcludeAmbiguous = settings.ExcludeAmbiguous;
        RequireEachGroup = settings.RequireEachGroup;
        Count = Math.Clamp(settings.Count, 1, 25);

        _loading = false;

        Recalculate();
        Generate();
    }

    /// <summary>Set by the view once it is attached to a window.</summary>
    /// <remarks>The clipboard belongs to the <c>TopLevel</c>, which only the view knows.</remarks>
    public IClipboard? Clipboard { get; set; }

    /// <summary>The window the view lives in, owner of the clipboard. See <see cref="SecretClipboard"/>.</summary>
    public nint WindowHandle { get; set; }

    /// <summary>How long a copied password stays in the clipboard.</summary>
    private static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);

    public ObservableCollection<string> Results { get; } = [];

    [ObservableProperty] public partial int Length { get; set; }
    [ObservableProperty] public partial bool UseLower { get; set; }
    [ObservableProperty] public partial bool UseUpper { get; set; }
    [ObservableProperty] public partial bool UseDigits { get; set; }
    [ObservableProperty] public partial bool UseSymbols { get; set; }
    [ObservableProperty] public partial string Symbols { get; set; } = PasswordFactory.DefaultSymbols;
    [ObservableProperty] public partial bool ExcludeAmbiguous { get; set; }
    [ObservableProperty] public partial bool RequireEachGroup { get; set; }
    [ObservableProperty] public partial int Count { get; set; }

    [ObservableProperty] public partial string EntropyText { get; set; } = "";
    [ObservableProperty] public partial string CrackText { get; set; } = "";
    [ObservableProperty] public partial string RatingLabel { get; set; } = "";
    [ObservableProperty] public partial int RatingLevel { get; set; }
    [ObservableProperty] public partial string AlphabetText { get; set; } = "";
    [ObservableProperty] public partial string? Warning { get; set; }
    [ObservableProperty] public partial string? Toast { get; set; }

    public bool IsWeak => RatingLevel <= 1;
    public bool IsMedium => RatingLevel == 2;
    public bool IsStrong => RatingLevel >= 3;

    partial void OnLengthChanged(int value) => Changed();
    partial void OnUseLowerChanged(bool value) => Changed();
    partial void OnUseUpperChanged(bool value) => Changed();
    partial void OnUseDigitsChanged(bool value) => Changed();
    partial void OnUseSymbolsChanged(bool value) => Changed();
    partial void OnSymbolsChanged(string value) => Changed();
    partial void OnExcludeAmbiguousChanged(bool value) => Changed();
    partial void OnRequireEachGroupChanged(bool value) => Changed();
    partial void OnCountChanged(int value) => Changed();

    partial void OnRatingLevelChanged(int value)
    {
        OnPropertyChanged(nameof(IsWeak));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsStrong));
    }

    private PasswordRequest BuildRequest()
    {
        var groups = CharacterGroups.None;
        if (UseLower) groups |= CharacterGroups.Lower;
        if (UseUpper) groups |= CharacterGroups.Upper;
        if (UseDigits) groups |= CharacterGroups.Digits;
        if (UseSymbols) groups |= CharacterGroups.Symbols;

        return new PasswordRequest(Length, groups, Symbols, ExcludeAmbiguous, RequireEachGroup);
    }

    private void Changed()
    {
        if (_loading) return;
        Save();
        Recalculate();
        Generate();
    }

    private void Recalculate()
    {
        var request = BuildRequest();
        var groups = PasswordFactory.BuildGroups(request);
        var alphabet = groups.Sum(g => g.Length);

        if (alphabet == 0)
        {
            Warning = "Select at least one character group.";
            EntropyText = "—";
            CrackText = "";
            AlphabetText = "";
            RatingLabel = "";
            RatingLevel = 0;
            return;
        }

        Warning = RequireEachGroup && groups.Count > 1 && Length < groups.Count
            ? $"{Length} characters are not enough for {groups.Count} groups. The condition doesn't apply."
            : null;

        var bits = PasswordFactory.EntropyBits(request);
        var (label, level) = PasswordFactory.Rate(bits);

        EntropyText = $"{bits:0.0} bits of entropy";
        CrackText = "Offline brute force: " + PasswordFactory.CrackTime(bits);
        AlphabetText = $"{alphabet} characters in {groups.Count} groups";
        RatingLabel = label;
        RatingLevel = level;
    }

    [RelayCommand]
    public void Generate()
    {
        var request = BuildRequest();

        Results.Clear();
        for (var i = 0; i < Math.Clamp(Count, 1, 25); i++)
        {
            var password = PasswordFactory.Create(request);
            if (password.Length > 0) Results.Add(password);
        }

        // No tile status: "last 20 characters" recalls a setting, not the state of anything.
    }

    [RelayCommand]
    private async Task CopyAsync(string? password)
    {
        if (string.IsNullOrEmpty(password)) return;

        var owner = WindowHandle;
        var sequence = SecretClipboard.Set(owner, password);

        if (sequence is null)
        {
            // The direct way failed; fall back to plain text. A copied password in the history beats none.
            if (Clipboard is null) return;
            await Clipboard.SetTextAsync(password);
            Toast = "Copied.";
        }
        else
        {
            Toast = $"Copied, cleared in {ClearAfter.TotalSeconds:0} s.";
            _ = ClearLaterAsync(sequence.Value);
        }

        // The notice disappears by itself.
        var shown = Toast;
        await Task.Delay(2200);
        if (ReferenceEquals(Toast, shown)) Toast = null;
    }

    /// <summary>Clears after <see cref="ClearAfter"/>, unless something else was copied meanwhile.</summary>
    private static async Task ClearLaterAsync(uint sequence)
    {
        await Task.Delay(ClearAfter);
        SecretClipboard.ClearIfUnchanged(sequence);
    }

    private void Save() => _context.Settings.Save(new PasswordSettings
    {
        Length = Length,
        UseLower = UseLower,
        UseUpper = UseUpper,
        UseDigits = UseDigits,
        UseSymbols = UseSymbols,
        Symbols = Symbols,
        ExcludeAmbiguous = ExcludeAmbiguous,
        RequireEachGroup = RequireEachGroup,
        Count = Count,
    });
}
