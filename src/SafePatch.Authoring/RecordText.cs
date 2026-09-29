using System.Collections;
using System.Reflection;
using Loqui;
using Mutagen.Bethesda.Plugins.Records;
using Noggog.StructuredStrings;

namespace SafePatch.Authoring;

/// <summary>
/// Renders records and field values as text with Mutagen's generated printers, so every record type
/// reads the same way without code of its own. Long values are cut off.
/// </summary>
public static class RecordText
{
    public const int MaxChars = 20_000;

    public static string Print(IMajorRecordGetter record) => Truncate(Value(record) ?? "");

    /// <summary>One field of a record, by the name its getter interface uses (as in <c>RecordChange.Fields</c>).</summary>
    public static string? Field(IMajorRecordGetter record, string field)
    {
        var property = FindProperty(record.Registration.GetterType, field)
                       ?? throw new ArgumentException($"{record.Registration.ClassType.Name} has no field {field}.", nameof(field));
        return Value(property.GetValue(record)) is { } text ? Truncate(text) : null;
    }

    private static string? Value(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case IPrintable printable:
                var builder = new StructuredStringBuilder();
                printable.Print(builder, null);
                return builder.ToString();
            case IEnumerable items and not IReadOnlyDictionary<string, object>:
                return "[" + string.Join("; ", items.Cast<object?>().Select(Value)) + "]";
            default:
                return value.ToString();
        }
    }

    /// <summary>Getter interfaces declare their properties across a hierarchy of interfaces.</summary>
    private static PropertyInfo? FindProperty(Type getter, string name) =>
        getter.GetInterfaces().Prepend(getter).Select(i => i.GetProperty(name)).FirstOrDefault(p => p is not null);

    private static string Truncate(string text) => text.Length <= MaxChars ? text : text[..MaxChars] + $"… ({text.Length - MaxChars} more characters)";
}
