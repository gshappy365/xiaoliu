using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Xiaoliu.Core.Capture;

public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public static CaptureRect FromPoints(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public CaptureRect Inflate(int dx, int dy) =>
        new(X - dx, Y - dy, Width + dx * 2, Height + dy * 2);
}

/// <summary>
/// 一张已经压好的截图。
///
/// <see cref="Signature"/> 是 32×32 灰度指纹，用来判断「屏幕变了没有」；
/// <see cref="ContentHash"/> 是它的 SHA256，落库用（存二进制指纹太占地方）。
/// </summary>
public sealed record CapturedImage(
    byte[] Bytes,
    string MediaType,
    int Width,
    int Height,
    string ContentHash,
    byte[]? Signature = null)
{
    public string Base64 => Convert.ToBase64String(Bytes);
}

/// <summary>
/// 屏幕取图。只用 GDI BitBlt —— 不引 WGC。
///
/// WGC（Windows.Graphics.Capture）能拿到受保护内容和 HDR，但需要 WinRT 互操作和
/// 一整套帧池生命周期管理；v1 只需要「用户圈选的那块区域」，BitBlt 足够，且不依赖
/// 图形栈状态。代价是遇到 DRM/独占全屏会拿到黑图 —— 见 docs/adr/0002。
/// </summary>
public static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height, nint hdcSrc, int xSrc, int ySrc, int rop);

    public static CaptureRect VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
        Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));

    /// <summary>把屏幕的一块区域抓成位图。调用方负责 Dispose。</summary>
    public static Bitmap CaptureBitmap(CaptureRect rect)
    {
        if (rect.IsEmpty)
        {
            throw new ArgumentException("截图区域为空。", nameof(rect));
        }

        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var screenDc = GetDC(0);
        try
        {
            var destination = graphics.GetHdc();
            try
            {
                if (!BitBlt(destination, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y, SRCCOPY | CAPTUREBLT))
                {
                    throw new InvalidOperationException("BitBlt 失败，屏幕可能被独占。");
                }
            }
            finally
            {
                graphics.ReleaseHdc(destination);
            }
        }
        finally
        {
            ReleaseDC(0, screenDc);
        }

        return bitmap;
    }

    /// <summary>
    /// 抓取并压缩。长边压到 <paramref name="maxSide"/> 以内、JPEG q75 ——
    /// 1280/q75 约 90KB，与点点同档（见 reports/local-llm-feasibility-20261028.md）。
    /// </summary>
    public static CapturedImage Capture(CaptureRect rect, int maxSide = 1280, long quality = 75L)
    {
        using var bitmap = CaptureBitmap(rect);
        var signature = SignatureOf(bitmap);

        using var scaled = Scale(bitmap, maxSide);
        using var buffer = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        scaled.Save(buffer, codec, parameters);

        return new CapturedImage(buffer.ToArray(), "image/jpeg", scaled.Width, scaled.Height,
            HashOf(signature), signature);
    }

    /// <summary>
    /// 内容指纹：缩到 32×32 灰度。用「内容是否变化」替代「抓了多久」，
    /// 是因为定时失效会把「用户盯着不动但还在问」的截图判死（点点 45 秒阈值的老问题）。
    /// </summary>
    public static byte[] SignatureOf(Bitmap bitmap)
    {
        using var small = new Bitmap(32, 32, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(bitmap, 0, 0, 32, 32);
        }

        var luminance = new byte[32 * 32];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var pixel = small.GetPixel(x, y);
                luminance[y * 32 + x] = (byte)((pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000);
            }
        }

        return luminance;
    }

    public static string HashOf(byte[] signature) => Convert.ToHexString(SHA256.HashData(signature));

    /// <summary>
    /// 两份指纹差多少。返回 0..1 的「变化格子占比」。
    ///
    /// 不做逐字节相等判断，是因为我们自己的字幕条和指点图形就画在屏幕上：
    /// 严格相等会让「模型连续两个指点」里的第二个必然失败。允许 8% 的格子变化
    /// 足以放过光标、时钟秒数、字幕条，但页面一换就会大面积不同。
    /// </summary>
    public static double Difference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 1;
        }

        const int tolerance = 24;
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > tolerance)
            {
                changed++;
            }
        }

        return (double)changed / a.Length;
    }

    public const double StaleThreshold = 0.08;

    /// <summary>重新看一眼这块区域，判断截图是否还有效。</summary>
    public static bool StillMatches(CaptureRect rect, byte[] signature)
    {
        try
        {
            using var bitmap = CaptureBitmap(rect);
            return Difference(SignatureOf(bitmap), signature) <= StaleThreshold;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            return false;
        }
    }

    private static Bitmap Scale(Bitmap source, int maxSide)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxSide)
        {
            return new Bitmap(source);
        }

        var ratio = (double)maxSide / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return scaled;
    }
}
