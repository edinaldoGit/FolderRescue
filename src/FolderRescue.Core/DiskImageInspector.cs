using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FolderRescue.Core;

public sealed record ImageInspection(
    string Format,
    long SizeBytes);

public static class DiskImageInspector
{
    public static async Task<ImageInspection> InspectAsync(
        string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);

        if (stream.Length < 512)
            throw new InvalidDataException(
                "A imagem possui menos de 512 bytes.");

        byte[] sector = new byte[512];
        await stream.ReadExactlyAsync(sector);

        string signature = Encoding.ASCII.GetString(
            sector, 3, 8);

        if (signature == "NTFS    ")
            return new("Assinatura NTFS", stream.Length);

        if (signature == "EXFAT   ")
            return new("Assinatura exFAT", stream.Length);

        if (stream.Length >= 1024)
        {
            await stream.ReadExactlyAsync(sector);

            string gpt = Encoding.ASCII.GetString(
                sector, 0, 8);

            if (gpt == "EFI PART")
                return new("Possível tabela GPT", stream.Length);
        }

        stream.Position = 510;

        byte[] marker = new byte[2];
        await stream.ReadExactlyAsync(marker);

        if (marker[0] == 0x55 && marker[1] == 0xAA)
            return new(
                "Possível setor de inicialização MBR",
                stream.Length);

        return new(
            "Formato não identificado",
            stream.Length);
    }
}
