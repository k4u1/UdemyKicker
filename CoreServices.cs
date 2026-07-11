using System;
using System.Threading.Tasks;

namespace UdemyKicker
{
    public static class AppLogger
    {
        public static Action<string> OnLog { get; set; }
        public static Action OnSwitchToLibraryView { get; set; }

        public static void LogInfo(string message)
        {
            OnLog?.Invoke(message);
        }

        public static void SwitchToLibraryView()
        {
            OnSwitchToLibraryView?.Invoke();
        }
    }

    public static class BrowserHost
    {
        public static Func<string, Task<string>> FetchUdemyApiAsyncHandler { get; set; }
        public static Func<string, Task<string>> FetchDrmCommandAsyncHandler { get; set; }

        public static Task<string> FetchUdemyApiAsync(string url)
        {
            if (FetchUdemyApiAsyncHandler != null)
                return FetchUdemyApiAsyncHandler(url);
            return Task.FromResult<string>(null);
        }

        public static Task<string> FetchDrmCommandAsync(string url)
        {
            if (FetchDrmCommandAsyncHandler != null)
                return FetchDrmCommandAsyncHandler(url);
            return Task.FromResult<string>(null);
        }
    }
}
