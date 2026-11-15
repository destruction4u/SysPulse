using System.Security.Cryptography;
using System.Text;

namespace SysPulse.Core.Remote;

/// <summary>
/// The shared secret between an agent and a viewer: 24 characters of Crockford base32
/// (120 random bits), shown as six groups of four, e.g. <c>7K3Q-M9XA-...</c>.
/// Crockford's alphabet has no I, L, O or U, so it's hard to misread or mistype.
/// </summary>
public static class PairingCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 24;

    public static string Generate()
    {
        var sb = new StringBuilder(Length);
        for (int i = 0; i < Length; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    /// <summary>Uppercases, drops spaces/dashes and maps look-alikes (O→0, I/L→1).</summary>
    public static string Normalize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char ch in input.ToUpperInvariant())
        {
            char c = ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch };
            if (Alphabet.Contains(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool IsValid(string input) => Normalize(input).Length == Length;

    /// <summary>Display form: XXXX-XXXX-XXXX-XXXX-XXXX-XXXX.</summary>
    public static string Format(string code)
    {
        string n = Normalize(code);
        return string.Join('-', Enumerable.Range(0, (n.Length + 3) / 4).Select(i => n.Substring(i * 4, Math.Min(4, n.Length - i * 4))));
    }

    /// <summary>Constant-time comparison, so response timing doesn't leak how much of a guess was right.</summary>
    public static bool Matches(string candidate, string expected)
    {
        byte[] a = Encoding.ASCII.GetBytes(Normalize(candidate));
        byte[] b = Encoding.ASCII.GetBytes(Normalize(expected));
        return b.Length == Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
