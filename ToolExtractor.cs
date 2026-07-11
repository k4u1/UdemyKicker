using System;
using System.IO;
using System.Reflection;

namespace UdemyKicker
{
    public static class ToolExtractor
    {
        public static string EngineDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UdemyKicker_Engine");

        public static void ExtractTools()
        {
            if (!Directory.Exists(EngineDirectory))
            {
                Directory.CreateDirectory(EngineDirectory);
            }

            Assembly assembly = Assembly.GetExecutingAssembly();
            string[] resourceNames = assembly.GetManifestResourceNames();

            foreach (string resourceName in resourceNames)
            {
                if (resourceName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // The resource name usually has format: Namespace.Folder.Filename.exe
                    // E.g., UdemyKicker.Engine.N_m3u8DL-RE.exe
                    string[] parts = resourceName.Split('.');
                    if (parts.Length >= 2)
                    {
                        string fileName = parts[parts.Length - 2] + "." + parts[parts.Length - 1]; // "N_m3u8DL-RE.exe"
                        
                        // Handle hyphens or special chars that might have been replaced in resource names
                        // Wait, C# replaces hyphens with underscores in some cases? No, embedded files usually keep original names unless folder names have them.
                        // Let's just write to the file based on the parsed name.
                        string destPath = Path.Combine(EngineDirectory, fileName);

                        if (!File.Exists(destPath))
                        {
                            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                            {
                                if (stream != null)
                                {
                                    using (FileStream fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                                    {
                                        stream.CopyTo(fileStream);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
