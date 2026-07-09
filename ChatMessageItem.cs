using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace direct_module
{
    public sealed class ChatMessageItem
    {
        public string Text { get; init; } = "";

        public string FileName { get; init; } = "";

        public string LocalFilePath { get; init; } = "";

        public bool IsFile => !string.IsNullOrWhiteSpace(LocalFilePath);

        public bool IsImage => IsFile && IsImageFileName(FileName);

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

                return new BitmapImage(new Uri(LocalFilePath));
            }
        }

        public static bool IsImageFileName(string fileName)
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
        }
    }
}
