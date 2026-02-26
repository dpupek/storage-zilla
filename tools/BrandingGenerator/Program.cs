using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

var repoRoot = ResolveRepoRoot();
var outputDir = Path.Combine(repoRoot, "src", "AzureFilesSync.Desktop", "Assets", "Branding");
var sourceIconPath = Path.Combine(repoRoot, "img", "storage-zilla-logo-notext.png");
var sourceWordmarkPath = Path.Combine(repoRoot, "img", "storage-zilla-logo-horizon.png");
Directory.CreateDirectory(outputDir);
if (!File.Exists(sourceIconPath))
{
    throw new FileNotFoundException("Brand source icon not found.", sourceIconPath);
}
if (!File.Exists(sourceWordmarkPath))
{
    throw new FileNotFoundException("Brand source wordmark not found.", sourceWordmarkPath);
}

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256, 512 };
var iconSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

using var sourceIconOriginal = (Bitmap)Image.FromFile(sourceIconPath);
using var sourceWordmarkOriginal = (Bitmap)Image.FromFile(sourceWordmarkPath);
using var sourceIcon = BuildSourceIconCrop(sourceIconOriginal);
RemoveBorderMatte(sourceIcon, 34);
RemoveBorderMatte(sourceWordmarkOriginal, 24);

foreach (var size in sizes)
{
    using var bitmap = ComposeAppIcon(sourceIcon, size);
    var pngPath = Path.Combine(outputDir, $"logo-{size}.png");
    bitmap.Save(pngPath, ImageFormat.Png);
}

using (var wordmark24 = ResizeBitmapToHeight(sourceWordmarkOriginal, 24))
{
    wordmark24.Save(Path.Combine(outputDir, "wordmark-24.png"), ImageFormat.Png);
}

using (var wordmark32 = ResizeBitmapToHeight(sourceWordmarkOriginal, 32))
{
    wordmark32.Save(Path.Combine(outputDir, "wordmark-32.png"), ImageFormat.Png);
}

WriteIco(outputDir, iconSizes, Path.Combine(outputDir, "app.ico"));
Console.WriteLine($"Brand assets generated in: {outputDir}");

static string ResolveRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AzureFilesSync.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Could not locate repository root from BrandingGenerator execution path.");
}

static Bitmap BuildSourceIconCrop(Bitmap sourceLogo)
{
    var square = Math.Min(sourceLogo.Width, sourceLogo.Height);
    var cropRect = new Rectangle(0, 0, square, square);
    if (cropRect.Right > sourceLogo.Width)
    {
        cropRect = new Rectangle(0, 0, sourceLogo.Width, sourceLogo.Width);
    }

    var icon = new Bitmap(square, square, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(icon);
    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    g.DrawImage(sourceLogo, new Rectangle(0, 0, square, square), cropRect, GraphicsUnit.Pixel);
    return icon;
}

static Bitmap ComposeAppIcon(Image iconArtwork, int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.CompositingQuality = CompositingQuality.HighQuality;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.SmoothingMode = SmoothingMode.HighQuality;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    var pad = Math.Max(1f, size * 0.05f);
    var rect = new RectangleF(pad, pad, size - (pad * 2f), size - (pad * 2f));
    var radius = Math.Max(2f, size * 0.18f);
    using var bgPath = RoundedRect(rect, radius);
    using var bgBrush = new LinearGradientBrush(rect, ColorTranslator.FromHtml("#0A2A57"), ColorTranslator.FromHtml("#1C72CF"), 45f);
    g.FillPath(bgBrush, bgPath);

    var inner = RectangleF.Inflate(rect, -Math.Max(1f, size * 0.04f), -Math.Max(1f, size * 0.04f));
    using var edge = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, size * 0.02f));
    g.DrawPath(edge, bgPath);

    var artMargin = size <= 24 ? 0.11f : 0.08f;
    var artRect = RectangleF.Inflate(inner, -inner.Width * artMargin, -inner.Height * artMargin);
    g.DrawImage(iconArtwork, artRect, new RectangleF(0, 0, iconArtwork.Width, iconArtwork.Height), GraphicsUnit.Pixel);
    return bmp;
}

static Bitmap ResizeBitmap(Image source, int width, int height)
{
    var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    g.DrawImage(source, new Rectangle(0, 0, width, height), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);

    return bmp;
}

static Bitmap ResizeBitmapToHeight(Image source, int targetHeight)
{
    var width = (int)Math.Round((double)source.Width * targetHeight / source.Height);
    return ResizeBitmap(source, width, targetHeight);
}

static GraphicsPath RoundedRect(RectangleF bounds, float radius)
{
    var diameter = radius * 2f;
    var path = new GraphicsPath();
    path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
    path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
    path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}

static void RemoveBorderMatte(Bitmap image, int tolerance)
{
    var width = image.Width;
    var height = image.Height;
    var visited = new bool[width * height];
    var queue = new Queue<(int X, int Y, Color Seed)>();

    void Enqueue(int x, int y, Color seed)
    {
        var index = (y * width) + x;
        if (visited[index])
        {
            return;
        }

        visited[index] = true;
        queue.Enqueue((x, y, seed));
    }

    for (var x = 0; x < width; x++)
    {
        var top = image.GetPixel(x, 0);
        var bottom = image.GetPixel(x, height - 1);
        Enqueue(x, 0, top);
        Enqueue(x, height - 1, bottom);
    }

    for (var y = 0; y < height; y++)
    {
        var left = image.GetPixel(0, y);
        var right = image.GetPixel(width - 1, y);
        Enqueue(0, y, left);
        Enqueue(width - 1, y, right);
    }

    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        var color = image.GetPixel(current.X, current.Y);
        if (!IsWithinTolerance(color, current.Seed, tolerance))
        {
            continue;
        }

        image.SetPixel(current.X, current.Y, Color.FromArgb(0, color.R, color.G, color.B));

        if (current.X > 0)
        {
            Enqueue(current.X - 1, current.Y, current.Seed);
        }
        if (current.X < width - 1)
        {
            Enqueue(current.X + 1, current.Y, current.Seed);
        }
        if (current.Y > 0)
        {
            Enqueue(current.X, current.Y - 1, current.Seed);
        }
        if (current.Y < height - 1)
        {
            Enqueue(current.X, current.Y + 1, current.Seed);
        }
    }
}

static bool IsWithinTolerance(Color value, Color seed, int tolerance)
{
    var dr = Math.Abs(value.R - seed.R);
    var dg = Math.Abs(value.G - seed.G);
    var db = Math.Abs(value.B - seed.B);
    return dr <= tolerance && dg <= tolerance && db <= tolerance;
}

static void WriteIco(string outputDir, IReadOnlyList<int> sizes, string icoPath)
{
    var images = new List<(int Size, byte[] Png)>();
    foreach (var size in sizes)
    {
        var pngPath = Path.Combine(outputDir, $"logo-{size}.png");
        images.Add((size, File.ReadAllBytes(pngPath)));
    }

    using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write, FileShare.None);
    using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

    bw.Write((ushort)0); // reserved
    bw.Write((ushort)1); // type: icon
    bw.Write((ushort)images.Count);

    var offset = 6 + (16 * images.Count);
    foreach (var image in images)
    {
        bw.Write((byte)(image.Size >= 256 ? 0 : image.Size));
        bw.Write((byte)(image.Size >= 256 ? 0 : image.Size));
        bw.Write((byte)0); // colors
        bw.Write((byte)0); // reserved
        bw.Write((ushort)1); // planes
        bw.Write((ushort)32); // bit count
        bw.Write(image.Png.Length);
        bw.Write(offset);
        offset += image.Png.Length;
    }

    foreach (var image in images)
    {
        bw.Write(image.Png);
    }
}
