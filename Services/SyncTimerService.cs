using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AntigravityQuota
{
    /// <summary>
    /// Encapsulates the 1-second tick timer and the configurable sync interval timer.
    /// </summary>
    public class SyncTimerService : IDisposable
    {
        private readonly DispatcherTimer _tickTimer;
        private DispatcherTimer? _syncTimer;

        /// <summary>Fires every second for countdown animations.</summary>
        public event Action? TickElapsed;

        /// <summary>Fires on the sync interval to request a quota refresh.</summary>
        public event Func<Task>? SyncRequested;

        public SyncTimerService()
        {
            _tickTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _tickTimer.Tick += (s, e) => TickElapsed?.Invoke();
        }

        /// <summary>Starts the 1-second tick timer and initializes the sync timer.</summary>
        public void Start(int syncIntervalMinutes)
        {
            _tickTimer.Start();
            UpdateInterval(syncIntervalMinutes);
        }

        /// <summary>Changes the sync interval. Pass 0 or negative to disable.</summary>
        public void UpdateInterval(int minutes)
        {
            if (_syncTimer != null)
            {
                _syncTimer.Stop();
                _syncTimer = null;
            }

            if (minutes <= 0) return;

            _syncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(minutes)
            };
            _syncTimer.Tick += async (s, e) =>
            {
                if (SyncRequested != null)
                {
                    await SyncRequested.Invoke();
                }
            };
            _syncTimer.Start();
        }

        public void Dispose()
        {
            _tickTimer.Stop();
            _syncTimer?.Stop();
        }
    }
}
