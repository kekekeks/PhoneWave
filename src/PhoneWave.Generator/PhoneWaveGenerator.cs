using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PhoneWave.Generator
{
    
    [Generator(LanguageNames.CSharp)]
    public class PhoneWaveGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<ClassDeclarationSyntax> enumDeclarations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => s is ClassDeclarationSyntax { AttributeLists.Count: > 0 }, 
                    transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
                .Where(static m => m is not null)!;
            
            IncrementalValueProvider<(Compilation, ImmutableArray<ClassDeclarationSyntax>)> compilationAndEnums
                = context.CompilationProvider.Combine(enumDeclarations.Collect());
            
            context.RegisterSourceOutput(compilationAndEnums,
                static (spc, source) =>
                {
                    try
                    {
                        Execute(source.Item1, source.Item2, spc);
                    }
                    catch (Exception e)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create("WTF001", "PhoneWaveGen", $"Failure: {e.ToString()}", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0));
                    }
                });
        }

        private static void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> declarations, SourceProductionContext ctx)
        {
            if (declarations.IsDefaultOrEmpty)
                return;
            INamedTypeSymbol trackPropertyAttribute = compilation.GetTypeByMetadataName("PhoneWave.TrackPropertyAttribute")!;

            
            
            foreach (var sourceClass in declarations.Distinct())
            {
                var classCode = "";
                var initCode = "";
                
                var model = compilation.GetSemanticModel(sourceClass.SyntaxTree);
                var symbol = ModelExtensions.GetDeclaredSymbol(model, sourceClass);

                foreach (var attr in symbol!.GetAttributes())
                {
                    if (trackPropertyAttribute.Equals(attr.AttributeClass, SymbolEqualityComparer.Default))
                    {
                        var typeArg = (INamedTypeSymbol)attr.ConstructorArguments[0].Value;
                        var nameArg = (string)attr.ConstructorArguments[1].Value;
                        var displayNameArg = (string?)attr.ConstructorArguments[2].Value ?? nameArg;
                        var displayName = displayNameArg == nameArg 
                            ? "" 
                            : $"[System.ComponentModel.DisplayName(\"{displayNameArg}\")]{Environment.NewLine}";
                        var descriptionArg = (string)attr.ConstructorArguments[3].Value;
                        var description = string.IsNullOrWhiteSpace(descriptionArg) 
                            ? "" 
                            : $"[System.ComponentModel.Description(\"{descriptionArg}\")]{Environment.NewLine}";
                        classCode += $@"
PhoneWave.PhoneWavePropertyStorage<{typeArg}> __storageFor__{nameArg} = new();
public partial {typeArg} {nameArg} {{ get; set; }}

{displayName}{description}public partial {typeArg} {nameArg}
{{
    get => __storageFor__{nameArg}.Value;
    set => SetValue(__storageFor__{nameArg}, value);
}}
";
                        initCode += $"__storageFor__{nameArg}.Initialize(this, \"{nameArg}\");\n";
                    }
                }

                if (!string.IsNullOrWhiteSpace(classCode))
                {
                    var ns = GetNamespace(sourceClass);
                    classCode = $@"
namespace {ns}
{{
    partial class {sourceClass.Identifier}
    {{
        {classCode}
        protected override void InitializeStorage()
        {{
            {initCode}
            base.InitializeStorage();
        }}
    }}
}}";
                    ctx.AddSource(ns + "." + sourceClass.Identifier + ".g.cs", classCode);
                }
            }

            /*
            // Convert each EnumDeclarationSyntax to an EnumToGenerate
            List<EnumToGenerate> enumsToGenerate = GetTypesToGenerate(compilation, distinctEnums, context.CancellationToken);

            // If there were errors in the EnumDeclarationSyntax, we won't create an
            // EnumToGenerate for it, so make sure we have something to generate
            if (enumsToGenerate.Count > 0)
            {
                // generate the source code and add it to the output
                string result = SourceGenerationHelper.GenerateExtensionClass(enumsToGenerate);
                context.AddSource("EnumExtensions.g.cs", SourceText.From(result, Encoding.UTF8));
            }*/
        }

        static ClassDeclarationSyntax? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
        {
            var declaration = (ClassDeclarationSyntax)context.Node;
            
            foreach (AttributeListSyntax attributeListSyntax in declaration.AttributeLists)
            {
                foreach (AttributeSyntax attributeSyntax in attributeListSyntax.Attributes)
                {
                    if (ModelExtensions.GetSymbolInfo(context.SemanticModel, attributeSyntax).Symbol is not IMethodSymbol attributeSymbol)
                        continue;

                    var attributeContainingTypeSymbol = attributeSymbol.ContainingType;
                    string fullName = attributeContainingTypeSymbol.ToDisplayString();
                    
                    if (fullName == "PhoneWave.TrackPropertyAttribute")
                        return declaration;
                }
            }

            return null;
        }   
// determine the namespace the class/enum/struct is declared in, if any
        static string GetNamespace(BaseTypeDeclarationSyntax syntax)
        {
            // If we don't have a namespace at all we'll return an empty string
            // This accounts for the "default namespace" case
            string nameSpace = string.Empty;

            // Get the containing syntax node for the type declaration
            // (could be a nested type, for example)
            SyntaxNode? potentialNamespaceParent = syntax.Parent;
    
            // Keep moving "out" of nested classes etc until we get to a namespace
            // or until we run out of parents
            while (potentialNamespaceParent != null &&
                   potentialNamespaceParent is not NamespaceDeclarationSyntax
                   && potentialNamespaceParent is not FileScopedNamespaceDeclarationSyntax)
            {
                potentialNamespaceParent = potentialNamespaceParent.Parent;
            }

            // Build up the final namespace by looping until we no longer have a namespace declaration
            if (potentialNamespaceParent is BaseNamespaceDeclarationSyntax namespaceParent)
            {
                // We have a namespace. Use that as the type
                nameSpace = namespaceParent.Name.ToString();
        
                // Keep moving "out" of the namespace declarations until we 
                // run out of nested namespace declarations
                while (true)
                {
                    if (namespaceParent.Parent is not NamespaceDeclarationSyntax parent)
                    {
                        break;
                    }

                    // Add the outer namespace as a prefix to the final namespace
                    nameSpace = $"{namespaceParent.Name}.{nameSpace}";
                    namespaceParent = parent;
                }
            }

            // return the final namespace
            return nameSpace;
        }

    }
}