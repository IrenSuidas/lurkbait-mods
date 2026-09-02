using System;

namespace LurkBait.TwitchWatchdog
{
    // Per-connection decision logic: tracks a "down episode" so logging stays clean (one line down,
    // periodic while down, one line on recovery) and gates reconnects behind exponential backoff.
    // Evaluate returns true when the caller should perform the reconnect now. Main-thread only.
    internal sealed class ConnectionWatch
    {
        private const double ObserveLogEverySeconds = 30;

        private readonly string _name;
        private bool _down;
        private int _failCount;
        private DateTime _downSince;
        private DateTime _lastAttempt = DateTime.MinValue;
        private DateTime _lastObserveLog;

        public ConnectionWatch(string name) => _name = name;

        // reason == null means healthy.
        public bool Evaluate(string reason, bool autoReconnect, float baseBackoff, float maxBackoff)
        {
            var now = DateTime.UtcNow;

            if (reason == null)
            {
                if (_down)
                    Plugin.Log.LogInfo(
                        $"{_name} recovered after {(now - _downSince).TotalSeconds:F0}s "
                            + $"({_failCount} reconnect attempt(s))."
                    );
                _down = false;
                _failCount = 0;
                return false;
            }

            if (!_down)
            {
                _down = true;
                _downSince = now;
                _lastObserveLog = now;
                _failCount = 0;
                Plugin.Log.LogWarning($"{_name} appears DOWN: {reason}.");
            }

            if (!autoReconnect)
            {
                if ((now - _lastObserveLog).TotalSeconds >= ObserveLogEverySeconds)
                {
                    _lastObserveLog = now;
                    Plugin.Log.LogWarning(
                        $"[observe] {_name} still down after {(now - _downSince).TotalSeconds:F0}s: "
                            + $"{reason} (AutoReconnect is off)."
                    );
                }
                return false;
            }

            double delay = Math.Min(baseBackoff * Math.Pow(2, _failCount), maxBackoff);
            if ((now - _lastAttempt).TotalSeconds < delay)
                return false;

            _lastAttempt = now;
            _failCount++;
            Plugin.Log.LogWarning($"Reconnecting {_name} (attempt {_failCount}, {reason}).");
            return true;
        }

        // The connection isn't supposed to be up (auth gone / feature off): drop the episode without
        // a spurious "recovered" line.
        public void Reset()
        {
            _down = false;
            _failCount = 0;
        }
    }
}
