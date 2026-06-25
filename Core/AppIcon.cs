namespace ChessKit
{
    internal static class AppIcon
    {
        private static readonly Lazy<Icon?> CachedIcon = new(LoadIcon);

        public static void ApplyTo(Form form)
        {
            Icon? icon = CachedIcon.Value;
            if (icon == null)
                return;

            form.Icon = (Icon)icon.Clone();
        }

        public static Icon CreateIconOrDefault()
        {
            Icon? icon = CachedIcon.Value;
            return icon == null
                ? SystemIcons.Application
                : (Icon)icon.Clone();
        }

        private static Icon? LoadIcon()
        {
            try
            {
                var assembly = typeof(AppIcon).Assembly;
                string? resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(".Assets.Brand.chess-kit-icon.ico", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(resourceName))
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var resourceIcon = new Icon(stream);
                        return (Icon)resourceIcon.Clone();
                    }
                }
            }
            catch
            {
                // Fall back to file/executable icon below.
            }

            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "chess-kit-icon.ico"),
                Path.Combine(AppContext.BaseDirectory, "chess-kit-icon.ico"),
                Path.Combine(Environment.CurrentDirectory, "Assets", "Brand", "chess-kit-icon.ico")
            };

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    return new Icon(candidate);
                }
                catch
                {
                    // Fall back to the executable icon below.
                }
            }

            try
            {
                string? executablePath = Application.ExecutablePath;
                return string.IsNullOrWhiteSpace(executablePath)
                    ? null
                    : Icon.ExtractAssociatedIcon(executablePath);
            }
            catch
            {
                return null;
            }
        }
    }
}
