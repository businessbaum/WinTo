using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;

namespace MultiTool.Modules.TextTools.Passwords;

/// <summary>The character groups a password is built from.</summary>
[Flags]
public enum CharacterGroups
{
    None = 0,
    Lower = 1,
    Upper = 2,
    Digits = 4,
    Symbols = 8,
}

/// <param name="Length">Number of characters.</param>
/// <param name="Groups">Which groups are allowed.</param>
/// <param name="Symbols">The symbol set. Empty means the default.</param>
/// <param name="ExcludeAmbiguous">Leave out easily confused characters (I l 1, O 0).</param>
/// <param name="RequireEachGroup">Every selected group must appear at least once.</param>
public sealed record PasswordRequest(
    int Length,
    CharacterGroups Groups,
    string Symbols = "",
    bool ExcludeAmbiguous = false,
    bool RequireEachGroup = true);

/// <summary>Creates passwords and says how strong they really are.</summary>
public static class PasswordFactory
{
    public const string Lower = "abcdefghijklmnopqrstuvwxyz";
    public const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string Digits = "0123456789";
    public const string DefaultSymbols = "!#$%&()*+,-./:;<=>?@[]^_{|}~";

    /// <summary>Characters confused in sans-serif fonts; unwanted when typing or reading a password aloud.</summary>
    private const string AmbiguousChars = "Il1O0o|";

    private static readonly SearchValues<char> Ambiguous = SearchValues.Create(AmbiguousChars);

    /// <summary>
    /// Builds the alphabet from the selected groups, returning each group separately because the
    /// entropy calculation needs the group sizes.
    /// </summary>
    public static IReadOnlyList<string> BuildGroups(PasswordRequest request)
    {
        var groups = new List<string>(4);

        Add(CharacterGroups.Lower, Lower);
        Add(CharacterGroups.Upper, Upper);
        Add(CharacterGroups.Digits, Digits);
        Add(CharacterGroups.Symbols, string.IsNullOrWhiteSpace(request.Symbols)
            ? DefaultSymbols
            : new string(request.Symbols.Distinct().ToArray()));

        return groups;

        void Add(CharacterGroups flag, string chars)
        {
            if (!request.Groups.HasFlag(flag)) return;

            var usable = request.ExcludeAmbiguous
                ? new string(chars.Where(c => !Ambiguous.Contains(c)).ToArray())
                : chars;

            if (usable.Length > 0) groups.Add(usable);
        }
    }

    /// <summary>Creates a password.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="RandomNumberGenerator.GetItems{T}(ReadOnlySpan{T}, int)"/> instead of modulo, which skews
    /// the distribution when the alphabet size doesn't divide the range evenly.
    /// </para>
    /// <para>
    /// "Every group must appear" is met by rejecting and redrawing, not by replacing a character
    /// afterwards: a fixed position becomes predictable and costs more entropy than the condition.
    /// </para>
    /// </remarks>
    public static string Create(PasswordRequest request)
    {
        var groups = BuildGroups(request);
        if (groups.Count == 0) return "";

        var alphabet = string.Concat(groups);
        var length = Math.Max(1, request.Length);

        var mustContainAll = request.RequireEachGroup && groups.Count > 1 && length >= groups.Count;

        // Once per group, not per attempt: SearchValues.Create builds a lookup table that costs more
        // than the search itself.
        var needles = mustContainAll
            ? groups.Select(g => SearchValues.Create(g)).ToArray()
            : [];

        // 10,000 attempts are orders of magnitude more than any sensible combination needs; the limit
        // only keeps an impossible request from running forever.
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var candidate = RandomNumberGenerator.GetItems<char>(alphabet, length);

            if (!mustContainAll) return new string(candidate);

            var ok = true;
            foreach (var needle in needles)
            {
                if (candidate.AsSpan().IndexOfAny(needle) >= 0) continue;
                ok = false;
                break;
            }

            if (ok) return new string(candidate);
        }

        // Unreachable for length >= groups.Count. A valid password without the extra condition beats
        // an exception in the UI.
        return new string(RandomNumberGenerator.GetItems<char>(alphabet, length));
    }

    /// <summary>How many bits of entropy this password really has.</summary>
    /// <remarks>
    /// <para>
    /// The usual <c>length × log2(alphabet)</c> is only right without "every group must appear"; with it
    /// the set of possible passwords is smaller.
    /// </para>
    /// <para>
    /// The exact value follows from inclusion-exclusion: of all <c>N^L</c> strings, subtract those
    /// missing a group, add back the doubly subtracted, and so on. With at most four groups that is
    /// 16 terms, exact and cheap.
    /// </para>
    /// <code>
    ///   |valid| = Σ  (-1)^|S| · (N − Σ  n_i)^L
    ///             S⊆G              i∈S
    /// </code>
    /// <para>Computed in <see cref="BigInteger"/>: 128 characters out of 90 is about 830 bits, too big for a <c>double</c>.</para>
    /// </remarks>
    public static double EntropyBits(PasswordRequest request)
    {
        var groups = BuildGroups(request);
        if (groups.Count == 0 || request.Length <= 0) return 0;

        var length = request.Length;
        var total = groups.Sum(g => g.Length);

        if (!request.RequireEachGroup || groups.Count == 1 || length < groups.Count)
            return length * Math.Log2(total);

        BigInteger valid = 0;

        for (var subset = 0; subset < 1 << groups.Count; subset++)
        {
            var excluded = 0;
            var bits = 0;

            for (var i = 0; i < groups.Count; i++)
            {
                if ((subset & (1 << i)) == 0) continue;
                excluded += groups[i].Length;
                bits++;
            }

            var remaining = total - excluded;
            if (remaining <= 0) continue;

            var term = BigInteger.Pow(remaining, length);
            valid += bits % 2 == 0 ? term : -term;
        }

        return valid <= 0 ? 0 : BigInteger.Log(valid, 2);
    }

    /// <summary>How long an attacker guessing offline against a fast hash needs on average.</summary>
    /// <remarks>
    /// 10^12 guesses per second, the order for SHA-1/MD5 on GPUs, not for bcrypt or Argon2. Pessimistic
    /// on purpose: a number depending on the other side should assume its best case.
    /// </remarks>
    public static string CrackTime(double bits)
    {
        if (bits <= 0) return "instantly";

        // On average half the space is searched, i.e. 2^(bits-1).
        var seconds = Math.Pow(2, bits - 1) / 1e12;

        return seconds switch
        {
            < 1 => "under a second",
            < 60 => $"{seconds:0} seconds",
            < 3600 => $"{seconds / 60:0} minutes",
            < 86_400 => $"{seconds / 3600:0} hours",
            < 31_557_600 => $"{seconds / 86_400:0} days",
            < 31_557_600e3 => $"{seconds / 31_557_600:N0} years",
            < 31_557_600e9 => $"{seconds / 31_557_600e6:N0} million years",
            _ => "over a billion years",
        };
    }

    /// <summary>Rating in words. The limits are convention, not physics.</summary>
    public static (string Label, int Level) Rate(double bits) => bits switch
    {
        < 40 => ("weak", 0),
        < 60 => ("fair", 1),
        < 80 => ("good", 2),
        < 110 => ("strong", 3),
        _ => ("very strong", 4),
    };
}
