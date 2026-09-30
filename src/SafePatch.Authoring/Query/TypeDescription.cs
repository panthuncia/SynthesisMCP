using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;
using SafePatch.Mutagen;

namespace SafePatch.Authoring.Query;

/// <summary>
/// A record type's fields, as Mutagen names them, with readable types: <c>link → Item</c>, <c>list of
/// LeveledItemEntry</c>. Nested objects are expanded with dotted paths (<c>Data.Damage</c>), and list elements with
/// <c>[]</c> (<c>Entries[].Data.Level</c>), a few levels deep.
/// </summary>
public static class TypeDescription
{
    private const int MaxDepth = 3;
    private const int MaxEnumValues = 24;

    public static IEnumerable<IReadOnlyList<string?>> Rows(Type classType) => Rows(classType, "", 0, [classType]);

    private static IEnumerable<IReadOnlyList<string?>> Rows(Type classType, string prefix, int depth, HashSet<Type> seen)
    {
        var registration = LoquiRegistration.GetRegister(classType);
        var children = RecordDiff.ChildFields(registration.ClassType);
        foreach (var name in RecordDiff.FieldNames(registration.ClassType))
        {
            var property = RecordText.FindProperty(registration.GetterType, name);
            if (property is null) continue;
            var type = property.PropertyType;
            if (children.Contains(name))
            {
                yield return [prefix + name, Name(type), "child records, each a record of its own"];
                continue;
            }
            yield return [prefix + name, Name(type), Details(type)];

            if (depth >= MaxDepth) continue;
            var element = Element(type);
            var nested = Loqui(element ?? type);
            if (nested is null || !seen.Add(nested)) continue;
            foreach (var row in Rows(nested, prefix + name + (element is null ? "." : "[]."), depth + 1, seen)) yield return row;
            seen.Remove(nested);
        }
    }

    /// <summary>A readable type name: <c>int</c>, <c>link → Item</c>, <c>list of LeveledItemEntry</c>, <c>Flag?</c>.</summary>
    public static string Name(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return Name(inner) + "?";
        if (type == typeof(string)) return "string";
        if (typeof(ITranslatedStringGetter).IsAssignableFrom(type)) return "string (translated)";
        if (type == typeof(FormKey)) return "FormKey";
        if (typeof(IFormLinkGetter).IsAssignableFrom(type))
        {
            var target = type.IsGenericType ? Strip(type.GetGenericArguments()[0]) : "record";
            return $"link → {target}" + (type.Name.Contains("Nullable", StringComparison.Ordinal) ? "?" : "");
        }
        if (type == typeof(ReadOnlyMemorySlice<byte>) || type == typeof(byte[])) return "bytes";
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return Keyword(type);
        if (Element(type) is { } element) return $"list of {Name(element)}";
        return Strip(type);
    }

    private static string Details(Type type)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;
        if (!bare.IsEnum) return "";
        var names = Enum.GetNames(bare);
        var flags = bare.IsDefined(typeof(FlagsAttribute), false) ? "flags: " : "one of: ";
        return flags + string.Join(", ", names.Take(MaxEnumValues)) + (names.Length > MaxEnumValues ? $", … +{names.Length - MaxEnumValues} more" : "");
    }

    /// <summary>The generated class of a nested Mutagen object type, if it is one (not a record: those are links).</summary>
    private static Type? Loqui(Type type)
    {
        if (!typeof(ILoquiObject).IsAssignableFrom(type) || typeof(IMajorRecordGetter).IsAssignableFrom(type)) return null;
        try
        {
            return LoquiRegistration.GetRegister(type)?.ClassType;
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Type? Element(Type type)
    {
        if (type == typeof(string) || type == typeof(ReadOnlyMemorySlice<byte>)) return null;
        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    /// <summary><c>ILeveledItemEntryGetter</c> → <c>LeveledItemEntry</c>.</summary>
    private static string Strip(Type type)
    {
        var name = type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] : type.Name;
        if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) && name.EndsWith("Getter", StringComparison.Ordinal)) name = name[1..^6];
        return name;
    }

    private static string Keyword(Type type) => type switch
    {
        _ when type == typeof(int) => "int",
        _ when type == typeof(uint) => "uint",
        _ when type == typeof(short) => "short",
        _ when type == typeof(ushort) => "ushort",
        _ when type == typeof(byte) => "byte",
        _ when type == typeof(sbyte) => "sbyte",
        _ when type == typeof(long) => "long",
        _ when type == typeof(ulong) => "ulong",
        _ when type == typeof(float) => "float",
        _ when type == typeof(double) => "double",
        _ when type == typeof(bool) => "bool",
        _ => type.Name,
    };
}
