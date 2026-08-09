using System.Xml.Linq;

namespace MesIngest.Watch.UiTests;

public sealed class WatchVisualCaptureConverterTests
{
    [Fact]
    [Trait("Category", "watch-vm-tests")]
    public void Unused_runtime_namespace_declarations_do_not_change_the_serialized_baseline()
    {
        var document = XDocument.Parse(
            """
            <Root xmlns="urn:root"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:swb="clr-namespace:System.Windows.Baml2006;assembly=PresentationFramework">
              <Child x:Key="used" />
            </Root>
            """);

        WatchVisualCaptureConverter.RemoveUnusedNamespaceDeclarations(document);

        var root = Assert.IsType<XElement>(document.Root);
        Assert.Null(root.Attribute(XNamespace.Xmlns + "swb"));
        Assert.Equal(
            "http://schemas.microsoft.com/winfx/2006/xaml",
            root.GetNamespaceOfPrefix("x")?.NamespaceName);
    }
}
