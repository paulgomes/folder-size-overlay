using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PastaGBIcons
{
    internal enum SizeUnits { Automatic, Gigabytes }
    internal enum AppearanceTheme { Automatic, Light, Dark }
    internal enum PerformanceMode { Balanced, Economical }

    // Replaced as a complete snapshot; worker threads never mutate preferences.
    internal sealed class AppPreferences
    {
        internal SizeUnits Units = SizeUnits.Gigabytes;
        internal AppearanceTheme Theme = AppearanceTheme.Automatic;
        internal PerformanceMode Performance = PerformanceMode.Balanced;
        internal float TextScale = 1f;
        internal bool FollowDirectoryLinks = true;
        internal bool CrossVolumeLinks = true;

        internal int MaximumReaders { get { return Performance == PerformanceMode.Economical ? 1 : 2; } }
        internal int ReaderCpuPercent { get { return Performance == PerformanceMode.Economical ? 10 : 15; } }
        internal int EntriesPerSecond { get { return Performance == PerformanceMode.Economical ? 3000 : 12000; } }
        internal int DesktopInterval { get { return Performance == PerformanceMode.Economical ? 650 : 375; } }
        internal int ExplorerInterval { get { return Performance == PerformanceMode.Economical ? 500 : 250; } }
        internal int ScanInterval { get { return Performance == PerformanceMode.Economical ? 300 : 175; } }
        internal int DesktopIdleInterval { get { return Performance == PerformanceMode.Economical ? 3000 : 2000; } }
        internal int ExplorerIdleInterval { get { return Performance == PerformanceMode.Economical ? 3000 : 2000; } }
        internal int ReconcileSeconds { get { return Performance == PerformanceMode.Economical ? 600 : 300; } }

        internal AppPreferences Copy() { return (AppPreferences)MemberwiseClone(); }

        internal static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PastaGBIcones", "preferencias.ini"); }
        }

        internal static AppPreferences Load(string path, out string error)
        {
            var result = new AppPreferences();
            error = null;
            try
            {
                if (!File.Exists(path)) return result;
                if (new FileInfo(path).Length > 16384) throw new IOException("O arquivo de preferências excedeu o limite.");
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = raw.IndexOf('=');
                    if (separator < 0) continue;
                    string key = raw.Substring(0, separator).Trim();
                    string value = raw.Substring(separator + 1).Trim();
                    int number; float scale; bool flag;
                    if (key == "units" && Int32.TryParse(value, out number) && number >= 0 && number <= 1)
                        result.Units = (SizeUnits)number;
                    else if (key == "theme" && Int32.TryParse(value, out number) && number >= 0 && number <= 2)
                        result.Theme = (AppearanceTheme)number;
                    else if (key == "performance" && Int32.TryParse(value, out number) && number >= 0 && number <= 1)
                        result.Performance = (PerformanceMode)number;
                    else if (key == "textScale" && Single.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out scale) && !Single.IsNaN(scale) && !Single.IsInfinity(scale))
                        result.TextScale = Math.Max(.85f, Math.Min(1.3f, scale));
                    else if (key == "followDirectoryLinks" && Boolean.TryParse(value, out flag))
                        result.FollowDirectoryLinks = flag;
                    else if (key == "crossVolumeLinks" && Boolean.TryParse(value, out flag))
                        result.CrossVolumeLinks = flag;
                }
            }
            catch (Exception ex) { error = "Preferências padrão em uso: " + ex.Message; }
            return result;
        }

        internal void Save(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".preferencias-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                string text = "version=1\r\nunits=" + (int)Units + "\r\ntheme=" + (int)Theme +
                    "\r\nperformance=" + (int)Performance + "\r\ntextScale=" +
                    TextScale.ToString("0.##", CultureInfo.InvariantCulture) +
                    "\r\nfollowDirectoryLinks=" + FollowDirectoryLinks +
                    "\r\ncrossVolumeLinks=" + CrossVolumeLinks + "\r\n";
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
