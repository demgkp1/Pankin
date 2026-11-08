using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pankin.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _configFilePath;
    private readonly object _lock = new();
    private PankinSettings _current;

    public string ConfigFilePath => _configFilePath;

    public PankinSettings Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public event EventHandler<PankinSettings>? Changed;

    public static string GetDefaultConfigPath()
    {
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(xdgConfig)
            ? xdgConfig
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "pankin", "settings.json");
    }

    public SettingsStore(string? configFilePath = null)
    {
        _configFilePath = configFilePath ?? GetDefaultConfigPath();
        _current = new PankinSettings();
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(_configFilePath))
                {
                    _current = new PankinSettings();
                    SaveCore(_current);
                    return;
                }

                string json = File.ReadAllText(_configFilePath);
                var loaded = JsonSerializer.Deserialize<PankinSettings>(json, JsonOptions);
                _current = loaded ?? new PankinSettings();
            }
            catch
            {
                // 若配置文件损坏，备份损坏文件并回退到默认设置
                try
                {
                    if (File.Exists(_configFilePath))
                    {
                        var backup = _configFilePath + ".bak-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        File.Move(_configFilePath, backup, true);
                    }
                }
                catch { }

                _current = new PankinSettings();
                SaveCore(_current);
            }
        }

        Changed?.Invoke(this, _current);
    }

    public void Save(PankinSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        lock (_lock)
        {
            SaveCore(settings);
            _current = settings;
        }

        Changed?.Invoke(this, _current);
    }

    public void Update(Func<PankinSettings, PankinSettings> updater)
    {
        if (updater == null) throw new ArgumentNullException(nameof(updater));

        PankinSettings updated;
        lock (_lock)
        {
            updated = updater(_current);
            SaveCore(updated);
            _current = updated;
        }

        Changed?.Invoke(this, updated);
    }

    private void SaveCore(PankinSettings settings)
    {
        var dir = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempFile = _configFilePath + ".tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);

        File.WriteAllText(tempFile, json);
        File.Move(tempFile, _configFilePath, true);
    }
}
