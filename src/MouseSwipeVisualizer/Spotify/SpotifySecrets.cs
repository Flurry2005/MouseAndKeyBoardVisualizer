using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MouseSwipeVisualizer.Spotify;

/// <summary>
/// The Spotify client secret and refresh token. Never stored in settings.json and never logged: they live
/// in their own file, encrypted with Windows DPAPI for the current Windows account (only this user on this
/// machine can decrypt it).
/// </summary>
public sealed class SpotifySecrets
{
    /// <summary>Client ID the refresh token belongs to (a token only works with the app that issued it).</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? RefreshToken { get; set; }

    public static SpotifySecrets Load(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return new SpotifySecrets();
            }

            byte[] json = Dpapi.Unprotect(File.ReadAllBytes(file));
            return JsonSerializer.Deserialize<SpotifySecrets>(json) ?? new SpotifySecrets();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            // Unreadable (other user/machine, corrupt): start over; the user reconnects.
            Utilities.Logger.Warn($"Stored Spotify credentials could not be read ({ex.GetType().Name}); please connect again.");
            return new SpotifySecrets();
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        byte[] data = Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(this));
        string temp = file + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, file, overwrite: true);
    }

    public static void Delete(string file)
    {
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}

/// <summary>Windows Data Protection API (CryptProtectData), scoped to the current user.</summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MouseSwipeVisualizer.Spotify.v1");
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] plain) => Run(plain, protect: true);

    public static byte[] Unprotect(byte[] cipher) => Run(cipher, protect: false);

    private static byte[] Run(byte[] input, bool protect)
    {
        GCHandle inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out DataBlob output)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }
}
