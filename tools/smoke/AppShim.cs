namespace SekaiSync.Desktop;

/// 冒烟工程里的 App.Log 替身（正式工程里在 App.xaml.cs）。
public static class App
{
    public static void Log(string message) => Console.WriteLine("[log] " + message);
}
