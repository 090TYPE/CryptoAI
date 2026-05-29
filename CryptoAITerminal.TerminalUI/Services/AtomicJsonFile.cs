using System;
using System.IO;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.Services;

public static class AtomicJsonFile
{
    public static T? Read<T>(string path, JsonSerializerOptions? options = null)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, options);
    }

    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.tmp";
        var json = JsonSerializer.Serialize(value, options);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    public static string BackupCorruptFile(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var backupPath = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }
}
