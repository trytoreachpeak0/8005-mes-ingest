using System.IO;
using System.Text;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace MesIngest.Watch.UiTests;

internal static class WatchVisualCaptureConverter
{
    public static void Initialize() =>
        VerifierSettings.RegisterFileConverter<WatchVisualCapture>(Convert, null);

    internal static MemoryStream CapturePng(WatchVisualCapture target)
    {
        var width = (int)Math.Round(target.ActualWidth);
        var height = (int)Math.Round(target.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"The visual capture target has no arranged size: {target.ActualWidth}x{target.ActualHeight}.");
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }

    private static ConversionResult Convert(
        WatchVisualCapture target,
        IReadOnlyDictionary<string, object> context)
    {
        var png = CapturePng(target);
        var xaml = SerializeXaml(target);
        return new ConversionResult(
            null,
            [
                new Target("xml", xaml, null),
                new Target("png", png, null, true),
            ],
            null);
    }

    private static string SerializeXaml(WatchVisualCapture target)
    {
        var document = new XDocument();
        using (var documentWriter = document.CreateWriter())
        {
            XamlWriter.Save(target, documentWriter);
        }

        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineOnAttributes = true,
            OmitXmlDeclaration = true,
        };
        using (var writer = XmlWriter.Create(builder, settings))
        {
            document.WriteTo(writer);
        }

        return builder.ToString();
    }
}
