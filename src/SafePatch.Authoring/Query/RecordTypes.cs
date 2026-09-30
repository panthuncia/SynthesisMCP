using System.Reflection;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SafePatch.Host;

namespace SafePatch.Authoring.Query;

/// <summary>Skyrim's record types, by Mutagen class name (<c>LeveledItem</c>) or link interface (<c>Item</c>).</summary>
public static class RecordTypes
{
    private static readonly Lazy<IReadOnlyList<string>> Concrete = new(() =>
        [.. typeof(ISkyrimModGetter).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(ISkyrimMajorRecord).IsAssignableFrom(t) && t.Namespace == typeof(Npc).Namespace)
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)]);

    /// <summary>Every concrete record type.</summary>
    public static IReadOnlyList<string> All => Concrete.Value;

    /// <summary>The getter interface for a type name: a record class (<c>Npc</c>) or a link interface (<c>Item</c>, <c>Placeable</c>).</summary>
    public static Type Getter(string type)
    {
        var name = type.Trim();
        if (name.StartsWith('I') && name.EndsWith("Getter", StringComparison.Ordinal)) name = name[1..^"Getter".Length];
        return typeof(ISkyrimModGetter).Assembly.GetType($"Mutagen.Bethesda.Skyrim.I{name}Getter", throwOnError: false, ignoreCase: true) is { } getter
               && typeof(IMajorRecordGetter).IsAssignableFrom(getter)
            ? getter
            : throw new SafePatchException($"{type} is not a Skyrim record type. Use Mutagen's class name, e.g. LeveledItem, Npc, Weapon (describe_type lists them all).");
    }

    /// <summary>The generated class for a record type, for its field list.</summary>
    public static Type Class(string type)
    {
        var getter = Getter(type);
        var name = getter.Name[1..^"Getter".Length];
        return Concrete.Value.Contains(name)
            ? LoquiRegistration.GetRegister(getter).ClassType
            : throw new SafePatchException($"{type} is a group of record types, not one; describe one of them, e.g. Weapon for Item.");
    }

    public static ILoquiRegistration Registration(string type) => LoquiRegistration.GetRegister(Class(type));

    /// <summary>
    /// Each signature's record class (<c>WEAP</c> → Weapon). A few signatures hold several classes that Mutagen tells
    /// apart by content (<c>GMST</c>: GameSettingFloat, GameSettingInt...); those map to the classes' common base.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<uint, Type>> BySignature = new(() =>
    {
        var map = new Dictionary<uint, Type>();
        foreach (var name in Concrete.Value)
        {
            for (var classType = typeof(ISkyrimModGetter).Assembly.GetType($"Mutagen.Bethesda.Skyrim.{name}"); classType is not null && classType != typeof(object); classType = classType.BaseType)
            {
                if (TriggeringSignature(classType) is not { } signature) continue;
                map[signature] = map.TryGetValue(signature, out var existing) && existing != classType ? Common(existing, classType) : classType;
                break;
            }
        }
        return map;
    });

    private static uint? TriggeringSignature(Type classType)
    {
        Type registration;
        try
        {
            registration = LoquiRegistration.GetRegister(classType).GetType();
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
        var member = (object?)registration.GetField("TriggeringRecordType", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                     ?? registration.GetProperty("TriggeringRecordType", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return member is RecordType recordType ? (uint)recordType.TypeInt : null;
    }

    private static Type Common(Type a, Type b)
    {
        for (var type = a; type is not null; type = type.BaseType)
        {
            if (type.IsAssignableFrom(b)) return type;
        }
        return typeof(object);
    }

    /// <summary>A record's signature, from its class (an overlay's included).</summary>
    public static uint SignatureOf(IMajorRecordGetter record) =>
        SignatureByClass.GetOrAdd(record.GetType(), type =>
        {
            for (var classType = LoquiRegistration.GetRegister(type).ClassType; classType is not null && classType != typeof(object); classType = classType.BaseType)
            {
                if (TriggeringSignature(classType) is { } signature) return signature;
            }
            throw new SafePatchException($"Mutagen names no record type for {type.Name}.");
        });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, uint> SignatureByClass = new();

    /// <summary>The record class for a signature (a common base for a shared one), or null for one Mutagen does not model.</summary>
    public static Type? ClassOf(uint signature) => BySignature.Value.GetValueOrDefault(signature);

    /// <summary>The getter interface Mutagen reads a signature's records through.</summary>
    public static Type? GetterOf(uint signature) =>
        ClassOf(signature) is { } classType ? LoquiRegistration.GetRegister(classType).GetterType : null;

    /// <summary>Whether several record classes share a signature, so a record must be read to know its class.</summary>
    public static bool IsShared(uint signature) => ClassOf(signature) is { IsAbstract: true };

    /// <summary>The signatures of every record class a type name covers: one for a class, several for a link interface such as Item.</summary>
    public static IReadOnlySet<uint> Signatures(string type)
    {
        var getter = Getter(type);
        return BySignature.Value
            .Where(p => GetterOf(p.Key) is { } mapped && (getter.IsAssignableFrom(mapped) || mapped.IsAssignableFrom(getter)))
            .Select(p => p.Key).ToHashSet();
    }
}
