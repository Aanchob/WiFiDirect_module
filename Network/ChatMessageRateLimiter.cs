using System;
using System.Diagnostics;

namespace direct_module.Network
{
    /// <summary>
    /// Per-connection protocol-control limiter. Bulk file chunks and chat messages
    /// remain governed by the aggregate frame/byte limiter; dispatcher-heavy control
    /// messages receive tighter burst limits here.
    /// </summary>
    internal sealed class ChatMessageRateLimiter
    {
        internal const int KeepAliveBurstCapacity = 6;
        internal const int FileControlBurstCapacity = 64;
        private const double KeepAliveTokensPerMinute = 30;
        private const double FileControlTokensPerMinute = 180;

        private readonly object _gate = new();
        private TokenBucket _keepAlive = new(KeepAliveBurstCapacity, KeepAliveTokensPerMinute);
        private TokenBucket _fileControl = new(FileControlBurstCapacity, FileControlTokensPerMinute);
        private bool _helloConsumed;

        public bool TryConsume(string? messageType, long timestamp)
        {
            string normalizedType = messageType?.Trim().ToLowerInvariant() ?? "";
            lock (_gate)
            {
                return normalizedType switch
                {
                    "hello" => ConsumeHello(),
                    "ping" or "pong" => _keepAlive.TryConsume(timestamp),
                    "file_start" or "file_end" or "file_abort" or "file_ack" =>
                        _fileControl.TryConsume(timestamp),
                    _ => true
                };
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _helloConsumed = false;
                _keepAlive = new TokenBucket(KeepAliveBurstCapacity, KeepAliveTokensPerMinute);
                _fileControl = new TokenBucket(FileControlBurstCapacity, FileControlTokensPerMinute);
            }
        }

        private bool ConsumeHello()
        {
            if (_helloConsumed) return false;
            _helloConsumed = true;
            return true;
        }

        private sealed class TokenBucket
        {
            private readonly double _capacity;
            private readonly double _tokensPerSecond;
            private double _tokens;
            private long _lastTimestamp;

            public TokenBucket(int capacity, double tokensPerMinute)
            {
                _capacity = capacity;
                _tokens = capacity;
                _tokensPerSecond = tokensPerMinute / 60.0;
            }

            public bool TryConsume(long timestamp)
            {
                if (_lastTimestamp == 0)
                {
                    _lastTimestamp = timestamp;
                }
                else if (timestamp > _lastTimestamp)
                {
                    double elapsedSeconds = (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency;
                    _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _tokensPerSecond);
                    _lastTimestamp = timestamp;
                }

                if (_tokens < 1.0) return false;
                _tokens -= 1.0;
                return true;
            }
        }
    }
}
