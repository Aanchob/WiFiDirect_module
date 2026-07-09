using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace direct_module
{
    public class ChatMessageViewModel : INotifyPropertyChanged
    {
        private string _senderName = "";
        private string _body = "";
        private string _timeText = "";
        private bool _isMine;
        private bool _isGroup;
        private string _messageType = "chat";
        private string _fileId = "";
        private string _fileName = "";
        private long _fileSize;
        private string _localFilePath = "";
        private string _mimeType = "";
        private double _progressValue = 100.0;
        private bool _isTransferring = false;
        private string _progressText = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string SenderName
        {
            get => _senderName;
            set { _senderName = value; OnPropertyChanged(); OnPropertyChanged(nameof(AvatarText)); }
        }

        public string Body
        {
            get => _body;
            set { _body = value; OnPropertyChanged(); }
        }

        public string TimeText
        {
            get => _timeText;
            set { _timeText = value; OnPropertyChanged(); }
        }

        public bool IsMine
        {
            get => _isMine;
            set 
            { 
                _isMine = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(HorizontalAlignment)); 
                OnPropertyChanged(nameof(BubbleCornerRadius)); 
                OnPropertyChanged(nameof(BubbleBackground)); 
                OnPropertyChanged(nameof(LeftAvatarVisibility));
                OnPropertyChanged(nameof(SenderNameVisibility));
            }
        }

        public bool IsGroup
        {
            get => _isGroup;
            set { _isGroup = value; OnPropertyChanged(); OnPropertyChanged(nameof(SenderNameVisibility)); }
        }

        public string MessageType
        {
            get => _messageType;
            set 
            { 
                _messageType = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(TextVisibility)); 
                OnPropertyChanged(nameof(ImageVisibility)); 
                OnPropertyChanged(nameof(FileVisibility)); 
            }
        }

        public string FileId
        {
            get => _fileId;
            set { _fileId = value; OnPropertyChanged(); }
        }

        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(); }
        }

        public long FileSize
        {
            get => _fileSize;
            set { _fileSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(FileSizeText)); }
        }

        public string LocalFilePath
        {
            get => _localFilePath;
            set 
            { 
                _localFilePath = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(ImageSource)); 
                OnPropertyChanged(nameof(OpenButtonVisibility)); 
            }
        }

        public string MimeType
        {
            get => _mimeType;
            set { _mimeType = value; OnPropertyChanged(); }
        }

        public double ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }

        public bool IsTransferring
        {
            get => _isTransferring;
            set 
            { 
                _isTransferring = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(ProgressVisibility)); 
                OnPropertyChanged(nameof(OpenButtonVisibility)); 
            }
        }

        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(); }
        }

        // Calculated UI properties
        public HorizontalAlignment HorizontalAlignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public CornerRadius BubbleCornerRadius => IsMine 
            ? new CornerRadius(16, 4, 16, 16) 
            : new CornerRadius(4, 16, 16, 16);
        public Thickness BubblePadding => new Thickness(14, 11, 14, 11);
        
        public Brush BubbleBackground => IsMine 
            ? (Brush)Application.Current.Resources["OutgoingBubbleBrush"]
            : (Brush)Application.Current.Resources["IncomingBubbleBrush"];

        public Visibility LeftAvatarVisibility => IsMine ? Visibility.Collapsed : Visibility.Visible;
        
        public Visibility SenderNameVisibility => (!IsMine && IsGroup) ? Visibility.Visible : Visibility.Collapsed;

        public Visibility TextVisibility => MessageType == "chat" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ImageVisibility => MessageType == "image" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FileVisibility => MessageType == "file" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ProgressVisibility => IsTransferring ? Visibility.Visible : Visibility.Collapsed;
        
        public Visibility OpenButtonVisibility => (!string.IsNullOrWhiteSpace(LocalFilePath) && System.IO.File.Exists(LocalFilePath) && !IsTransferring)
            ? Visibility.Visible
            : Visibility.Collapsed;

        public string FileSizeText => FormatFileSize(FileSize);
        
        public string AvatarText => string.IsNullOrEmpty(SenderName) ? "WD" : SenderName.Substring(0, Math.Min(2, SenderName.Length)).ToUpper();

        public ImageSource? ImageSource
        {
            get
            {
                if (string.IsNullOrWhiteSpace(LocalFilePath) || !System.IO.File.Exists(LocalFilePath))
                {
                    return null;
                }
                try
                {
                    return new BitmapImage(new Uri(LocalFilePath));
                }
                catch
                {
                    return null;
                }
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{(double)bytes / 1024:F1} KB";
            return $"{(double)bytes / (1024 * 1024):F1} MB";
        }
    }
}
