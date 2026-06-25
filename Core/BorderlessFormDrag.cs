using System.Runtime.InteropServices;

namespace ChessKit
{
    internal static class BorderlessFormDrag
    {
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        public static void Enable(Form form)
        {
            Attach(form, form);
            form.ControlAdded += (_, e) =>
            {
                if (e.Control != null)
                    Attach(form, e.Control);
            };
        }

        private static void Attach(Form form, Control control)
        {
            if (CanStartDrag(control))
            {
                control.MouseDown -= Control_MouseDown;
                control.MouseDown += Control_MouseDown;
            }

            control.ControlAdded += (_, e) =>
            {
                if (e.Control != null)
                    Attach(form, e.Control);
            };

            foreach (Control child in control.Controls)
                Attach(form, child);

            void Control_MouseDown(object? sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left ||
                    form.IsDisposed ||
                    form.Disposing ||
                    !form.IsHandleCreated ||
                    form.WindowState == FormWindowState.Maximized)
                    return;

                try
                {
                    IntPtr handle = form.Handle;
                    if (handle == IntPtr.Zero)
                        return;

                    ReleaseCapture();
                    SendMessage(handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
                catch (ObjectDisposedException)
                {
                    // Late mouse messages can arrive while a transient borderless form is closing.
                }
                catch (InvalidOperationException)
                {
                    // The target form may be between handle destruction and disposal.
                }
            }
        }

        private static bool CanStartDrag(Control control)
        {
            if (control is Button or TextBoxBase or ComboBox or ListBox or NumericUpDown or TrackBar or DataGridView)
                return false;

            if (control.Cursor == Cursors.Hand)
                return false;

            return true;
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    }
}
