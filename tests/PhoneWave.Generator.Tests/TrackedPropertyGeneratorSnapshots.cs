using System;
using System.Threading.Tasks;
using VerifyXunit;
using Xunit;

namespace PhoneWave.Generator.Tests
{
    [UsesVerify] // 👈 Adds hooks for Verify into XUnit
    public class TrackedPropertyGeneratorSnapshots
    {
        [Fact]
        public Task GeneratesEnumExtensionsCorrectly()
        {
            
            // The source code to test
            var source = @"
namespace Test;
using System;
using PhoneWave;
[TrackProperty(typeof(System.Collections.Generic.List<int>.Enumerator), ""Bar"")]
partial class Foo
{
            
}";

            // Pass the source code to our helper and snapshot test the output
            return TestHelper.Verify(source);
        }
    }
}