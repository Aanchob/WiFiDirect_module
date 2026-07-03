using System;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectAdvertiser
    {
        private WiFiDirectAdvertisementPublisher? _publisher;
        private bool _isStarted;

        public event Action<string>? LogReceived;

        public void Start(bool listenerRegistered)
        {
            if (_isStarted)
            {
                LogReceived?.Invoke("Wi-Fi Direct Advertisement はすでに起動中です");
                LogReceived?.Invoke($"Current Publisher Status: {_publisher?.Status}");
                return;
            }

            try
            {
                _publisher = new WiFiDirectAdvertisementPublisher();
                LogReceived?.Invoke("AdvertisementPublisher作成");

                _publisher.StatusChanged += OnStatusChanged;
                LogReceived?.Invoke("AdvertisementPublisher StatusChanged登録済み");

                // 公式UWPサンプルのAdvertiserシナリオに近い最小構成で広告する。
                // LegacySettingsやAutonomousGroupOwnerは環境差が大きいため、ここでは使わない。
                _publisher.Advertisement.ListenStateDiscoverability =
                    WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                _publisher.Advertisement.IsAutonomousGroupOwnerEnabled = false;

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
                LogReceived?.Invoke($"AdvertisementPublisher Stop前 Status: {_publisher.Status}");
                _publisher.Stop();
                LogReceived?.Invoke($"AdvertisementPublisher Stop後 Status: {_publisher.Status}");
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
                _isStarted = false;
            }
        }

        private void CleanupPublisher()
        {
            if (_publisher != null)
            {
                _publisher.StatusChanged -= OnStatusChanged;
                _publisher = null;
            }

            _isStarted = false;
        }
    }
}
