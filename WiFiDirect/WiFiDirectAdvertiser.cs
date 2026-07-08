using System;
using System.Text;
using Windows.Devices.WiFiDirect;
using Windows.Storage.Streams;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectAdvertiser
    {
        private WiFiDirectAdvertisementPublisher? _publisher;
        private bool _isStarted;

        public event Action<string>? LogReceived;

        public void Start(
            bool listenerRegistered,
            string displayName = "",
            string shortSessionId = "",
            bool autonomousGroupOwner = false)
        {
            if (_isStarted)
            {
                LogReceived?.Invoke("Wi-Fi Direct Advertisement はすでに起動中です");
                LogReceived?.Invoke($"Current Publisher Status: {_publisher?.Status}");
                return;
            }

            try
            {
                CleanupPublisher();

                _publisher = new WiFiDirectAdvertisementPublisher();
                LogReceived?.Invoke("AdvertisementPublisher作成");

                _publisher.StatusChanged += OnStatusChanged;
                LogReceived?.Invoke("AdvertisementPublisher StatusChanged登録済み");

                _publisher.Advertisement.ListenStateDiscoverability =
                    WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                _publisher.Advertisement.IsAutonomousGroupOwnerEnabled = autonomousGroupOwner;

                TryAddAppInformationElement(
                    _publisher.Advertisement,
                    displayName,
                    shortSessionId
                );

                LogReceived?.Invoke("Advertisement設定内容");
                LogReceived?.Invoke($"ListenStateDiscoverability: {_publisher.Advertisement.ListenStateDiscoverability}");
                LogReceived?.Invoke($"IsAutonomousGroupOwnerEnabled: {_publisher.Advertisement.IsAutonomousGroupOwnerEnabled}");
                LogReceived?.Invoke($"LegacySettings.IsEnabled: {_publisher.Advertisement.LegacySettings.IsEnabled}");
                LogReceived?.Invoke($"InformationElements.Count: {_publisher.Advertisement.InformationElements.Count}");
                LogReceived?.Invoke($"Listener登録状態: {listenerRegistered}");

                _publisher.Start();
                _isStarted = true;

                LogReceived?.Invoke($"AdvertisementPublisher Start呼び出し完了: Status={_publisher.Status}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"AdvertisementPublisher開始失敗: {ex.GetType().Name}");
                LogReceived?.Invoke($"Error: {ex.Message}");
                CleanupPublisher();
            }
        }

        public void Stop()
        {
            if (_publisher == null)
            {
                LogReceived?.Invoke("Wi-Fi Direct Advertisement は開始されていません");
                return;
            }

            try
            {
                LogReceived?.Invoke($"AdvertisementPublisher Stop前Status: {_publisher.Status}");
                _publisher.Stop();
                LogReceived?.Invoke($"AdvertisementPublisher Stop後Status: {_publisher.Status}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"AdvertisementPublisher停止失敗: {ex.GetType().Name}");
                LogReceived?.Invoke($"Error: {ex.Message}");
            }
            finally
            {
                CleanupPublisher();
            }
        }

        private void TryAddAppInformationElement(
            WiFiDirectAdvertisement advertisement,
            string displayName,
            string shortSessionId)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId))
            {
                LogReceived?.Invoke("Wi-Fi Direct InformationElement追加スキップ: ShortSessionIdなし");
                return;
            }

            try
            {
                string payload = string.IsNullOrWhiteSpace(displayName)
                    ? $"DCHAT|{shortSessionId}"
                    : $"DCHAT|{displayName}|{shortSessionId}";

                var informationElement = new WiFiDirectInformationElement
                {
                    Oui = CreateBuffer(new byte[] { 0x44, 0x43, 0x48 }),
                    OuiType = 1,
                    Value = CreateBuffer(Encoding.UTF8.GetBytes(payload))
                };

                advertisement.InformationElements.Add(informationElement);
                LogReceived?.Invoke($"Wi-Fi Direct InformationElement追加成功: {payload}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke("Wi-Fi Direct InformationElement追加失敗");
                LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
                LogReceived?.Invoke($"Message: {ex.Message}");
            }
        }

        private static IBuffer CreateBuffer(byte[] bytes)
        {
            var writer = new DataWriter();
            writer.WriteBytes(bytes);
            return writer.DetachBuffer();
        }

        private void OnStatusChanged(
            WiFiDirectAdvertisementPublisher sender,
            WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
        {
            LogReceived?.Invoke("AdvertisementPublisher StatusChanged");
            LogReceived?.Invoke($"StatusChanged Status: {args.Status}");
            LogReceived?.Invoke($"StatusChanged Error: {args.Error}");
            LogReceived?.Invoke($"Publisher Current Status: {sender.Status}");

            if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
            {
                LogReceived?.Invoke("AdvertisementPublisher Aborted: Publisherをクリアします");
                CleanupPublisher(sender);
            }
        }

        private void CleanupPublisher(WiFiDirectAdvertisementPublisher? publisher = null)
        {
            WiFiDirectAdvertisementPublisher? target = publisher ?? _publisher;

            if (target != null)
            {
                target.StatusChanged -= OnStatusChanged;
            }

            if (publisher == null || ReferenceEquals(_publisher, publisher))
            {
                _publisher = null;
            }

            _isStarted = false;
        }
    }
}
