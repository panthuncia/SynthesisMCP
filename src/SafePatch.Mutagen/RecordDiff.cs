using System.Collections.Concurrent;
using System.Linq.Expressions;
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
        var differ = Differs.GetOrAdd(Registration(before).ClassType, BuildDiffer);
        var mask = differ.Mask(before, after);
        var changed = new List<string>();
        foreach (var (name, isEqual) in differ.Fields)
        {
            if (!isEqual(mask)) changed.Add(name);
        }
        return changed;
    }

    /// <summary>
    /// A record type's comparison, compiled once: a call to its generated equals mask, and for each compared field a
    /// test of whether the mask says it is equal (a <c>bool</c>, or the <c>Overall</c> of a sub-object or list mask).
    /// Replaces reflection on every comparison, which the host's commit and conflict analysis both do per record.
    /// </summary>
    private sealed record Differ(Func<IMajorRecordGetter, IMajorRecordGetter, object> Mask, IReadOnlyList<(string Name, Func<object, bool> IsEqual)> Fields);

    private static readonly ConcurrentDictionary<Type, Differ> Differs = new();

    private static Differ BuildDiffer(Type classType)
    {
        var method = FindEqualsMask(classType);
        var getter = method.GetParameters()[0].ParameterType;
        var include = Enum.Parse(method.GetParameters()[2].ParameterType, "All");
        var before = Expression.Parameter(typeof(IMajorRecordGetter), "before");
        var after = Expression.Parameter(typeof(IMajorRecordGetter), "after");
        var call = Expression.Call(method, Expression.Convert(before, getter), Expression.Convert(after, getter), Expression.Constant(include));
        var mask = Expression.Lambda<Func<IMajorRecordGetter, IMajorRecordGetter, object>>(Expression.Convert(call, typeof(object)), before, after).Compile();

        var maskType = method.ReturnType;
        var children = ChildFields(classType);
        var fields = new List<(string, Func<object, bool>)>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        IEnumerable<MemberInfo> members = maskType.GetFields(flags).Cast<MemberInfo>()
            .Concat(maskType.GetProperties(flags).Where(p => p.GetIndexParameters().Length == 0));
        foreach (var member in members)
        {
            if (Bookkeeping.Contains(member.Name) || children.Contains(member.Name)) continue;
            fields.Add((member.Name, CompileIsEqual(maskType, member)));
        }
        return new Differ(mask, fields);
    }

    /// <summary>Whether a mask entry says its field is equal: true for an unset entry, as before.</summary>
    private static Func<object, bool> CompileIsEqual(Type maskType, MemberInfo member)
    {
        var parameter = Expression.Parameter(typeof(object), "mask");
        var value = Expression.MakeMemberAccess(Expression.Convert(parameter, maskType), member);
        return Expression.Lambda<Func<object, bool>>(IsEqual(value), parameter).Compile();
    }

    /// <summary>Whether one mask entry says equal. An unset (null) entry does.</summary>
    private static Expression IsEqual(Expression value)
    {
        if (value.Type == typeof(bool)) return value;
        Expression body;
        if (value.Type.IsGenericType && value.Type.GetGenericTypeDefinition() == typeof(GenderedItem<>))
        {
            // A gendered field's mask: one entry for each gender.
            body = Expression.AndAlso(IsEqual(Expression.Property(value, "Male")), IsEqual(Expression.Property(value, "Female")));
        }
        else if (value.Type.GetField("Overall") is { FieldType: var overallType } overall && overallType == typeof(bool))
        {
            // MaskItem<bool, TSubMask>: Overall covers the whole sub-object or list.
            body = Expression.Field(value, overall);
        }
        else
        {
            return value.Type.IsValueType && Nullable.GetUnderlyingType(value.Type) is null
                ? Expression.Constant(false)
                : Expression.Equal(value, Expression.Constant(null, value.Type));
        }
        return value.Type.IsValueType
            ? body
            : Expression.Condition(Expression.Equal(value, Expression.Constant(null, value.Type)), Expression.Constant(true), body);
    }

    /// <summary>
    /// The field names of a generated Mutagen class (a record or a nested object such as <c>LeveledItemEntry</c>),
    /// base-class fields first, as its masks name them. These are the names <see cref="ChangedFields"/> reports
    /// and manifests use. Header bookkeeping is left out.
    /// </summary>
    public static IReadOnlyList<string> FieldNames(Type classType) => FieldNameCache.GetOrAdd(classType, type =>
    {
        var registration = LoquiRegistration.GetRegister(type);
        var mask = registration.ClassType.GetNestedType("Mask`1")?.MakeGenericType(typeof(bool))
                   ?? throw new NotSupportedException($"Mutagen generated no mask for {type.Name}.");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        IEnumerable<string> Names(Type t) =>
            (t.BaseType is { } parent && parent != typeof(object) ? Names(parent) : [])
            .Concat(t.GetFields(flags | BindingFlags.DeclaredOnly).Select(f => f.Name))
            .Concat(t.GetProperties(flags | BindingFlags.DeclaredOnly).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name));
        return [.. Names(mask).Where(n => !Bookkeeping.Contains(n) && n != "Specified").Distinct()];
    });

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<string>> FieldNameCache = new();

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

    /// <summary>
    /// Whether two generated objects (records, or parts such as <c>RankPlacement</c>) hold the same values, by their
    /// equals mask. Their own <c>Equals</c> is not used: in Mutagen 0.54.4 it compares lists of byte arrays, and
    /// gendered fields, by where they are rather than what they hold, so two reads of the same bytes can differ.
    /// </summary>
    public static bool ValuesEqual(ILoquiObject a, ILoquiObject b) =>
        a.Registration.ClassType == b.Registration.ClassType
        && ValueComparers.GetOrAdd(a.Registration.ClassType, BuildValueComparer)(a, b);

    private static readonly ConcurrentDictionary<Type, Func<object, object, bool>> ValueComparers = new();

    private static Func<object, object, bool> BuildValueComparer(Type classType)
    {
        var method = FindEqualsMask(classType);
        var getter = method.GetParameters()[0].ParameterType;
        var include = Enum.Parse(method.GetParameters()[2].ParameterType, "All");
        var a = Expression.Parameter(typeof(object), "a");
        var b = Expression.Parameter(typeof(object), "b");
        var mask = Expression.Call(method, Expression.Convert(a, getter), Expression.Convert(b, getter), Expression.Constant(include));
        var all = mask.Type.GetMethod("All", [typeof(Func<bool, bool>)])
                  ?? throw new NotSupportedException($"Mutagen generated no All on {mask.Type.Name}.");
        Func<bool, bool> isTrue = x => x;
        var body = Expression.Call(mask, all, Expression.Constant(isTrue));
        return Expression.Lambda<Func<object, object, bool>>(body, a, b).Compile();
    }

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
