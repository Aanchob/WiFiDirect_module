using direct_module.Discovery;
using direct_module.Database;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace direct_module
{
    public enum LogLevel
    {
        Info,
        Success,
        Debug,
        Error
    }

    public enum ChatRole
    {
        Host,
        Client
    }

    public sealed partial class MainWindow : Window
    {
        private const int LocalTcpPort = 50001;
        private const int MaxLogLines = 500;
        private static readonly TimeSpan WiFiDirectScanRestartDelay = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan WiFiDirectGoAdvertisementWait = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan WiFiDirectCandidateRefreshTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan WiFiDirectCandidatePollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);

        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private readonly ChatConnectionManager _chatConnectionManager = new();
        private readonly ChatMessageRouter _chatMessageRouter;
        private readonly ChatMessageFactory _chatMessageFactory;
        private readonly List<string> _logLines = new();
        private readonly Guid _localSessionId = Guid.NewGuid();
        private readonly DatabaseService? _databaseService = null;
        private readonly ChatHistoryService? _chatHistoryService = null;
        private readonly ConnectionRoleService _connectionRoleService;
        private readonly PeerConnectionStateService _peerConnectionStateService;
        private readonly PeerRegistryService _peerRegistryService;
        private readonly FileTransferService _fileTransferService = new();
        private readonly SemaphoreSlim _fileTransferReceiveGate = new(1, 1);
        private readonly SemaphoreSlim _tcpServerStartGate = new(1, 1);
        private readonly ConcurrentDictionary<string, PeerInfo> _pendingIncomingWiFiDirectPeers =
            new(StringComparer.OrdinalIgnoreCase);
        private bool _isAutonomousGoAdvertisementEnabled;
        private string _activeBleRolePeerKey = "";
        private bool? _activeBleRoleIsGo;
        private bool _isClientWiFiDirectScanScheduled;
        private int _bleRoleGeneration;

        private ChatRole _chatRole = ChatRole.Client;

        public MainWindow()
        {
            InitializeComponent();
            _chatMessageRouter = new ChatMessageRouter(_chatConnectionManager);
            _chatMessageFactory = new ChatMessageFactory(LocalPeerId, Environment.MachineName, GetLocalShortSessionId());
            _connectionRoleService = new ConnectionRoleService(GetLocalShortSessionId(), GetLocalRoleKey());
            _peerRegistryService = new PeerRegistryService(_connectionRoleService);
            _peerConnectionStateService = new PeerConnectionStateService(_connectionRoleService);
            Title = "Hide Chat";
            ResizeWindow(1440, 920);

            try
            {
                _databaseService = new DatabaseService();
                var chatRepository = new ChatRepository(_databaseService);
                _chatHistoryService = new ChatHistoryService(chatRepository, LocalPeerId, Environment.MachineName);
                AddLog("履歴DB初期化成功", LogLevel.Success);
                AddLog($"履歴DBパス: {_databaseService.DatabasePath}");
            }
            catch (Exception ex)
            {
                AddLog($"履歴DB初期化失敗: {ex.Message}", LogLevel.Error);
            }

            _manager = new WiFiDirectManager();
            _discoveryManager = new DiscoveryManager();

            _manager.LogReceived += OnLogReceived;
            _manager.ConnectionRequested += OnConnectionRequested;
            _manager.PeerFound += OnPeerFound;
            _manager.Connected += OnWiFiDirectConnected;

            _discoveryManager.LogReceived += OnLogReceived;
            _discoveryManager.PeerFound += OnPeerFound;

            _tcpServer.LogReceived += OnLogReceived;
            _tcpServer.ConnectionAccepted += OnTcpConnectionAccepted;

            _chatConnectionManager.LogReceived += OnLogReceived;
            _chatConnectionManager.MessageReceived += OnChatMessageReceived;
            _chatConnectionManager.ConnectionDisconnected += OnChatConnectionDisconnected;
            _chatConnectionManager.ConnectionsChanged += OnChatConnectionsChanged;
            _chatConnectionManager.StartKeepAlive(LocalPeerId, Environment.MachineName, GetLocalShortSessionId());
            _fileTransferService.LogReceived += OnLogReceived;
            _fileTransferService.ProgressChanged += OnFileTransferProgressChanged;
            _fileTransferService.EnsureStorageReady();
            AddLog($"添付ファイル一時保存先: {_fileTransferService.AttachmentsDirectory}");
            AddLog($"添付ファイルDownloads保存先: {_fileTransferService.DownloadsDirectory}");
            Closed += MainWindow_Closed;

            AddGroupChatPeer();
            UpdateSendButtonState();
            UpdatePeerCount();
            UpdateSelectedPeerDetails(null);
        }
    }
}
