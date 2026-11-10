using Android.Graphics;
using Android.Media;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;
using Path = System.IO.Path;

namespace NurseShift.App.Images;

public sealed class AndroidImageService : IImagePicker, IImageProcessor
{
    public Task<PickedImage?> CaptureAsync(CancellationToken cancellationToken) => PickAsync(true, cancellationToken);
    public Task<PickedImage?> PickAsync(CancellationToken cancellationToken) => PickAsync(false, cancellationToken);

    private static async Task<PickedImage?> PickAsync(bool capture, CancellationToken cancellationToken)
    {
        if (capture && !MediaPicker.Default.IsCaptureSupported)
            throw new RecognitionUnavailableException(SafeErrorCode.ImageUnreadable);
        var result = capture ? await MediaPicker.Default.CapturePhotoAsync() : await MediaPicker.Default.PickPhotoAsync();
        if (result is null)
            return null;
        await using var source = await result.OpenReadAsync();
        var bitmap = await BitmapFactory.DecodeStreamAsync(source);
        if (bitmap is null)
            throw new RecognitionUnavailableException(SafeErrorCode.ImageUnreadable);
        try
        {
            var orientation = Orientation.Normal;
            if (!string.IsNullOrWhiteSpace(result.FullPath) && File.Exists(result.FullPath))
            {
                using var exif = new ExifInterface(result.FullPath);
                orientation = (Orientation)exif.GetAttributeInt(ExifInterface.TagOrientation, (int)Orientation.Normal);
            }
            bitmap = ApplyOrientation(bitmap, orientation);
            return await CompressAsync(bitmap, cancellationToken);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    public async Task RotateAsync(string privatePath, CancellationToken cancellationToken)
    {
        var bitmap = await BitmapFactory.DecodeFileAsync(privatePath) ?? throw new RecognitionUnavailableException(SafeErrorCode.ImageUnreadable);
        try
        {
            using var rotated = Rotate(bitmap, 90);
            await using var output = new FileStream(privatePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            if (!await rotated.CompressAsync(Bitmap.CompressFormat.Jpeg!, 95, output))
                throw new RecognitionUnavailableException(SafeErrorCode.StorageUnavailable);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static async Task<PickedImage> CompressAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        var current = bitmap;
        try
        {
            if (Math.Max(current.Width, current.Height) > 4096)
            {
                var scale = 4096d / Math.Max(current.Width, current.Height);
                current = Bitmap.CreateScaledBitmap(current, Math.Max(1, (int)(current.Width * scale)), Math.Max(1, (int)(current.Height * scale)), true);
            }
            for (var quality = 92; quality >= 72; quality -= 10)
            {
                var stream = new MemoryStream();
                if (!await current.CompressAsync(Bitmap.CompressFormat.Jpeg!, quality, stream))
                    throw new RecognitionUnavailableException(SafeErrorCode.ImageUnreadable);
                if (stream.Length <= 10 * 1024 * 1024)
                {
                    stream.Position = 0;
                    return new PickedImage(stream, "page.jpg", stream.Length);
                }
                await stream.DisposeAsync();
            }
            throw new RecognitionUnavailableException(SafeErrorCode.ImageTooLarge);
        }
        finally
        {
            if (!ReferenceEquals(current, bitmap))
                current.Dispose();
        }
    }

    private static Bitmap ApplyOrientation(Bitmap source, Orientation orientation)
    {
        var degrees = orientation switch
        {
            Orientation.Rotate90 => 90,
            Orientation.Rotate180 => 180,
            Orientation.Rotate270 => 270,
            _ => 0
        };
        return degrees == 0 ? source : Rotate(source, degrees);
    }

    private static Bitmap Rotate(Bitmap source, float degrees)
    {
        var matrix = new Matrix();
        matrix.PostRotate(degrees);
        return Bitmap.CreateBitmap(source, 0, 0, source.Width, source.Height, matrix, true);
    }
}

public sealed class RecognitionUnavailableException(SafeErrorCode code) : Exception
{
    public SafeErrorCode Code { get; } = code;
}
