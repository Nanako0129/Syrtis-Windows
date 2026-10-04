using System.Runtime.InteropServices;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>The one place a tray bitmap becomes an HICON. Every tray icon
/// (gauge, title, and every animation frame) goes through here, so the
/// pure-black fix-up (<see cref="TrayIconPixels.LiftPureBlack"/>) covers all
/// of them. The caller owns the returned handle (DestroyIcon).</summary>
internal static class TrayIconHandle
{
    public static nint From(System.Drawing.Bitmap bitmap)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(
            rect,
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var line = data.Scan0 + y * data.Stride;
                Marshal.Copy(line, row, 0, row.Length);
                if (TrayIconPixels.LiftPureBlack(row) > 0)
                {
                    Marshal.Copy(row, 0, line, row.Length);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap.GetHicon();
    }
}
