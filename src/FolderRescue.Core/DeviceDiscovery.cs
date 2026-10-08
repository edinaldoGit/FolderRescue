using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRescue.Core;

public sealed record StorageDevice(
    string Path,
    string Connection,
    string Model,
    long SizeBytes,
    string FileSystems)
{
    public override string ToString()
    {
        double gib = SizeBytes / (1024d * 1024 * 1024);
        string connection = string.IsNullOrWhiteSpace(Connection)
            ? "Desconhecida"
            : Connection.ToUpperInvariant();

        return $"{Path} | {gib:F1} GiB | " +
               $"{connection} | {Model} | {FileSystems}";
    }
}

public static class DeviceDiscovery
{
    public static async Task<IReadOnlyList<StorageDevice>> ListAsync()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "Detecção para Windows ainda não implementada.");

        var info = new ProcessStartInfo
        {
            FileName = "lsblk",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        info.ArgumentList.Add("--json");
        info.ArgumentList.Add("--bytes");
        info.ArgumentList.Add("--output");
        info.ArgumentList.Add(
            "NAME,PATH,TRAN,SIZE,FSTYPE,TYPE,MOUNTPOINT,MODEL");

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException(
                "Não foi possível executar lsblk.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(error);

        using var json = JsonDocument.Parse(output);

        var devices = new List<StorageDevice>();

        foreach (var item in json.RootElement
                     .GetProperty("blockdevices")
                     .EnumerateArray())
        {
            if (Field(item, "type") != "disk")
                continue;

            long.TryParse(Field(item, "size"), out long size);

            string fileSystems = string.Join(
                ", ", GetFileSystems(item));

            devices.Add(new StorageDevice(
                Field(item, "path"),
                Field(item, "tran"),
                Field(item, "model"),
                size,
                fileSystems));
        }

        return devices;
    }

    private static string Field(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return "";

        return value.ToString();
    }

    private static IEnumerable<string> GetFileSystems(
        JsonElement parent)
    {
        if (!parent.TryGetProperty("children", out var children) ||
            children.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var child in children.EnumerateArray())
        {
            string fs = Field(child, "fstype");

            if (!string.IsNullOrWhiteSpace(fs))
                yield return $"{Field(child, "name")}: {fs}";

            foreach (var nested in GetFileSystems(child))
                yield return nested;
        }
    }
}
