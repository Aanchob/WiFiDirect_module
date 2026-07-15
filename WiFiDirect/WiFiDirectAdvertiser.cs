using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectAdvertiser
    {
        private readonly object _gate = new();
        private WiFiDirectAdvertisementPublisher? _publisher;

        public event Action<string>? LogReceived;

        public bool Start(
            bool listenerRegistered,
            string displayName = "",
            string shortSessionId = "",
            bool autonomousGroupOwner = false,
            string peerIdentity = "",
            int tcpPort = 0)
        {
            if (!listenerRegistered)
            {
                SafeLog("Wi-Fi Direct advertisement requires a registered listener.");
                return false;
            }

            WiFiDirectAdvertisementPublisher? attemptedPublisher = null;
            var pendingLogs = new List<string>();
            bool started;
            try
            {
                lock (_gate)
                {
                    if (_publisher != null &&
                        (_publisher.Status == WiFiDirectAdvertisementPublisherStatus.Aborted ||
                         _publisher.Status == WiFiDirectAdvertisementPublisherStatus.Stopped))
                    {
                        _publisher.StatusChanged -= OnStatusChanged;
                        _publisher = null;
                    }

                    if (_publisher != null)
                    {
                        pendingLogs.Add("Wi-Fi Direct advertisement is already active.");
                        pendingLogs.Add($"Current publisher status: {_publisher.Status}");
                        started = true;
                    }
                    else
                    {
                        var publisher = new WiFiDirectAdvertisementPublisher();
                        attemptedPublisher = publisher;
                        publisher.StatusChanged += OnStatusChanged;
                        _publisher = publisher;

                        publisher.Advertisement.ListenStateDiscoverability =
                            WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                        publisher.Advertisement.IsAutonomousGroupOwnerEnabled = autonomousGroupOwner;
                        publisher.Advertisement.SupportedConfigurationMethods.Add(
                            WiFiDirectConfigurationMethod.PushButton);

                        AddAppInformationElement(
                            publisher.Advertisement,
                            displayName,
                            shortSessionId,
                            peerIdentity,
                            tcpPort);

                        pendingLogs.Add("Wi-Fi Direct advertisement configured.");
                        pendingLogs.Add($"ListenStateDiscoverability: {publisher.Advertisement.ListenStateDiscoverability}");
                        pendingLogs.Add($"IsAutonomousGroupOwnerEnabled: {publisher.Advertisement.IsAutonomousGroupOwnerEnabled}");
                        pendingLogs.Add($"LegacySettings.IsEnabled: {publisher.Advertisement.LegacySettings.IsEnabled}");
                        pendingLogs.Add($"InformationElements.Count: {publisher.Advertisement.InformationElements.Count}");
                        pendingLogs.Add($"Listener registered: {listenerRegistered}");
                        pendingLogs.Add($"DCHAT information element added: Identity={peerIdentity}, ShortSessionId={shortSessionId}, TcpPort={tcpPort}");

                        publisher.Start();

                        pendingLogs.Add($"Advertisement publisher Start returned. Status={publisher.Status}");
                        started = publisher.Status != WiFiDirectAdvertisementPublisherStatus.Aborted;
                    }
                }
            }
            catch (Exception ex)
            {
                ReleasePublisher(attemptedPublisher);
                SafeLog($"Advertisement publisher start failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            foreach (string message in pendingLogs) SafeLog(message);
            return started;
        }

        public void Stop()
        {
            WiFiDirectAdvertisementPublisher? publisher;
            lock (_gate)
            {
                publisher = _publisher;
                if (publisher != null)
                {
                    publisher.StatusChanged -= OnStatusChanged;
                    _publisher = null;
                }
            }

            if (publisher == null)
            {
                SafeLog("Wi-Fi Direct advertisement is not active.");
                return;
            }

            try
            {
                SafeLog($"Advertisement publisher status before Stop: {publisher.Status}");
                publisher.Stop();
                SafeLog($"Advertisement publisher status after Stop: {publisher.Status}");
            }
            catch (Exception ex)
            {
                SafeLog($"Advertisement publisher stop failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AddAppInformationElement(
            WiFiDirectAdvertisement advertisement,
            string displayName,
            string shortSessionId,
            string peerIdentity,
            int tcpPort)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId) && string.IsNullOrWhiteSpace(peerIdentity))
            {
                throw new ArgumentException("A peer identity is required.", nameof(peerIdentity));
            }

            WiFiDirectInformationElement informationElement = DchatInformationElement.Create(
                displayName,
                peerIdentity,
                shortSessionId,
                tcpPort);
            advertisement.InformationElements.Add(informationElement);
        }

        private void OnStatusChanged(
            WiFiDirectAdvertisementPublisher sender,
            WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
        {
            if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
            {
                ReleasePublisher(sender);
            }

            QueueLog(
                $"Advertisement publisher status changed: Status={args.Status}, Error={args.Error}" +
                (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted
                    ? " (publisher released for restart)"
                    : ""));
        }

        private void ReleasePublisher(WiFiDirectAdvertisementPublisher? publisher)
        {
            if (publisher == null) return;

            lock (_gate)
            {
                publisher.StatusChanged -= OnStatusChanged;
                if (ReferenceEquals(_publisher, publisher))
                {
                    _publisher = null;
                }
            }
        }

        private void QueueLog(string message)
        {
            ThreadPool.QueueUserWorkItem(_ => SafeLog(message));
        }

        private void SafeLog(string message)
        {
            Action<string>? handlers = LogReceived;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
            {
                try { handler(message); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WiFiDirectAdvertiser log handler failed: {ex}");
                }
            }
        }
    }
}
