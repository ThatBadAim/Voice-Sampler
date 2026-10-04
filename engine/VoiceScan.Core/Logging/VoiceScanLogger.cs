namespace VoiceScan.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

public static class VoiceScanLogger
{
    private static readonly object _lock = new();
    private static string _logFilePath = AppPaths.LogFilePath;
    private static bool _initialized;

    public static string LogFilePath
    {
        get => _logFilePath;
        set
        {
            lock (_lock)
            {
                _logFilePath = value;
                _initialized = false;
            }
        }
    }

    public static void Initialize(string? customLogPath = null)
    {
        lock (_lock)
        {
            if (customLogPath != null)
            {
                _logFilePath = customLogPath;
            }

            try
            {
                string? dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                _initialized = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Could not initialize file logging: {ex.Message}");
            }
        }
    }

    public static void Debug(string component, string message) => Log(LogLevel.Debug, component, message);
    public static void Info(string component, string message) => Log(LogLevel.Info, component, message);
    public static void Warn(string component, string message) => Log(LogLevel.Warn, component, message);
    public static void Error(string component, string message, Exception? ex = null)
    {
        string fullMsg = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}" : message;
        Log(LogLevel.Error, component, fullMsg);
    }
    public static void Fatal(string component, string message, Exception? ex = null)
    {
        string fullMsg = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Log(LogLevel.Fatal, component, fullMsg);
    }

    private static void Log(LogLevel level, string component, string message)
    {
        lock (_lock)
        {
            if (!_initialized)
            {
                Initialize();
            }

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string entry = $"[{timestamp}Z] [{level,-5}] [{component}] {message}";

            // Console output for warnings & errors
            if (level is LogLevel.Warn or LogLevel.Error or LogLevel.Fatal)
            {
                Console.Error.WriteLine(entry);
            }

            try
            {
                File.AppendAllText(_logFilePath, entry + Environment.NewLine);
            }
            catch
            {
                // Never crash the application on log failure
            }
        }
    }
}
