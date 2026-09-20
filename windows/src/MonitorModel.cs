//
//  MonitorModel.cs
//  MonBright for Windows — one of these per connected display.
//
//  A DDC/CI write costs roughly 30-70 ms and blocks the calling thread, so it
//  can never happen on the UI thread. Each monitor owns a worker thread with a
//  single "latest value wins" slot: dragging a slider overwrites the pending
//  value rather than queueing, so the panel always converges on where the
//  user's finger actually stopped instead of replaying the whole drag.
//

using System;
using System.Threading;

namespace MonBright
{
    internal sealed class MonitorModel : IDisposable
    {
        public readonly DisplayInfo Display;
        public string Id { get { return Display.Id; } }
        public string Name { get { return Display.Name; } }
        public bool IsInternal { get { return Display.IsInternal; } }

        /// <summary>False when the display can't be driven — the row dims and explains itself.</summary>
        public bool Available = true;

        /// <summary>
        /// Why the slider isn't working, in words a user can act on. Null while
        /// everything is fine. Derived rather than stored, so it can never
        /// disagree with <see cref="Available"/>.
        /// </summary>
        public string StatusMessage
        {
            get
            {
                // Keep these under ~44 characters: the panel is 320px wide at
                // 96 DPI and anything longer is drawn with an ellipsis.
                if (Available) return null;
                if (Display.IsInternal) return "Windows refused this brightness change.";
                if (!Display.HasDdc) return "No DDC/CI. Enable it in the monitor's menu.";
                return "Not responding. Check DDC/CI in its menu.";
            }
        }

        /// <summary>Raised on the UI thread when brightness or availability changes.</summary>
        public event EventHandler Changed;

        private int _brightness = 50;
        private readonly DdcRange _range = new DdcRange();
        private readonly SynchronizationContext _ui;

        private readonly Thread _worker;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly object _gate = new object();
        private int _pending = -1;
        private volatile bool _stop;

        private const int MinWriteIntervalMs = 25;

        public MonitorModel(DisplayInfo display)
        {
            Display = display;
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();

            // Known-unusable displays say so the moment the panel opens, rather
            // than looking fine until the user drags a slider that does nothing.
            Available = display.IsInternal
                ? !string.IsNullOrEmpty(display.WmiInstanceName)
                : display.HasDdc;

            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "monbright-write-" + display.Id;
            _worker.Start();
        }

        public int Brightness
        {
            get { return _brightness; }
            set { Set(value, true); }
        }

        /// <summary>Change brightness, optionally without scheduling a hardware write.</summary>
        public void Set(int percent, bool write)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (_brightness == percent && !write) return;

            bool changed = _brightness != percent;
            _brightness = percent;
            if (changed) RaiseChanged();
            if (write) Request(percent);
        }

        public void Nudge(int delta)
        {
            Set(_brightness + delta, true);
        }

        private void Request(int percent)
        {
            lock (_gate) _pending = percent;
            _signal.Set();
        }

        /// <summary>
        /// Ask the hardware what it is currently set to, then optionally push a
        /// remembered value over the top. Runs entirely on the worker thread so
        /// startup never blocks the UI.
        /// </summary>
        public void BeginInitialize(int? restoreTo)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                int current;
                bool read = ReadHardware(out current);

                _ui.Post(delegate
                {
                    if (read) Set(current, false);
                    if (restoreTo.HasValue) Set(restoreTo.Value, true);
                    else if (!read) Set(_brightness, false);
                }, null);
            });
        }

        private bool ReadHardware(out int percent)
        {
            percent = 0;
            try
            {
                if (Display.IsInternal)
                    return WmiBrightness.TryRead(Display.WmiInstanceName, out percent);
                if (!Display.HasDdc) return false;
                return Ddc.TryRead(Display.PhysicalHandle, _range, out percent);
            }
            catch (Exception ex)
            {
                Log.Write("ReadHardware " + Id + ": " + ex.Message);
                return false;
            }
        }

        private void WorkerLoop()
        {
            var lastWrite = DateTime.MinValue;
            while (!_stop)
            {
                _signal.WaitOne();
                if (_stop) return;

                int value;
                lock (_gate)
                {
                    value = _pending;
                    _pending = -1;
                }
                if (value < 0) continue;

                // Don't hammer the panel faster than it can answer.
                double since = (DateTime.UtcNow - lastWrite).TotalMilliseconds;
                if (since < MinWriteIntervalMs)
                    Thread.Sleep((int)(MinWriteIntervalMs - since));

                bool ok;
                try
                {
                    ok = Display.IsInternal
                        ? WmiBrightness.Write(Display.WmiInstanceName, value)
                        : Display.HasDdc && Ddc.Write(Display.PhysicalHandle, _range, value);
                }
                catch (Exception ex)
                {
                    Log.Write("Write " + Id + ": " + ex.Message);
                    ok = false;
                }
                lastWrite = DateTime.UtcNow;

                if (ok != Available)
                {
                    bool state = ok;
                    _ui.Post(delegate
                    {
                        Available = state;
                        RaiseChanged();
                    }, null);
                }
            }
        }

        private void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            try { _worker.Join(400); }
            catch (Exception) { }
            _signal.Close();
        }
    }
}
