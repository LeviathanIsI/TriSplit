namespace TriSplit.Core.Models;

public static class MappingObjectTypes
{
    public const string Contact = "Contact";
    public const string PhoneNumber = "Phone Number";
    public const string Property = "Property";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Contact;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, PhoneNumber, System.StringComparison.OrdinalIgnoreCase))
        {
            return PhoneNumber;
        }

        if (string.Equals(trimmed, Property, System.StringComparison.OrdinalIgnoreCase))
        {
            return Property;
        }

        return Contact;
    }
}
