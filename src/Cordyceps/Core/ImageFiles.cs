using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Cordyceps.Core
{
    /// <summary>
    /// Hyperceps: write an image through a .NET file stream. <c>Image.Save(path)</c> goes through
    /// GDI+, which can't open paths longer than Windows' old 260-character limit and fails with
    /// "A generic error occurred in GDI+"; an AI app's scratch folders easily pass that limit.
    /// </summary>
    public static class ImageFiles
    {
        public static void Save(Image image, string path, ImageFormat format)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                image.Save(stream, format);
        }
    }
}
