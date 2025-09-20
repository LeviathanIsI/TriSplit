using System.Globalization;
using System.Text.RegularExpressions;
using TriSplit.Core.Models;

namespace TriSplit.Core.Transforms;

public sealed record BuiltInTransformDefinition(string Key, string DisplayName, string Description);

public static class BuiltInTransforms
{
    public const string BuiltInType = "builtin";

    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly IReadOnlyList<BuiltInTransformDefinition> _definitions = new List<BuiltInTransformDefinition>
    {
        new("trim", "Trim", "Remove leading and trailing whitespace"),
        new("upper", "Uppercase", "Convert all characters to uppercase"),
        new("lower", "Lowercase", "Convert all characters to lowercase"),
        new("titlecase", "Title Case", "Capitalize each word for name-style values"),
        new("whitespace_collapse", "Collapse Whitespace", "Replace repeated whitespace with a single space"),
        new("zip5", "ZIP 5", "Keep only the first five ZIP code digits"),
        new("zip_plus4", "ZIP+4", "Format ZIP codes as 12345-6789 when nine digits are present"),
        new("phone10", "Phone (10 digits)", "Strip non-digits and keep the last ten digits")
    };

    private static readonly Dictionary<string, Func<string, string>> _handlers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trim"] = static value => value.Trim(),
        ["upper"] = static value => value.ToUpperInvariant(),
        ["lower"] = static value => value.ToLowerInvariant(),
        ["titlecase"] = static value =>
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return trimmed;
            }

            var culture = CultureInfo.CurrentCulture;
            return culture.TextInfo.ToTitleCase(trimmed.ToLower(culture));
        },
        ["whitespace_collapse"] = static value =>
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var trimmed = value.Trim();
            return CollapseWhitespaceRegex.Replace(trimmed, " ");
        },
        ["zip5"] = static value =>
        {
            var digits = FilterDigits(value);
            if (digits.Length >= 5)
            {
                return digits[..5];
            }
            return digits;
        },
        ["zip_plus4"] = static value =>
        {
            var digits = FilterDigits(value);
            if (digits.Length >= 9)
            {
                return $"{digits[..5]}-{digits.Substring(5, 4)}";
            }

            if (digits.Length >= 5)
            {
                return digits[..5];
            }

            return digits;
        },
        ["phone10"] = static value =>
        {
            var digits = FilterDigits(value);
            if (digits.Length > 10)
            {
                digits = digits[^10..];
            }
            return digits;
        }
    };

    public static IReadOnlyList<BuiltInTransformDefinition> Definitions => _definitions;

    public static bool IsKnown(string? key)
    {
        return !string.IsNullOrWhiteSpace(key) && _handlers.ContainsKey(key);
    }

    public static bool TryApply(string? key, string value, out string result)
    {
        if (!string.IsNullOrWhiteSpace(key) && _handlers.TryGetValue(key, out var handler))
        {
            result = handler(value);
            return true;
        }

        result = value;
        return false;
    }

    public static Transform CreateTransform(string key)
    {
        if (!IsKnown(key))
        {
            throw new ArgumentException($"Unknown built-in transform '{key}'", nameof(key));
        }

        return new Transform
        {
            Type = BuiltInType,
            Key = key,
            Pattern = key
        };
    }

    public static string? ResolveKey(Transform? transform)
    {
        if (transform == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(transform.Key) && IsKnown(transform.Key))
        {
            return transform.Key;
        }

        if (!string.IsNullOrWhiteSpace(transform.Pattern) && IsKnown(transform.Pattern))
        {
            return transform.Pattern;
        }

        if (!string.IsNullOrWhiteSpace(transform.Type))
        {
            if (string.Equals(transform.Type, BuiltInType, StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(transform.Pattern) && IsKnown(transform.Pattern)
                    ? transform.Pattern
                    : transform.Key;
            }

            if (IsKnown(transform.Type))
            {
                return transform.Type;
            }
        }

        return null;
    }

    private static string FilterDigits(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                buffer[index++] = ch;
            }
        }

        return new string(buffer[..index]);
    }
}
