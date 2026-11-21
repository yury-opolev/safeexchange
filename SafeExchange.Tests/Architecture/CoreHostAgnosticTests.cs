/// <summary>
/// CoreHostAgnosticTests
/// </summary>

namespace SafeExchange.Tests.Architecture
{
    using NUnit.Framework;
    using SafeExchange.Core;
    using System;
    using System.IO;
    using System.Linq;
    using System.Xml.Linq;

    [TestFixture]
    public class CoreHostAgnosticTests
    {
        private const string AzureFunctionsPrefix = "Microsoft.Azure.Functions";

        [Test]
        public void CoreAssembly_ReferencesNoAzureFunctionsAssembly()
        {
            var offending = typeof(SafeExchangeDbContext).Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name ?? string.Empty)
                .Where(name => name.StartsWith(AzureFunctionsPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.That(offending, Is.Empty, "SafeExchange.Core must not use Azure Functions types; put them in SafeExchange.Core.Functions.");
        }

        [Test]
        public void CoreProject_HasNoAzureFunctionsPackageReference()
        {
            var project = XDocument.Load(FindCoreProjectFile());

            var offending = project.Descendants("PackageReference")
                .Select(reference => (string?)reference.Attribute("Include") ?? string.Empty)
                .Where(name => name.StartsWith(AzureFunctionsPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.That(offending, Is.Empty, "SafeExchange.Core.csproj must not reference Azure Functions packages.");
        }

        private static string FindCoreProjectFile()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "SafeExchange.Core", "SafeExchange.Core.csproj");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("SafeExchange.Core/SafeExchange.Core.csproj was not found above the test output directory.");
        }
    }
}
