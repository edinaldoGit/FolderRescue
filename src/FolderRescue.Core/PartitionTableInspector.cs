using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FolderRescue.Core;

public sealed record PartitionInfo(
    int Number,
    string Type,
    long OffsetBytes,
    long LengthBytes,
    string FileSystem);

public sealed record PartitionScanResult(
    string Scheme,
    IReadOnlyList<PartitionInfo> Partitions);

public static class PartitionTableInspector
{
    public static async Task<PartitionScanResult> InspectAsync(
        string path)
    {
        // Nesta fase, proibimos acesso direto a dispositivos.
        if (OperatingSystem.IsLinux() &&
            Path.GetFullPath(path).StartsWith(
                "/dev/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Selecione uma imagem de teste, não um disco físico.");
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.RandomAccess);

        if (stream.Length < 512)
            return new("Imagem muito pequena", []);

        byte[] mbr = await ReadAt(stream, 0, 512);

        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
            return new("Tabela não identificada", []);

        bool protective = false;

        for (int i = 0; i < 4; i++)
            if (mbr[446 + i * 16 + 4] == 0xEE)
                protective = true;

        // Detecta GPT com setores de 512 ou 4096 bytes.
        foreach (int sectorSize in new[] { 512, 4096 })
        {
            if (stream.Length < 2L * sectorSize)
                continue;

            byte[] header = await ReadAt(
                stream, sectorSize, sectorSize);

            if (Encoding.ASCII.GetString(header, 0, 8)
                == "EFI PART")
                return await InspectGpt(
                    stream, header, sectorSize);
        }

        if (protective)
            return new(
                "MBR protetora; cabeçalho GPT ausente", []);

        return await InspectMbr(stream, mbr);
    }

    private static async Task<PartitionScanResult> InspectGpt(
        FileStream stream, byte[] header, int sectorSize)
    {
        uint headerSize = U32(header, 12);

        if (headerSize < 92 || headerSize > sectorSize)
            throw new InvalidDataException(
                "Tamanho inválido do cabeçalho GPT.");

        uint expectedHeaderCrc = U32(header, 16);

        byte[] headerData =
            header.AsSpan(0, (int)headerSize).ToArray();

        Array.Clear(headerData, 16, 4);

        if (Crc32(headerData) != expectedHeaderCrc)
            throw new InvalidDataException(
                "CRC do cabeçalho GPT inválido.");

        if (U64(header, 24) != 1)
            throw new InvalidDataException(
                "LBA do cabeçalho GPT inesperado.");

        ulong firstUsable = U64(header, 40);
        ulong lastUsable = U64(header, 48);
        ulong entriesLba = U64(header, 72);

        uint count = U32(header, 80);
        uint entrySize = U32(header, 84);
        uint expectedEntriesCrc = U32(header, 88);

        if (count == 0 || count > 4096 ||
            entrySize < 128 || entrySize > 1024 ||
            entrySize % 8 != 0)
            throw new InvalidDataException(
                "Parâmetros da tabela GPT não suportados.");

        int totalBytes = checked((int)(count * entrySize));
        ulong imageSize = (ulong)stream.Length;
        ulong sector = (ulong)sectorSize;

        if (entriesLba > imageSize / sector)
            throw new InvalidDataException(
                "Tabela GPT fora da imagem.");

        ulong tableOffset = entriesLba * sector;

        if ((ulong)totalBytes > imageSize - tableOffset)
            throw new InvalidDataException(
                "Tabela GPT truncada.");

        byte[] entries = await ReadAt(
            stream, (long)tableOffset, totalBytes);

        if (Crc32(entries) != expectedEntriesCrc)
            throw new InvalidDataException(
                "CRC das entradas GPT inválido.");

        var partitions = new List<PartitionInfo>();

        for (int i = 0; i < count; i++)
        {
            int pos = checked(i * (int)entrySize);

            bool empty = true;
            for (int j = 0; j < 16; j++)
                if (entries[pos + j] != 0)
                    empty = false;

            if (empty)
                continue;

            ulong start = U64(entries, pos + 32);
            ulong end = U64(entries, pos + 40);

            if (start > end ||
                start < firstUsable ||
                end > lastUsable ||
                end >= imageSize / sector)
                continue;

            long offset = checked((long)(start * sector));
            long length = checked((long)(
                (end - start + 1) * sector));

            string fs = await DetectFileSystem(
                stream, offset);

            var typeGuid = new Guid(
                entries.AsSpan(pos, 16));

            partitions.Add(new PartitionInfo(
                i + 1,
                typeGuid.ToString(),
                offset,
                length,
                fs));
        }

        return new(
            $"GPT ({sectorSize} bytes/setor)", partitions);
    }

    private static async Task<PartitionScanResult> InspectMbr(
        FileStream stream, byte[] mbr)
    {
        var partitions = new List<PartitionInfo>();

        for (int i = 0; i < 4; i++)
        {
            int pos = 446 + i * 16;
            byte type = mbr[pos + 4];

            uint start = U32(mbr, pos + 8);
            uint count = U32(mbr, pos + 12);

            if (type == 0 || count == 0 ||
                type is 0x05 or 0x0F or 0x85 or 0xEE)
                continue;

            long offset = (long)start * 512;
            long length = (long)count * 512;

            if (length < 512 ||
                offset > stream.Length - 512 ||
                length > stream.Length - offset)
                continue;

            string fs = await DetectFileSystem(
                stream, offset);

            partitions.Add(new PartitionInfo(
                i + 1, $"0x{type:X2}",
                offset, length, fs));
        }

        return new("MBR (512 bytes/setor)", partitions);
    }

    private static async Task<string> DetectFileSystem(
        FileStream stream, long offset)
    {
        byte[] data = await ReadAt(stream, offset, 512);

        string signature =
            Encoding.ASCII.GetString(data, 3, 8);

        return signature switch
        {
            "NTFS    " => "NTFS (assinatura)",
            "EXFAT   " => "exFAT (assinatura)",
            _ => "Não identificado"
        };
    }

    private static uint U32(byte[] data, int pos) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            data.AsSpan(pos, 4));

    private static ulong U64(byte[] data, int pos) =>
        BinaryPrimitives.ReadUInt64LittleEndian(
            data.AsSpan(pos, 8));

    private static async Task<byte[]> ReadAt(
        FileStream stream, long offset, int count)
    {
        byte[] buffer = new byte[count];
        stream.Position = offset;
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^
                    ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }

        return ~crc;
    }
}
