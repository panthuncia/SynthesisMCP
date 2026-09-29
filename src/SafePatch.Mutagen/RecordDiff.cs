using System.Collections.Concurrent;
using System.Reflection;
using Loqui;
using Loqui.Internal;
using Mutagen.Bethesda.Plugins.Records;

namespace SafePatch.Mutagen;

/// <summary>
/// Field-level comparison of two versions of a record using the equals masks Mutagen generates
/// for every record type (<c>&lt;Type&gt;MixIn.GetEqualsMask</c>). Field names are Mutagen's property
/// names, so policies track Mutagen automatically.
/// <para>
/// Records that contain other records (cells, worldspaces, dialog topics) are compared without
/// their child fields: each child is a record of its own and is compared on its own. Child fields
/// are found from the record's type, never listed by name.
/// </para>
/// </summary>
public static class RecordDiff
{
    /// <summary>Header bookkeeping that serialization may renormalise; never treated as a change.</summary>
    private static readonly HashSet<string> Bookkeeping = new(StringComparer.Ordinal) { "FormKey", "VersionControl", "FormVersion", "Version2" };

    private static readonly ConcurrentDictionary<Type, MethodInfo> EqualsMaskMethods = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlySet<string>> ChildFieldCache = new();
    private static readonly ConcurrentDictionary<Type, object?> ChildlessCopyMasks = new();

    private static readonly MethodInfo DeepCopyIn = typeof(MajorRecordMixIn).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(MajorRecordMixIn.DeepCopyIn)
                     && m.GetParameters() is [_, _, var e, var c]
                     && e.ParameterType == typeof(ErrorMaskBuilder) && c.ParameterType == typeof(TranslationCrystal));

    /// <summary>The record's type name, e.g. <c>LeveledItem</c>.</summary>
    public static string TypeName(IMajorRecordGetter record) => Registration(record).ClassType.Name;

    /// <summary>Names of the fields that differ between two versions of the same record, excluding child records.</summary>
    public static IReadOnlyList<string> ChangedFields(IMajorRecordGetter before, IMajorRecordGetter after)
    {
        var classType = Registration(before).ClassType;
        var method = EqualsMaskMethods.GetOrAdd(classType, FindEqualsMask);
        var include = Enum.Parse(method.GetParameters()[2].ParameterType, "All");
        var mask = method.Invoke(null, [before, after, include])!;
        var children = ChildFields(classType);

        var changed = new List<string>();
        foreach (var (name, value) in MaskEntries(mask))
        {
            if (Bookkeeping.Contains(name) || children.Contains(name)) continue;
            var equal = value switch
            {
                bool b => b,
                null => true,
                // MaskItem<bool, TSubMask>: Overall covers the whole sub-object or list.
                var item => item.GetType().GetField("Overall")?.GetValue(item) is true,
            };
            if (!equal) changed.Add(name);
        }
        return changed;
    }

    /// <summary>
    /// The per-field entries of an equals mask. Generated masks expose some entries as fields and
    /// some (sub-object masks) as properties.
    /// </summary>
    private static IEnumerable<(string Name, object? Value)> MaskEntries(object mask)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        foreach (var field in mask.GetType().GetFields(flags))
            yield return (field.Name, field.GetValue(mask));
        foreach (var property in mask.GetType().GetProperties(flags).Where(p => p.GetIndexParameters().Length == 0))
            yield return (property.Name, property.GetValue(mask));
    }

    /// <summary>
    /// Fields of <paramref name="classType"/> that hold other major records, directly or through
    /// lists and group-like objects (e.g. <c>Cell.Temporary</c>, <c>Worldspace.SubCells</c>).
    /// </summary>
    public static IReadOnlySet<string> ChildFields(Type classType) => ChildFieldCache.GetOrAdd(classType, type =>
    {
        var getter = LoquiRegistration.GetRegister(type).GetterType;
        return AllProperties(getter)
            .Where(p => HoldsRecords(p.PropertyType, []))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
    });

    /// <summary>
    /// Copies <paramref name="source"/>'s own fields into <paramref name="target"/>, leaving the
    /// target's child records alone (they are copied as records of their own).
    /// </summary>
    public static void CopyIn(IMajorRecord target, IMajorRecordGetter source)
    {
        var crystal = ChildlessCopyMasks.GetOrAdd(Registration(source).ClassType, BuildChildlessCrystal);
        DeepCopyIn.Invoke(null, [target, source, null, crystal]);
    }

    private static object? BuildChildlessCrystal(Type classType)
    {
        var children = ChildFields(classType);
        if (children.Count == 0) return null;

        var maskType = classType.GetNestedType("TranslationMask")
                       ?? throw new NotSupportedException($"Mutagen generated no translation mask for {classType.Name}.");
        var mask = Activator.CreateInstance(maskType, true, true)!;
        foreach (var name in children)
        {
            var field = maskType.GetField(name) ?? throw new NotSupportedException($"{classType.Name}.{name} has no translation mask field.");
            field.SetValue(mask, field.FieldType == typeof(bool) ? false : Activator.CreateInstance(field.FieldType, false, false));
        }
        return maskType.GetMethod("GetCrystal", Type.EmptyTypes)!.Invoke(mask, null);
    }

    private static bool HoldsRecords(Type type, HashSet<Type> seen)
    {
        if (typeof(IMajorRecordGetter).IsAssignableFrom(type)) return true;
        if (type == typeof(string) || !seen.Add(type)) return false;

        var element = type.IsArray ? type.GetElementType() : EnumerableElement(type);
        if (element is not null) return HoldsRecords(element, seen);
        // Group-like containers (cell blocks, worldspace blocks) are generated Loqui objects.
        return typeof(ILoquiObject).IsAssignableFrom(type) && AllProperties(type).Any(p => HoldsRecords(p.PropertyType, seen));
    }

    private static Type? EnumerableElement(Type type) =>
        (type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? type : null)
        ?.GetGenericArguments()[0]
        ?? type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];

    private static IEnumerable<PropertyInfo> AllProperties(Type type) =>
        type.IsInterface
            ? type.GetInterfaces().Prepend(type).SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .DistinctBy(p => p.Name)
            : type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static MethodInfo FindEqualsMask(Type classType)
    {
        var registration = LoquiRegistration.GetRegister(classType);
        var mixIn = classType.Assembly.GetType(classType.FullName + "MixIn");
        return mixIn?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                   .SingleOrDefault(m => m.Name == "GetEqualsMask" && m.GetParameters() is [var a, var b, _]
                                         && a.ParameterType == registration.GetterType && b.ParameterType == registration.GetterType)
               ?? throw new NotSupportedException($"Mutagen generated no equals mask for {classType.Name}.");
    }

    private static ILoquiRegistration Registration(IMajorRecordGetter record) => ((ILoquiObject)record).Registration;
}
