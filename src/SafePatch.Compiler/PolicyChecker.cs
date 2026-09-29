using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SafePatch.Compiler;

public sealed record PolicyDiagnostic(string Code, string Message, int Line, int Column)
{
    public override string ToString() => $"({Line},{Column}) {Code}: {Message}";
}

/// <summary>
/// Walks compiled agent source and reports every construct or symbol outside the policy:
/// unsafe code, interop, attributes, <c>dynamic</c>, reflection gateways, finalizers, and any
/// reference to a framework type not on <see cref="ApiAllowList"/>.
/// </summary>
public static class PolicyChecker
{
    /// <param name="trees">The trees to check; all of the compilation's by default.</param>
    public static IReadOnlyList<PolicyDiagnostic> Check(CSharpCompilation compilation, IEnumerable<SyntaxTree>? trees = null)
    {
        var diagnostics = new List<PolicyDiagnostic>();
        foreach (var tree in trees ?? compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodesAndSelf())
            {
                if (ForbiddenSyntax(node) is { } reason) diagnostics.Add(At(node, "SP0001", reason));
                if (node is BaseTypeDeclarationSyntax declaration) CheckDeclaredType(model, declaration, diagnostics);
                if (node is ExpressionSyntax or TypeSyntax) CheckSymbol(model, node, compilation, diagnostics);
            }
        }
        return diagnostics
            .DistinctBy(d => (d.Code, d.Message, d.Line, d.Column))
            .OrderBy(d => d.Line).ThenBy(d => d.Column)
            .ToList();
    }

    private static string? ForbiddenSyntax(SyntaxNode node) => node switch
    {
        AttributeListSyntax => "attributes are not allowed",
        UnsafeStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax or FixedStatementSyntax => "unsafe code is not allowed",
        StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax => "stackalloc is not allowed",
        DestructorDeclarationSyntax => "finalizers are not allowed",
        TypeOfExpressionSyntax => "typeof is not allowed",
        MakeRefExpressionSyntax or RefTypeExpressionSyntax or RefValueExpressionSyntax => "undocumented reference operators are not allowed",
        MemberDeclarationSyntax member when member.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword) || m.IsKind(SyntaxKind.ExternKeyword))
            => "unsafe and extern members are not allowed",
        LocalFunctionStatementSyntax local when local.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword) || m.IsKind(SyntaxKind.ExternKeyword))
            => "unsafe and extern members are not allowed",
        _ when node.IsKind(SyntaxKind.ArgListExpression) => "__arglist is not allowed",
        _ => null,
    };

    private static void CheckDeclaredType(SemanticModel model, BaseTypeDeclarationSyntax declaration, List<PolicyDiagnostic> diagnostics)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type) return;
        var bases = type.Interfaces.AsEnumerable();
        if (type.BaseType is { } baseType) bases = bases.Append(baseType);
        foreach (var inherited in bases)
        {
            if (!IsAllowed(inherited, model.Compilation))
                diagnostics.Add(At(declaration, "SP0002", $"deriving from {inherited.ToDisplayString()} is not allowed"));
        }
    }

    private static void CheckSymbol(SemanticModel model, SyntaxNode node, Compilation compilation, List<PolicyDiagnostic> diagnostics)
    {
        var info = model.GetSymbolInfo(node);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is null)
        {
            if (model.GetTypeInfo(node).Type is IDynamicTypeSymbol) diagnostics.Add(At(node, "SP0003", "dynamic is not allowed"));
            return;
        }

        switch (symbol)
        {
            case IDynamicTypeSymbol:
                diagnostics.Add(At(node, "SP0003", "dynamic is not allowed"));
                return;
            case ITypeSymbol type when !IsAllowed(type, compilation):
                diagnostics.Add(At(node, "SP0004", $"type {type.ToDisplayString()} is not allowed"));
                return;
            case IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol:
                var owner = symbol is IMethodSymbol { ReducedFrom: { } extension } ? extension.ContainingType : symbol.ContainingType;
                if (owner is null) return;
                if (!IsAllowed(owner, compilation))
                    diagnostics.Add(At(node, "SP0004", $"{owner.ToDisplayString()}.{symbol.Name} is not allowed"));
                else if (!IsFromSource(owner, compilation) && ApiAllowList.IsDeniedMember(symbol))
                    diagnostics.Add(At(node, "SP0005", $"{owner.ToDisplayString()}.{symbol.Name} is not allowed"));
                if (symbol is IMethodSymbol method)
                {
                    foreach (var argument in method.TypeArguments.Where(a => !IsAllowed(a, compilation)))
                        diagnostics.Add(At(node, "SP0004", $"type argument {argument.ToDisplayString()} is not allowed"));
                }
                return;
        }
    }

    private static bool IsAllowed(ITypeSymbol type, Compilation compilation) => type switch
    {
        IArrayTypeSymbol array => IsAllowed(array.ElementType, compilation),
        ITypeParameterSymbol => true,
        IPointerTypeSymbol or IFunctionPointerTypeSymbol or IDynamicTypeSymbol => false,
        INamedTypeSymbol named => (IsFromSource(named, compilation) || ApiAllowList.IsAllowedType(named))
                                  && named.TypeArguments.All(a => IsAllowed(a, compilation)),
        _ => false,
    };

    private static bool IsFromSource(ITypeSymbol type, Compilation compilation) =>
        SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly);

    private static PolicyDiagnostic At(SyntaxNode node, string code, string message)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        return new PolicyDiagnostic(code, message, position.Line + 1, position.Character + 1);
    }
}
