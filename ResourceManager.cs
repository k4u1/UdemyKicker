using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;

namespace UdemyKicker
{
    public static class ResourceManager
    {
        private static string _tempDir = null;

        public static string TempDirectory
        {
            get
            {
                if (_tempDir == null)
                {
                    string baseTemp = Path.Combine(Path.GetTempPath(), "UdemyKicker_Temp_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(baseTemp);
                    _tempDir = baseTemp;
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanUp();
                    if (System.Windows.Application.Current != null)
                    {
                        System.Windows.Application.Current.Exit += (s, e) => CleanUp();
                    }
                }
                return _tempDir;
            }
        }

        public static string GetExtensionPath()
        {
            string extDir = Path.Combine(TempDirectory, "ex");
            if (Directory.Exists(extDir)) return extDir;

            Directory.CreateDirectory(extDir);

            string[] extensionFiles = {
                "manifest.json",
                "background.js",
                "content_script.js",
                "message_proxy.js",
                "protobuf.min.js",
                "license_protocol.js",
                "util.js",
                "forge.min.js",
                "udemyKICKER.xpi",
                "images/icon-128.png"
            };

            Assembly assembly = Assembly.GetExecutingAssembly();

            foreach (string file in extensionFiles)
            {
                string resName = "UdemyKickerWPF.Extension." + file.Replace('/', '.');
                string destPath = Path.Combine(extDir, file.Replace('/', Path.DirectorySeparatorChar));

                string dir = Path.GetDirectoryName(destPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (Stream stream = assembly.GetManifestResourceStream(resName))
                {
                    if (stream != null)
                    {
                        using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fs);
                        }
                    }
                    else
                    {
                        AppLogger.LogInfo($"[ResourceManager] Resource not found: {resName}");
                    }
                }
            }

            return extDir;
        }

        public static string GetCookieScoopPath()
        {
            string destPath = Path.Combine(TempDirectory, "cookie-scoop.exe");
            if (File.Exists(destPath)) return destPath;

            Assembly assembly = Assembly.GetExecutingAssembly();
            string resName = "UdemyKickerWPF.cookie-scoop.exe";

            using (Stream stream = assembly.GetManifestResourceStream(resName))
            {
                if (stream != null)
                {
                    using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fs);
                    }
                }
                else
                {
                    AppLogger.LogInfo($"[ResourceManager] Resource not found: {resName}");
                }
            }

            return destPath;
        }

        public static void CleanUp()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, true);
                    _tempDir = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ResourceManager] Failed to delete temp directory: {ex.Message}");
                }
            }
        }
    }
}
