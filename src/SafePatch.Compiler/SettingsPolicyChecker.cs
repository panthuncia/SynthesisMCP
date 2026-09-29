using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SafePatch.Compiler;

/// <summary>
/// Checks a settings source: the classes Synthesis shows in its settings GUI. Unlike the program,
/// this code is compiled into the trusted patcher (Synthesis reflects over it there, and
/// instantiates it in its own process), so it may hold data only: public classes and enums whose
/// members are auto-properties or fields with constant initial values, of plain data types, with
/// Synthesis's settings attributes. No methods, constructors, static state or expressions that run code.
/// </summary>
public static class SettingsPolicyChecker
{
    public const string AttributeNamespace = "Mutagen.Bethesda.Synthesis.Settings";

    /// <summary>Framework types a setting may have, by metadata name.</summary>
    private static readonly HashSet<string> DataTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Char", "System.String", "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Decimal",
        "System.Nullable`1",
        "System.Collections.Generic.List`1", "System.Collections.Generic.HashSet`1", "System.Collections.Generic.Dictionary`2",
    };

    /// <summary>Mutagen types a setting may have: record identifiers and links.</summary>
    private static readonly HashSet<string> MutagenDataTypes = new(StringComparer.Ordinal)
    {
        "Mutagen.Bethesda.Plugins.FormKey", "Mutagen.Bethesda.Plugins.ModKey",
        "Mutagen.Bethesda.Plugins.FormLink`1", "Mutagen.Bethesda.Plugins.FormLinkNullable`1",
        "Mutagen.Bethesda.Plugins.IFormLink`1", "Mutagen.Bethesda.Plugins.IFormLinkGetter`1",
        "Mutagen.Bethesda.Plugins.IFormLinkNullable`1", "Mutagen.Bethesda.Plugins.IFormLinkNullableGetter`1",
    };

    /// <summary>Static factories a setting's initial value may call, with constant arguments.</summary>
    private static readonly HashSet<string> Factories = new(StringComparer.Ordinal)
    {
        "Mutagen.Bethesda.Plugins.FormKey.Factory", "Mutagen.Bethesda.Plugins.ModKey.FromFileName",
        "Mutagen.Bethesda.Plugins.ModKey.FromNameAndExtension",
    };

    public static IReadOnlyList<PolicyDiagnostic> Check(Compilation compilation, SyntaxTree tree)
    {
        var model = compilation.GetSemanticModel(tree);
        var diagnostics = new List<PolicyDiagnostic>();
        var root = (CompilationUnitSyntax)tree.GetRoot();
        if (root.AttributeLists.Count > 0 || root.Externs.Count > 0)
            diagnostics.Add(At(root, "SP0300", "settings may not declare assembly attributes or extern aliases"));
        foreach (var typeOf in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
            diagnostics.Add(At(typeOf, "SP0306", "typeof is not allowed in settings"));

        foreach (var member in root.Members.SelectMany(Flatten))
        {
            switch (member)
            {
                case EnumDeclarationSyntax @enum:
                    CheckAttributes(model, @enum.AttributeLists, diagnostics);
                    foreach (var value in @enum.Members)
                    {
                        CheckAttributes(model, value.AttributeLists, diagnostics);
                        if (value.EqualsValue is { } equals && equals.Value is not LiteralExpressionSyntax)
                            diagnostics.Add(At(equals, "SP0301", "enum values must be literals"));
                    }
                    break;
                case ClassDeclarationSyntax @class:
                    CheckClass(model, @class, diagnostics);
                    break;
                default:
                    diagnostics.Add(At(member, "SP0300", "settings may only declare classes and enums"));
                    break;
            }
        }
        return diagnostics.OrderBy(d => d.Line).ThenBy(d => d.Column).ToList();
    }

    private static IEnumerable<MemberDeclarationSyntax> Flatten(MemberDeclarationSyntax member) =>
        member is BaseNamespaceDeclarationSyntax ns ? ns.Members.SelectMany(Flatten) : [member];

    private static void CheckClass(SemanticModel model, ClassDeclarationSyntax @class, List<PolicyDiagnostic> diagnostics)
    {
        CheckAttributes(model, @class.AttributeLists, diagnostics);
        if (!@class.Modifiers.Any(SyntaxKind.PublicKeyword) || @class.Modifiers.Any(m => !m.IsKind(SyntaxKind.PublicKeyword) && !m.IsKind(SyntaxKind.SealedKeyword)))
            diagnostics.Add(At(@class, "SP0302", $"settings class {@class.Identifier} must be public, and at most sealed"));
        if (@class.TypeParameterList is not null || @class.BaseList is not null || @class.ParameterList is not null)
            diagnostics.Add(At(@class, "SP0302", $"settings class {@class.Identifier} may not be generic, have a base type or a primary constructor"));

        foreach (var member in @class.Members)
        {
            switch (member)
            {
                case PropertyDeclarationSyntax property:
                    CheckAttributes(model, property.AttributeLists, diagnostics);
                    CheckDataMember(member, property.Modifiers, diagnostics);
                    if (property.ExpressionBody is not null || property.AccessorList is null
                        || property.AccessorList.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null || a.Modifiers.Count > 0 || a.AttributeLists.Count > 0)
                        || !property.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)))
                        diagnostics.Add(At(property, "SP0303", $"setting {property.Identifier} must be an auto-property ({{ get; set; }})"));
                    CheckDataType(model, property.Type, diagnostics);
                    if (property.Initializer is { } initializer) CheckInitialValue(model, initializer.Value, diagnostics);
                    break;
                case FieldDeclarationSyntax field:
                    CheckAttributes(model, field.AttributeLists, diagnostics);
                    CheckDataMember(member, field.Modifiers, diagnostics);
                    CheckDataType(model, field.Declaration.Type, diagnostics);
                    foreach (var variable in field.Declaration.Variables)
                        if (variable.Initializer is { } fieldInitializer) CheckInitialValue(model, fieldInitializer.Value, diagnostics);
                    break;
                default:
                    diagnostics.Add(At(member, "SP0303", "settings classes may only have properties and fields"));
                    break;
            }
        }
    }

    private static void CheckDataMember(MemberDeclarationSyntax member, SyntaxTokenList modifiers, List<PolicyDiagnostic> diagnostics)
    {
        if (modifiers.Count != 1 || !modifiers[0].IsKind(SyntaxKind.PublicKeyword))
            diagnostics.Add(At(member, "SP0303", "settings members must be public instance members with no other modifiers"));
    }

    private static void CheckDataType(SemanticModel model, TypeSyntax syntax, List<PolicyDiagnostic> diagnostics)
    {
        if (model.GetTypeInfo(syntax).Type is not { } type || !IsDataType(type, model.Compilation))
            diagnostics.Add(At(syntax, "SP0304", $"type {syntax} is not a settings data type"));
    }

    private static bool IsDataType(ITypeSymbol type, Compilation compilation) => type switch
    {
        IArrayTypeSymbol array => IsDataType(array.ElementType, compilation),
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => true,
        INamedTypeSymbol named when SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, compilation.Assembly)
            => named.TypeKind == TypeKind.Class && named.TypeArguments.Length == 0,
        INamedTypeSymbol named when MutagenDataTypes.Contains(MetadataName(named))
            => named.TypeArguments.All(IsRecordType),
        INamedTypeSymbol named => DataTypes.Contains(MetadataName(named)) && named.TypeArguments.All(a => IsDataType(a, compilation)),
        _ => false,
    };

    /// <summary>A link's target: a Mutagen record getter interface.</summary>
    private static bool IsRecordType(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Interface } named
        && named.ContainingNamespace.ToDisplayString().StartsWith("Mutagen.Bethesda.", StringComparison.Ordinal);

    /// <summary>
    /// Literals, enum members, and <c>new</c> of settings data types whose arguments and
    /// initializers are themselves initial values; plus a few pure Mutagen factories.
    /// </summary>
    private static void CheckInitialValue(SemanticModel model, ExpressionSyntax value, List<PolicyDiagnostic> diagnostics)
    {
        switch (value)
        {
            case LiteralExpressionSyntax or DefaultExpressionSyntax:
                return;
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax }:
                return;
            case MemberAccessExpressionSyntax when model.GetSymbolInfo(value).Symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum }:
                return;
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseOrExpression } or:
                CheckInitialValue(model, or.Left, diagnostics);
                CheckInitialValue(model, or.Right, diagnostics);
                return;
            case ParenthesizedExpressionSyntax parenthesized:
                CheckInitialValue(model, parenthesized.Expression, diagnostics);
                return;
            case BaseObjectCreationExpressionSyntax creation:
                if (model.GetTypeInfo(creation).Type is not { } created || !IsDataType(created, model.Compilation))
                    diagnostics.Add(At(creation, "SP0305", $"creating {model.GetTypeInfo(creation).Type?.ToDisplayString() ?? "this"} is not allowed in a setting"));
                foreach (var argument in creation.ArgumentList?.Arguments ?? [])
                    CheckInitialValue(model, argument.Expression, diagnostics);
                foreach (var element in creation.Initializer?.Expressions ?? [])
                    CheckInitialValue(model, element is AssignmentExpressionSyntax assignment ? assignment.Right : element, diagnostics);
                return;
            case CollectionExpressionSyntax collection:
                foreach (var element in collection.Elements)
                {
                    if (element is ExpressionElementSyntax expression) CheckInitialValue(model, expression.Expression, diagnostics);
                    else diagnostics.Add(At(element, "SP0305", "only plain elements are allowed in a setting's initial value"));
                }
                return;
            case InitializerExpressionSyntax initializer:
                foreach (var element in initializer.Expressions) CheckInitialValue(model, element, diagnostics);
                return;
            case InvocationExpressionSyntax invocation
                when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { IsStatic: true } method
                     && Factories.Contains($"{MetadataName(method.ContainingType)}.{method.Name}"):
                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (argument.Expression is not LiteralExpressionSyntax)
                        diagnostics.Add(At(argument, "SP0305", "factory arguments must be literals"));
                }
                return;
            default:
                diagnostics.Add(At(value, "SP0305", $"'{value}' is not allowed as a setting's initial value"));
                return;
        }
    }

    private static void CheckAttributes(SemanticModel model, SyntaxList<AttributeListSyntax> lists, List<PolicyDiagnostic> diagnostics)
    {
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var type = model.GetSymbolInfo(attribute).Symbol?.ContainingType;
            if (type?.ContainingNamespace.ToDisplayString() != AttributeNamespace)
                diagnostics.Add(At(attribute, "SP0306", $"only attributes from {AttributeNamespace} are allowed in settings"));
            foreach (var argument in attribute.ArgumentList?.Arguments ?? [])
            {
                if (argument.Expression is not (LiteralExpressionSyntax or MemberAccessExpressionSyntax))
                    diagnostics.Add(At(argument, "SP0306", "attribute arguments must be literals or enum members"));
            }
        }
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        return definition.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{definition.MetadataName}" : definition.MetadataName;
    }

    private static PolicyDiagnostic At(SyntaxNode node, string code, string message)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        return new PolicyDiagnostic(code, message, position.Line + 1, position.Character + 1);
    }
}
