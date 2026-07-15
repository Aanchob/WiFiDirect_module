using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private const int MaxVisibleMessagesPerConversation = 500;
        private const int MaxTrackedConversationCollections = 128;
        private const int MaxConversationAliases = 1024;
        private readonly Dictionary<string, ObservableCollection<ChatMessageItem>> _conversationItems =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _conversationAliases =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _conversationLastAccess =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _conversationAliasOrder = new();
        private readonly HashSet<string> _loadedConversationIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _conversationLoadGate = new();
        private long _conversationAccessSequence;
        private string _activeConversationId = "none";

        private string GetConversationIdForPeer(PeerInfo? peer)
        {
            if (peer == null) return "none";
            if (peer.IsGroupChat) return PeerIdentityService.DefaultGroupConversationId;

            string id = PeerIdentityService.GetConnectionId(peer);
            return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
        }

        private string GetConversationIdForMessage(ChatMessage message, ChatConnection sourceConnection)
        {
            if (message.IsGroup) return PeerIdentityService.NormalizeConversationId(message.ConversationId, isGroup: true);

            PeerInfo? peer = FindPeerForConnection(sourceConnection);
            if (peer != null) return GetConversationIdForPeer(peer);
            if (!string.IsNullOrWhiteSpace(sourceConnection.PeerId)) return sourceConnection.PeerId;
            if (!string.IsNullOrWhiteSpace(sourceConnection.RemoteIpAddress)) return sourceConnection.RemoteIpAddress;
            return "unknown";
        }

        private void SwitchConversation(PeerInfo? peer)
        {
            _activeConversationId = ResolveConversationId(GetConversationIdForPeer(peer));
            ObservableCollection<ChatMessageItem> items = GetConversationItems(_activeConversationId);
            MessageList.ItemsSource = items;
            if (items.Count > 0)
            {
                MessageList.ScrollIntoView(items[^1]);
            }

            string requestedConversationId = _activeConversationId;
            StartBackgroundOperation(
                () => LoadConversationHistoryAsync(requestedConversationId),
                "会話履歴の読み込み");
        }

        private ObservableCollection<ChatMessageItem> GetConversationItems(string conversationId)
        {
            conversationId = ResolveConversationId(conversationId);
            if (!_conversationItems.TryGetValue(conversationId, out ObservableCollection<ChatMessageItem>? items))
            {
                EvictConversationCollectionIfNeeded(conversationId);
                items = new ObservableCollection<ChatMessageItem>();
                _conversationItems[conversationId] = items;
            }

            _conversationLastAccess[conversationId] = ++_conversationAccessSequence;

            return items;
        }

        private void EvictConversationCollectionIfNeeded(string incomingConversationId)
        {
            if (_conversationItems.Count < MaxTrackedConversationCollections ||
                _conversationItems.ContainsKey(incomingConversationId))
            {
                return;
            }

            string active = ResolveConversationId(_activeConversationId);
            string? evicted = _conversationItems.Keys
                .Where(id =>
                    !string.Equals(id, active, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(id, PeerIdentityService.DefaultGroupConversationId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(id => _conversationLastAccess.TryGetValue(id, out long access) ? access : long.MinValue)
                .FirstOrDefault();
            if (evicted == null)
            {
                evicted = _conversationItems.Keys
                    .Where(id => !string.Equals(id, active, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(id => _conversationLastAccess.TryGetValue(id, out long access) ? access : long.MinValue)
                    .FirstOrDefault();
            }
            if (evicted == null)
            {
                return;
            }

            _conversationItems.Remove(evicted);
            _conversationLastAccess.Remove(evicted);
            lock (_conversationLoadGate)
            {
                _loadedConversationIds.Remove(evicted);
            }
        }

        private void SetConversationAlias(string previousConversationId, string canonicalConversationId)
        {
            bool isNewAlias = !_conversationAliases.ContainsKey(previousConversationId);
            _conversationAliases[previousConversationId] = canonicalConversationId;
            if (isNewAlias)
            {
                _conversationAliasOrder.Enqueue(previousConversationId);
            }
            int candidatesRemaining = _conversationAliasOrder.Count;
            while (_conversationAliases.Count > MaxConversationAliases &&
                   candidatesRemaining-- > 0 &&
                   _conversationAliasOrder.TryDequeue(out string? oldest))
            {
                if (_conversationAliases.TryGetValue(oldest, out string? current) &&
                    !string.Equals(oldest, _activeConversationId, StringComparison.OrdinalIgnoreCase))
                {
                    _conversationAliases.Remove(oldest);
                }
                else if (current != null)
                {
                    // Keep the active alias eligible for a later eviction pass; do
                    // not lose its queue entry and leave the dictionary above cap.
                    _conversationAliasOrder.Enqueue(oldest);
                }
            }
        }

        private void MigrateConversation(string previousConversationId, string canonicalConversationId)
        {
            if (string.IsNullOrWhiteSpace(previousConversationId) ||
                string.IsNullOrWhiteSpace(canonicalConversationId) ||
                string.Equals(previousConversationId, canonicalConversationId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            previousConversationId = ResolveConversationId(previousConversationId);
            canonicalConversationId = ResolveConversationId(canonicalConversationId);
            if (string.Equals(previousConversationId, canonicalConversationId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Record the alias even when no UI collection exists yet. An in-flight
            // history query for the provisional ID will then merge into the stable
            // collection instead of recreating an orphaned conversation.
            SetConversationAlias(previousConversationId, canonicalConversationId);

            _conversationItems.TryGetValue(
                previousConversationId,
                out ObservableCollection<ChatMessageItem>? previousItems);
            if (!_conversationItems.TryGetValue(canonicalConversationId, out ObservableCollection<ChatMessageItem>? canonicalItems))
            {
                if (previousItems == null)
                {
                    EvictConversationCollectionIfNeeded(canonicalConversationId);
                }
                canonicalItems = previousItems ?? new ObservableCollection<ChatMessageItem>();
                _conversationItems[canonicalConversationId] = canonicalItems;
            }
            else if (previousItems != null && !ReferenceEquals(previousItems, canonicalItems))
            {
                var existingIds = canonicalItems
                    .Where(item => !string.IsNullOrWhiteSpace(item.MessageId))
                    .Select(item => item.MessageId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (ChatMessageItem item in previousItems)
                {
                    if (string.IsNullOrWhiteSpace(item.MessageId) || existingIds.Add(item.MessageId))
                    {
                        item.ConversationId = canonicalConversationId;
                        canonicalItems.Add(item);
                    }
                }
            }

            foreach (ChatMessageItem item in canonicalItems)
            {
                item.ConversationId = canonicalConversationId;
            }

            _conversationItems.Remove(previousConversationId);
            _conversationLastAccess.Remove(previousConversationId);
            _conversationLastAccess[canonicalConversationId] = ++_conversationAccessSequence;
            lock (_conversationLoadGate)
            {
                _loadedConversationIds.Remove(previousConversationId);
            }
            _activeConversationId = ResolveConversationId(_activeConversationId);
            if (string.Equals(_activeConversationId, canonicalConversationId, StringComparison.OrdinalIgnoreCase))
            {
                MessageList.ItemsSource = canonicalItems;
            }

            StartBackgroundOperation(
                () => LoadConversationHistoryAsync(canonicalConversationId),
                "統合後の会話履歴の読み込み");
        }

        private string ResolveConversationId(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return "unknown";
            }

            string resolved = conversationId.Trim();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (visited.Add(resolved) &&
                   _conversationAliases.TryGetValue(resolved, out string? next) &&
                   !string.IsNullOrWhiteSpace(next))
            {
                resolved = next;
            }

            return resolved;
        }

        private void AddConversationItem(ChatMessageItem item)
        {
            string conversationId = string.IsNullOrWhiteSpace(item.ConversationId)
                ? _activeConversationId
                : item.ConversationId;
            conversationId = ResolveConversationId(conversationId);
            item.ConversationId = conversationId;
            ObservableCollection<ChatMessageItem> items = GetConversationItems(conversationId);
            bool shouldScroll = string.Equals(conversationId, _activeConversationId, StringComparison.OrdinalIgnoreCase) &&
                                IsMessageListNearBottom();

            items.Add(item);
            while (items.Count > MaxVisibleMessagesPerConversation)
            {
                items.RemoveAt(0);
            }

            if (shouldScroll && string.Equals(conversationId, _activeConversationId, StringComparison.OrdinalIgnoreCase))
            {
                MessageList.ScrollIntoView(item);
            }
        }

        private bool IsMessageListNearBottom()
        {
            ScrollViewer? viewer = FindVisualChild<ScrollViewer>(MessageList);
            return viewer == null || viewer.ScrollableHeight - viewer.VerticalOffset <= 64;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) return match;
                T? nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }

            return null;
        }

        private async System.Threading.Tasks.Task LoadConversationHistoryAsync(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId) ||
                string.Equals(conversationId, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(conversationId, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ChatHistoryService? chatHistoryService =
                await GetChatHistoryServiceWhenReadyAsync();
            if (chatHistoryService == null)
            {
                return;
            }

            lock (_conversationLoadGate)
            {
                if (!_loadedConversationIds.Add(conversationId)) return;
            }

            try
            {
                IReadOnlyList<direct_module.Models.ChatMessage> history =
                    await chatHistoryService.LoadConversationAsync(
                        conversationId,
                        MaxVisibleMessagesPerConversation,
                        _windowLifetimeCancellation.Token);

                void MergeHistory()
                {
                    if (System.Threading.Volatile.Read(ref _shutdownStarted) != 0)
                    {
                        lock (_conversationLoadGate)
                        {
                            _loadedConversationIds.Remove(conversationId);
                        }
                        return;
                    }

                    string mergeConversationId = ResolveConversationId(conversationId);
                    if (!string.Equals(
                            mergeConversationId,
                            conversationId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_conversationLoadGate)
                        {
                            _loadedConversationIds.Remove(conversationId);
                            _loadedConversationIds.Add(mergeConversationId);
                        }
                    }
                    ObservableCollection<ChatMessageItem> items = GetConversationItems(mergeConversationId);
                    var existingIds = new HashSet<string>(
                        items.Where(item => !string.IsNullOrWhiteSpace(item.MessageId))
                             .Select(item => item.MessageId),
                        StringComparer.OrdinalIgnoreCase);
                    int insertionIndex = 0;
                    foreach (direct_module.Models.ChatMessage stored in history)
                    {
                        if (!string.IsNullOrWhiteSpace(stored.MessageId) && !existingIds.Add(stored.MessageId))
                        {
                            continue;
                        }

                        items.Insert(insertionIndex++, new ChatMessageItem
                        {
                            MessageId = stored.MessageId,
                            Text = stored.Message,
                            ConversationId = mergeConversationId,
                            SenderName = SanitizeUntrustedDisplayText(stored.SenderName),
                            IsMine = stored.IsMine,
                            FileName = stored.FileName ?? "",
                            LocalFilePath = stored.LocalFilePath ?? ""
                        });
                    }

                    while (items.Count > MaxVisibleMessagesPerConversation)
                    {
                        items.RemoveAt(0);
                    }

                    if (string.Equals(mergeConversationId, ResolveConversationId(_activeConversationId), StringComparison.OrdinalIgnoreCase) &&
                        items.Count > 0)
                    {
                        MessageList.ScrollIntoView(items[^1]);
                    }
                }

                if (DispatcherQueue.HasThreadAccess)
                {
                    MergeHistory();
                }
                else if (!DispatcherQueue.TryEnqueue(MergeHistory))
                {
                    lock (_conversationLoadGate)
                    {
                        _loadedConversationIds.Remove(conversationId);
                    }
                }
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
                lock (_conversationLoadGate)
                {
                    _loadedConversationIds.Remove(conversationId);
                }
            }
            catch (Exception ex)
            {
                lock (_conversationLoadGate)
                {
                    _loadedConversationIds.Remove(conversationId);
                }
                EnqueueLog($"会話履歴の読み込みに失敗しました: {ex.Message}", LogLevel.Error);
            }
        }
    }
}
