using System.Collections;
using System.Globalization;
using System.Reflection;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;
using SafePatch.Mutagen;

namespace SafePatch.Authoring;

/// <summary>
/// Renders records and field values as compact one-line text, e.g.
/// <c>{Data: {Level: 1, Count: 1, Reference: 012E49:Skyrim.esm IronSword}}</c>, for any record type without code of its
/// own. Field names come from Mutagen's generated masks (<see cref="RecordDiff.FieldNames"/>), and links show the
/// EditorID <c>names</c> gives them (the load order index). Null fields are left out.
/// </summary>
public static class RecordText
{
    /// <summary>Items shown of a list nested inside a value; the rest are counted.</summary>
    public const int NestedListItems = 10;
    private const int MaxDepth = 6;

    /// <summary>One field of a record, by its Mutagen name (as in <c>RecordChange.Fields</c>), or null when it is unset.</summary>
    public static string? Field(IMajorRecordGetter record, string field, Func<FormKey, string?>? names = null) =>
        Value(FieldValue(record, field), names);

    /// <summary>A field's value, found on the record's getter interfaces.</summary>
    public static object? FieldValue(object owner, string field)
    {
        var property = FindProperty(owner, field)
                       ?? throw new ArgumentException($"{TypeName(owner)} has no field {field}.", nameof(field));
        return property.GetValue(owner);
    }

    /// <summary>A value as compact text, or null when it is unset.</summary>
    public static string? Value(object? value, Func<FormKey, string?>? names = null, int depth = 0)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return depth == 0 ? text : Quote(text);
            case ITranslatedStringGetter translated:
                return translated.String is { } s ? (depth == 0 ? s : Quote(s)) : null;
            case IFormLinkGetter link:
                return link.IsNull ? null : Link(link.FormKey, names);
            case FormKey formKey:
                return formKey.IsNull ? null : Link(formKey, names);
            case IMajorRecordGetter record when depth > 0:
                return $"{record.FormKey} {RecordDiff.TypeName(record)}";
            case Enum or bool:
                return value.ToString();
            case float f:
                return f.ToString("0.######", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("0.######", CultureInfo.InvariantCulture);
            case IFormattable formattable when value.GetType().IsPrimitive || value is decimal:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case ReadOnlyMemorySlice<byte> bytes:
                return Bytes(bytes);
            case byte[] bytes:
                return Bytes(bytes);
            case ILoquiObject loqui when depth < MaxDepth:
                return Object(loqui, names, depth);
            case IEnumerable items when depth < MaxDepth:
                var all = items.Cast<object?>().ToList();
                var shown = all.Take(NestedListItems).Select(i => Value(i, names, depth + 1) ?? "null");
                return "[" + string.Join(", ", shown) + (all.Count > NestedListItems ? $", … +{all.Count - NestedListItems} more" : "") + "]";
            default:
                return value.ToString();
        }
    }

    /// <summary>A link as its FormKey and, when it resolves, its EditorID: <c>012E49:Skyrim.esm IronSword</c>.</summary>
    public static string Link(FormKey formKey, Func<FormKey, string?>? names) =>
        names?.Invoke(formKey) is { Length: > 0 } editorId ? $"{formKey} {editorId}" : formKey.ToString();

    /// <summary>A field of a Mutagen object by name, looked up on its class and on the getter interfaces it implements.</summary>
    public static PropertyInfo? FindProperty(object owner, string name) => FindProperty(owner.GetType(), name)
        ?? (owner is ILoquiObject loqui ? FindProperty(loqui.Registration.GetterType, name) : null);

    public static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetInterfaces().Prepend(type)
            .Select(i => i.GetProperty(name, BindingFlags.Public | BindingFlags.Instance))
            .FirstOrDefault(p => p is not null && p.GetIndexParameters().Length == 0);

    /// <summary>A nested object as <c>{Field: value, …}</c>, its unset fields left out.</summary>
    private static string Object(ILoquiObject loqui, Func<FormKey, string?>? names, int depth)
    {
        IReadOnlyList<string> fieldNames;
        try
        {
            fieldNames = RecordDiff.FieldNames(loqui.Registration.ClassType);
        }
        catch (NotSupportedException)
        {
            return loqui.ToString() ?? "";
        }
        var fields = fieldNames
            .Select(name => (name, value: FindProperty(loqui, name)?.GetValue(loqui)))
            .Where(f => !IsDefault(f.value))
            .Select(f => (f.name, text: Value(f.value, names, depth + 1)))
            .Where(f => f.text is not null && f.text != "[]" && f.text != "{}")
            .Select(f => $"{f.name}: {f.text}");
        return "{" + string.Join(", ", fields) + "}";
    }

    /// <summary>
    /// Whether a value is unset or its type's default (0, false, an empty list or text): left out of records so an
    /// agent reads what a record sets, not every zero.
    /// </summary>
    public static bool IsDefault(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        ITranslatedStringGetter translated => string.IsNullOrEmpty(translated.String),
        IFormLinkGetter link => link.IsNull,
        ICollection items => items.Count == 0,
        _ when value.GetType().IsValueType => value.Equals(Activator.CreateInstance(value.GetType())),
        _ => false,
    };

    private static string TypeName(object owner) => owner is ILoquiObject loqui ? loqui.Registration.ClassType.Name : owner.GetType().Name;

    private static string Quote(string text) => "\"" + text.Replace("\"", "\\\"") + "\"";

    private static string Bytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length <= 16 ? $"0x{Convert.ToHexString(bytes)}" : $"<{bytes.Length} bytes>";
}
