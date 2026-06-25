namespace ChessKit
{
    internal sealed class OrientationPromptHost : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly object _sync = new();
        private readonly Thread _thread;
        private ApplicationContext? _context;
        private OrientationPromptForm? _form;
        private bool _disposed;
        private bool _promptShowing;

        public event Action<bool>? DirectionChosen;
        public event Action? Dismissed;

        public OrientationPromptHost()
        {
            _thread = new Thread(RunPromptThread)
            {
                IsBackground = true,
                Name = "Orientation Prompt UI"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
        }

        public void ShowPrompt(char referenceColor, Rectangle? anchorRect)
        {
            OrientationPromptForm? form;
            lock (_sync)
            {
                if (_disposed || _promptShowing)
                    return;

                form = _form;
                if (form == null || form.IsDisposed)
                    return;

                _promptShowing = true;
            }

            void ShowNow()
            {
                if (_disposed || form.IsDisposed)
                    return;

                Application.UseWaitCursor = false;
                form.ShowPrompt(referenceColor, anchorRect);
            }

            if (form.InvokeRequired)
                form.BeginInvoke(new Action(ShowNow));
            else
                ShowNow();
        }

        public void Hide()
        {
            OrientationPromptForm? form;
            lock (_sync)
            {
                form = _form;
                _promptShowing = false;
            }

            if (form == null || form.IsDisposed)
                return;

            void HideNow()
            {
                if (!form.IsDisposed)
                    form.Hide();
            }

            try
            {
                if (form.InvokeRequired)
                    form.BeginInvoke(new Action(HideNow));
                else
                    HideNow();
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose()
        {
            OrientationPromptForm? form;
            ApplicationContext? context;
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _promptShowing = false;
                form = _form;
                context = _context;
            }

            if (form == null || form.IsDisposed)
                return;

            void CloseNow()
            {
                if (!form.IsDisposed)
                    form.Close();
                context?.ExitThread();
            }

            try
            {
                if (form.InvokeRequired)
                    form.BeginInvoke(new Action(CloseNow));
                else
                    CloseNow();
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _ready.Dispose();
            }
        }

        private void RunPromptThread()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.UseWaitCursor = false;

                var context = new ApplicationContext();
                var form = new OrientationPromptForm();
                _ = form.Handle;

                form.DirectionChosen += value =>
                {
                    MarkPromptClosed();
                    ThreadPool.QueueUserWorkItem(_ => DirectionChosen?.Invoke(value));
                };
                form.Dismissed += () =>
                {
                    MarkPromptClosed();
                    ThreadPool.QueueUserWorkItem(_ => Dismissed?.Invoke());
                };

                lock (_sync)
                {
                    _context = context;
                    _form = form;
                }

                _ready.Set();
                Application.Run(context);
            }
            catch
            {
                _ready.Set();
            }
        }

        private void MarkPromptClosed()
        {
            lock (_sync)
            {
                _promptShowing = false;
            }
        }
    }
}
