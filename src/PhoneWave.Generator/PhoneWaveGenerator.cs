using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PropInfo = (bool Success, string Name, string PropertyType, string ParentType, string ParentTypeNamespace);

namespace PhoneWave.Generator
{
    [Generator(LanguageNames.CSharp)]
    public class PhoneWaveGenerator : IIncrementalGenerator
    {
        private static readonly PropInfo FailurePropInfo = (false, "", "", "", "");
        
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            
            IncrementalValuesProvider<PropInfo> enumDeclarations = context.SyntaxProvider
                .ForAttributeWithMetadataName("PhoneWave.TrackedAttribute",
                    predicate: static (s, _) => s is PropertyDeclarationSyntax p && p.Modifiers.Any(SyntaxKind.PartialKeyword), 
                    transform: static (ctx, _) => GetPropertyInfo(ctx))
                .Where(static m => m.Success);

            var grouped = enumDeclarations.Collect()
                .SelectMany((all, _) =>
                {
                    var groups = all.GroupBy(static m => (m.ParentTypeNamespace, m.ParentType));
                    return ImmutableArray<ClassInfo>.Empty.AddRange(groups.Select(static g => 
                        new ClassInfo(g.Key.ParentType, g.Key.ParentTypeNamespace, g.Select(x => (x.PropertyType, x.Name)).ToArray())));
                });
             
            context.RegisterSourceOutput(grouped,
                static (spc, source) =>
                {
                    try
                    {
                        ExecuteClass(spc, source);
                    }
                    catch (Exception e)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create("WTF001", "PhoneWaveGen", $"Failure: {e}", 
                            DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0));
                    }
                }); 
        }

        private static void ExecuteClass(SourceProductionContext ctx, ClassInfo classInfo)
        {
            var className = classInfo.ClassName;
            var classNamespace = classInfo.ClassNamespace;
            
            var propBuilder = new StringBuilder();
            var initBuilder = new StringBuilder();

            foreach (var property in classInfo.Properties)
            {
                var typeString = property.PropertyType;
                var propertyName = property.PropertyName;
                initBuilder.AppendLine($"__storageFor__{propertyName}.Initialize(this, \"{propertyName}\");");
                propBuilder.AppendLine($@"
        PhoneWave.PhoneWavePropertyStorage<{typeString}> __storageFor__{propertyName} = new();
        public partial {typeString} {propertyName} 
        {{ 
            get => __storageFor__{propertyName}.Value; 
            set => SetValue(__storageFor__{propertyName}, value); 
        }}");
            }

            var fileBody = $$"""
                             namespace {{classNamespace}} 
                             { 
                                 partial class {{className}} 
                                 {
                                    {{propBuilder}}
                                    
                                     protected override void InitializeStorage() 
                                     {
                                         {{initBuilder}}
                                         base.InitializeStorage();
                                     }
                                 }
                             }
                             """;
            ctx.AddSource($"{classNamespace}.{className}.g.cs", fileBody);
        }

        static PropInfo GetPropertyInfo(GeneratorAttributeSyntaxContext context)
        {
            if (context.TargetNode is not PropertyDeclarationSyntax declaration) return FailurePropInfo;
            if (declaration.Parent is not ClassDeclarationSyntax classDeclarationSyntax) return FailurePropInfo;
            if (context.SemanticModel.GetDeclaredSymbol(declaration) is not { } propertySymbol) return FailurePropInfo;
            var typeSymbol = propertySymbol.Type;
            var declaringType = propertySymbol.ContainingType;
            var typeString = typeSymbol.ToString();
            var name = declaration.Identifier.Text;
            var parentClassNamespace = GetNamespace(classDeclarationSyntax);
                //var parentClassName = classDeclarationSyntax.Identifier.Text;
            return (true, name, typeString, declaringType.Name, parentClassNamespace);
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
    
            // Keep moving "out" of nested classes etc. until we get to a namespace
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