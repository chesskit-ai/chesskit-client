using System.Runtime.InteropServices;

namespace ChessKit
{
    /// <summary>
    /// Owns the system-tray <see cref="NotifyIcon"/> and its context menu. The
    /// controller talks to the rest of the app only through the delegates injected
    /// at construction time.
    /// </summary>
    public sealed class SystemTrayController : IDisposable
    {
        private readonly Func<bool> _isSettingsToolbarHidden;
        private readonly Action _onRestoreToolbar;
        private readonly Action _onOpenAnalysisBoard;
        private readonly Action _onToggleOverlay;
        private readonly Action _onShowHardwareId;
        private readonly Action _onShowLicenseStatus;
        private readonly Action _onShowAbout;
        private readonly Action _onVisitWebsite;
        private readonly Func<bool> _confirmHideIcon;
        private readonly Action<bool> _persistShowTaskbarIcon;
        private readonly Action _onExit;

        private NotifyIcon? _taskbarIcon = null;
        private ContextMenuStrip? _taskbarIconMenu = null;
        private Icon? _taskbarIconImage = null;
        private ToolStripMenuItem? _showToolbarTaskbarMenuItem = null;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public SystemTrayController(
            Func<bool> isSettingsToolbarHidden,
            Action onRestoreToolbar,
            Action onOpenAnalysisBoard,
            Action onToggleOverlay,
            Action onShowHardwareId,
            Action onShowLicenseStatus,
            Action onShowAbout,
            Action onVisitWebsite,
            Func<bool> confirmHideIcon,
            Action<bool> persistShowTaskbarIcon,
            Action onExit)
        {
            _isSettingsToolbarHidden = isSettingsToolbarHidden;
            _onRestoreToolbar = onRestoreToolbar;
            _onOpenAnalysisBoard = onOpenAnalysisBoard;
            _onToggleOverlay = onToggleOverlay;
            _onShowHardwareId = onShowHardwareId;
            _onShowLicenseStatus = onShowLicenseStatus;
            _onShowAbout = onShowAbout;
            _onVisitWebsite = onVisitWebsite;
            _confirmHideIcon = confirmHideIcon;
            _persistShowTaskbarIcon = persistShowTaskbarIcon;
            _onExit = onExit;
        }

        private static Icon LoadApplicationIcon()
        {
            try
            {
                return AppIcon.CreateIconOrDefault();
            }
            catch
            {
                // Fall back to file/executable icon below.
            }

            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "chess-kit-icon.ico");
                if (File.Exists(iconPath))
                    return new Icon(iconPath);
            }
            catch
            {
                // Fall back to file/executable icon below.
            }

            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private static Icon LoadTaskbarIcon()
        {
            try
            {
                string imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "chess-kit-icon.png");
                if (File.Exists(imagePath))
                {
                    using var source = new Bitmap(imagePath);
                    return CreateTightTrayIcon(source);
                }
            }
            catch
            {
                // Fall back to the application icon below.
            }

            try
            {
                using Icon applicationIcon = AppIcon.CreateIconOrDefault();
                using Bitmap source = applicationIcon.ToBitmap();
                return CreateTightTrayIcon(source);
            }
            catch
            {
                return LoadApplicationIcon();
            }
        }

        private static Icon CreateTightTrayIcon(Bitmap source)
        {
            Rectangle visibleBounds = GetVisiblePixelBounds(source);
            const int canvasSize = 32;
            const int padding = 1;
            int available = canvasSize - padding * 2;

            float scale = Math.Min(
                available / (float)Math.Max(1, visibleBounds.Width),
                available / (float)Math.Max(1, visibleBounds.Height));

            int drawWidth = Math.Max(1, (int)Math.Round(visibleBounds.Width * scale));
            int drawHeight = Math.Max(1, (int)Math.Round(visibleBounds.Height * scale));
            var targetRect = new Rectangle(
                (canvasSize - drawWidth) / 2,
                (canvasSize - drawHeight) / 2,
                drawWidth,
                drawHeight);

            using var canvas = new Bitmap(canvasSize, canvasSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.DrawImage(source, targetRect, visibleBounds, GraphicsUnit.Pixel);
            }

            IntPtr handle = canvas.GetHicon();
            try
            {
                using Icon temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        private static Rectangle GetVisiblePixelBounds(Bitmap bitmap)
        {
            int left = bitmap.Width;
            int top = bitmap.Height;
            int right = -1;
            int bottom = -1;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A <= 8)
                        continue;

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            return right < left || bottom < top
                ? new Rectangle(0, 0, bitmap.Width, bitmap.Height)
                : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        public void SetVisible(bool visible, bool persist)
        {
            if (_taskbarIcon == null)
                return;

            _taskbarIcon.Visible = visible;

            if (persist)
            {
                _persistShowTaskbarIcon(visible);
            }
        }

        private string GetShowToolbarMenuText()
        {
            return _isSettingsToolbarHidden() ? "Restore settings bar" : "Show settings bar";
        }

        public void RefreshShowToolbarMenuText()
        {
            if (_showToolbarTaskbarMenuItem != null)
                _showToolbarTaskbarMenuItem.Text = GetShowToolbarMenuText();
        }

        public void Initialize(bool visible)
        {
            if (_taskbarIcon != null)
            {
                SetVisible(visible, persist: false);
                return;
            }

            _taskbarIconImage?.Dispose();
            _taskbarIconImage = LoadTaskbarIcon();

            _taskbarIconMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(40, 40, 42),
                ForeColor = Color.FromArgb(230, 230, 235),
                ShowImageMargin = false
            };

            _showToolbarTaskbarMenuItem = AddTaskbarMenuItem(GetShowToolbarMenuText(), _onRestoreToolbar);

            _taskbarIconMenu.Opening += (_, _) =>
            {
                if (_showToolbarTaskbarMenuItem != null)
                    _showToolbarTaskbarMenuItem.Text = GetShowToolbarMenuText();
            };

            AddTaskbarMenuItem("Open analysis board", _onOpenAnalysisBoard);

            AddTaskbarMenuItem("Enable / disable overlay", _onToggleOverlay);
            _taskbarIconMenu.Items.Add(new ToolStripSeparator());
            AddTaskbarMenuItem("Show HWID", _onShowHardwareId);
            AddTaskbarMenuItem("View license status", _onShowLicenseStatus);
            AddTaskbarMenuItem("About Chess Kit", _onShowAbout);
            AddTaskbarMenuItem("Visit website", _onVisitWebsite);
            _taskbarIconMenu.Items.Add(new ToolStripSeparator());

            var showIconItem = new ToolStripMenuItem("Hide system tray icon")
            {
                Checked = false,
                CheckOnClick = false,
                BackColor = Color.FromArgb(40, 40, 42),
                ForeColor = Color.FromArgb(230, 230, 235)
            };
            showIconItem.Click += (_, _) =>
            {
                if (_confirmHideIcon())
                {
                    SetVisible(false, persist: true);
                }
            };
            _taskbarIconMenu.Items.Add(showIconItem);

            _taskbarIconMenu.Items.Add(new ToolStripSeparator());
            AddTaskbarMenuItem("Exit Chess Kit", _onExit);

            _taskbarIcon = new NotifyIcon
            {
                Text = "Chess Kit",
                Icon = _taskbarIconImage,
                ContextMenuStrip = _taskbarIconMenu,
                Visible = visible
            };
            _taskbarIcon.DoubleClick += (_, _) =>
            {
                _onRestoreToolbar();
            };

            ToolStripMenuItem AddTaskbarMenuItem(string text, Action action)
            {
                var item = new ToolStripMenuItem(text)
                {
                    BackColor = Color.FromArgb(40, 40, 42),
                    ForeColor = Color.FromArgb(230, 230, 235)
                };
                item.Click += (_, _) => action();
                _taskbarIconMenu!.Items.Add(item);
                return item;
            }

            SetVisible(visible, persist: false);
        }

        public void Dispose()
        {
            try
            {
                if (_taskbarIcon != null)
                {
                    _taskbarIcon.Visible = false;
                    _taskbarIcon.Dispose();
                }
            }
            catch { }
            finally
            {
                _taskbarIcon = null;
            }

            try { _taskbarIconMenu?.Dispose(); } catch { }
            _taskbarIconMenu = null;

            try { _taskbarIconImage?.Dispose(); } catch { }
            _taskbarIconImage = null;
        }
    }
}
