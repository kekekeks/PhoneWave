using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VerifyTests;
using VerifyXunit;

namespace PhoneWave.Generator.Tests
{

    public class TestHelper
    {
        [ModuleInitializer]
        public static void Init()
        {
            /*
            VerifierSettings.DerivePathInfo((string file, string directory, Type type, MethodInfo method)=>
            {
                var dir = directory;
                while (!File.Exists(Path.Combine(dir, "PhoneWave.Generator.Tests.csproj")))
                {
                    if (Path.GetPathRoot(dir) == dir)
                        throw new InvalidOperationException();
                    dir = Path.GetDirectoryName(dir);
                }

                var innerDir = Path.GetFullPath(Path.Combine(dir, "snapshots", Path.GetRelativePath(dir, directory)));
                return new PathInfo(innerDir, type.FullName, method.Name);
            });*/
            VerifySourceGenerators.Enable();
        }
        
        public static Task Verify(string source)
        {
            var doc = XDocument.Load(typeof(TestHelper).Assembly.GetManifestResourceStream("nuget.info")!);
            var refPackagePath = doc.Descendants().First(n => n.Name.LocalName == "PkgNETStandard_Library_Ref").Value;
            var refPath = Path.Combine(refPackagePath, "ref/netstandard2.1");
            // Parse the provided string into a C# syntax tree
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = new[]
            {
                MetadataReference.CreateFromFile(Path.Combine(refPath, "netstandard.dll")),
                
                MetadataReference.CreateFromFile(typeof(TrackPropertyAttribute).Assembly.Location)
            };
            
            // Create a Roslyn compilation for the syntax tree.
            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName: "Tests",
                syntaxTrees: new[] { syntaxTree }, references);
            
            // Create an instance of our EnumGenerator incremental source generator
            var generator = new PhoneWaveGenerator();

            // The GeneratorDriver is used to run our generator against a compilation
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

            // Run the source generator!
            driver = driver.RunGenerators(compilation);

            // Use verify to snapshot test the source generator output!
            return Verifier.Verify(driver);
        }
    }
}