using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using Windows.UI;

namespace direct_module
{
    public sealed class ChatMessageItem
    {
        private const int MaximumPreviewDimension = 8_192;
        private const long MaximumPreviewPixels = 16_000_000;
        private const long MaximumJpegHeaderScanBytes = 1024 * 1024;
        private bool? _isImage;
        public string Text { get; init; } = "";

        public string MessageId { get; init; } = "";

        public string ConversationId { get; set; } = "";

        public string SenderName { get; init; } = "";

        public bool IsMine { get; init; }

        public string FileName { get; init; } = "";

        public string LocalFilePath { get; init; } = "";

        public bool IsFile => !string.IsNullOrWhiteSpace(LocalFilePath);

        public bool IsImage => _isImage ??= IsFile && IsSupportedImage(LocalFilePath, FileName);

        public string AvatarText => IsMine ? "ME" : "RX";

        public Visibility SenderVisibility => !IsMine && !string.IsNullOrWhiteSpace(SenderName)
            ? Visibility.Visible
            : Visibility.Collapsed;

        public int AvatarColumn => IsMine ? 2 : 0;

        public int BubbleColumn => 1;

        public HorizontalAlignment BubbleAlignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        public CornerRadius BubbleCornerRadius => IsMine
            ? new CornerRadius(16, 4, 16, 16)
            : new CornerRadius(4, 16, 16, 16);

        public Brush BubbleBackground => new SolidColorBrush(
            IsMine ? Color.FromArgb(255, 16, 49, 55) : Color.FromArgb(255, 28, 30, 45));

        public Brush AvatarForeground => new SolidColorBrush(
            IsMine ? Color.FromArgb(255, 184, 255, 106) : Color.FromArgb(255, 201, 184, 255));

        public Visibility ImageVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;

        public Visibility FileButtonVisibility => IsFile ? Visibility.Visible : Visibility.Collapsed;

        public BitmapImage? ImageSource
        {
            get
            {
                if (!IsImage || !File.Exists(LocalFilePath))
                {
                    return null;
                }

                try
                {
                    var fileUri = new Uri(LocalFilePath, UriKind.Absolute);
                    if (!fileUri.IsFile)
                    {
                        return null;
                    }

                    return new BitmapImage(fileUri)
                    {
                        DecodePixelWidth = 720,
                        DecodePixelHeight = 720
                    };
                }
                catch
                {
                    return null;
                }
            }
        }

        public static bool IsImageFileName(string fileName)
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            // Animated formats are deliberately excluded from in-process previews;
            // their frame count and aggregate decoded size are not cheaply bounded.
            return extension is ".png" or ".jpg" or ".jpeg" or ".bmp";
        }

        private static bool IsSupportedImage(string filePath, string fileName)
        {
            if (!IsImageFileName(fileName) || !File.Exists(filePath)) return false;

            try
            {
                Span<byte> header = stackalloc byte[32];
                using FileStream stream = File.OpenRead(filePath);
                int read = stream.Read(header);
                if (read < 2) return false;

                bool jpeg = header[0] == 0xFF && header[1] == 0xD8;
                bool png = read >= 24 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                bool bmp = read >= 26 && header[0] == (byte)'B' && header[1] == (byte)'M';
                string extension = Path.GetExtension(fileName).ToLowerInvariant();
                bool signatureMatchesExtension = extension switch
                {
                    ".jpg" or ".jpeg" => jpeg,
                    ".png" => png,
                    ".bmp" => bmp,
                    _ => false
                };
                if (!signatureMatchesExtension) return false;

                (int Width, int Height) dimensions = jpeg
                    ? ReadJpegDimensions(stream)
                    : png
                        ? (ReadInt32BigEndian(header[16..20]), ReadInt32BigEndian(header[20..24]))
                        : bmp
                            ? (Math.Abs(ReadInt32LittleEndian(header[18..22])), Math.Abs(ReadInt32LittleEndian(header[22..26])))
                            : (0, 0);

                return HasSafePreviewDimensions(dimensions.Width, dimensions.Height);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasSafePreviewDimensions(int width, int height) =>
            width > 0 && height > 0 &&
            width <= MaximumPreviewDimension && height <= MaximumPreviewDimension &&
            (long)width * height <= MaximumPreviewPixels;

        private static (int Width, int Height) ReadJpegDimensions(FileStream stream)
        {
            stream.Position = 2;
            while (stream.Position < stream.Length && stream.Position < MaximumJpegHeaderScanBytes)
            {
                int prefix = stream.ReadByte();
                if (prefix != 0xFF) continue;

                int marker;
                do
                {
                    marker = stream.ReadByte();
                }
                while (marker == 0xFF);

                if (marker < 0 || marker is 0xD8 or 0xD9) continue;
                int high = stream.ReadByte();
                int low = stream.ReadByte();
                int segmentLength = high < 0 || low < 0 ? 0 : (high << 8) | low;
                if (segmentLength < 2) return (0, 0);

                bool isStartOfFrame = marker is >= 0xC0 and <= 0xC3 or
                    >= 0xC5 and <= 0xC7 or
                    >= 0xC9 and <= 0xCB or
                    >= 0xCD and <= 0xCF;
                if (isStartOfFrame)
                {
                    if (stream.ReadByte() < 0) return (0, 0);
                    int heightHigh = stream.ReadByte();
                    int heightLow = stream.ReadByte();
                    int widthHigh = stream.ReadByte();
                    int widthLow = stream.ReadByte();
                    if (heightHigh < 0 || heightLow < 0 || widthHigh < 0 || widthLow < 0)
                        return (0, 0);
                    return ((widthHigh << 8) | widthLow, (heightHigh << 8) | heightLow);
                }

                long next = stream.Position + segmentLength - 2;
                if (next < stream.Position || next > stream.Length) return (0, 0);
                stream.Position = next;
            }

            return (0, 0);
        }

        private static int ReadInt32BigEndian(ReadOnlySpan<byte> value) =>
            (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

        private static int ReadInt32LittleEndian(ReadOnlySpan<byte> value) =>
            value[0] | (value[1] << 8) | (value[2] << 16) | (value[3] << 24);

    }
}
