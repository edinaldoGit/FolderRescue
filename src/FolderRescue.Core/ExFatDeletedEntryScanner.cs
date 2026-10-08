using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FolderRescue.Core;

public sealed record ExFatDeletedItem(
    string Path,
    bool IsDirectory,
    ulong LengthBytes,
    uint FirstCluster,
    bool NoFatChain,
    bool ParentDeleted);

public sealed record ExFatDeletedScanResult(
    IReadOnlyList<ExFatDeletedItem> Items,
    IReadOnlyList<string> Warnings);

public static class ExFatDeletedEntryScanner
{
    private const int MaxDirectoryBytes = 16 * 1024 * 1024;
    private const int MaxDepth = 20;
    private const int MaxItems = 20000;

    public static ExFatDeletedScanResult Scan(
        string imagePath,
        ExFatBootInfo boot)
    {
        var active = ExFatDirectoryReader.ReadTree(
            imagePath, boot);

        using var image = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var found = new List<ExFatDeletedItem>();
        var warnings = new List<string>();
        var visited = new HashSet<uint>();

        // Examinar o diretório raiz.
        ScanDirectory(
            image, boot,
            boot.RootDirectoryCluster,
            null, false, "",
            false, 0,
            visited, found, warnings);

        // Examinar todos os diretórios ainda ativos.
        foreach (var directory in active.Where(
            x => x.IsDirectory))
        {
            ScanDirectory(
                image, boot,
                directory.FirstCluster,
                directory.LengthBytes,
                directory.NoFatChain,
                directory.Path,
                false, 0,
                visited, found, warnings);
        }

        return new ExFatDeletedScanResult(
            found, warnings);
    }

    private static void ScanDirectory(
        FileStream image,
        ExFatBootInfo boot,
        uint firstCluster,
        ulong? length,
        bool noFatChain,
        string parentPath,
        bool parentDeleted,
        int depth,
        HashSet<uint> visited,
        List<ExFatDeletedItem> found,
        List<string> warnings)
    {
        if (depth > MaxDepth)
        {
            warnings.Add(
                $"Profundidade excedida: {parentPath}");
            return;
        }

        if (firstCluster < 2 ||
            (ulong)firstCluster > (ulong)boot.ClusterCount + 1)
        {
            warnings.Add(
                $"Cluster inválido: {parentPath}");
            return;
        }

        if (!visited.Add(firstCluster))
            return;

        byte[] directoryData;

        try
        {
            directoryData = ReadDirectoryBytes(
                image, boot,
                firstCluster, length, noFatChain);
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
            EndOfStreamException or
            OverflowException)
        {
            warnings.Add(
                $"Não foi possível ler {parentPath}: {ex.Message}");
            return;
        }

        for (int offset = 0;
             offset + 32 <= directoryData.Length;
             offset += 32)
        {
            byte type = directoryData[offset];

            if (type == 0x00)
                break;

            // 0x05: entrada principal desativada.
            // Dentro de pasta excluída, uma entrada ativa
            // também pode ter ficado inacessível pelo caminho.
            bool inactiveEntry = type == 0x05;

            if (!inactiveEntry &&
                !(parentDeleted && type == 0x85))
                continue;

            int secondaryCount = directoryData[offset + 1];

            if (secondaryCount < 2 ||
                offset + (secondaryCount + 1) * 32 >
                    directoryData.Length)
                continue;

            byte expectedStream =
                inactiveEntry ? (byte)0x40 : (byte)0xC0;

            byte expectedName =
                inactiveEntry ? (byte)0x41 : (byte)0xC1;

            ReadOnlySpan<byte> streamEntry =
                directoryData.AsSpan(offset + 32, 32);

            if (streamEntry[0] != expectedStream)
                continue;

            int nameLength = streamEntry[3];

            if (nameLength is < 1 or > 255)
                continue;

            byte[] nameBytes = new byte[nameLength * 2];
            int written = 0;

            for (int index = 2;
                 index <= secondaryCount;
                 index++)
            {
                ReadOnlySpan<byte> entry =
                    directoryData.AsSpan(
                        offset + index * 32, 32);

                if (entry[0] != expectedName)
                    continue;

                int count = Math.Min(
                    30, nameBytes.Length - written);

                if (count <= 0)
                    break;

                entry.Slice(2, count).CopyTo(
                    nameBytes.AsSpan(written, count));

                written += count;
            }

            if (written != nameBytes.Length)
                continue;

            string name = Encoding.Unicode.GetString(
                nameBytes);

            if (name is "." or ".." ||
                name.Contains('/') ||
                name.Contains('\\') ||
                name.Contains('\0'))
                continue;

            ushort attributes =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    directoryData.AsSpan(offset + 4, 2));

            bool isDirectory = (attributes & 0x10) != 0;
            bool contiguous = (streamEntry[1] & 0x02) != 0;

            uint cluster =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    streamEntry.Slice(20, 4));

            ulong dataLength =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    streamEntry.Slice(24, 8));

            string path = string.IsNullOrEmpty(parentPath)
                ? name
                : parentPath + "/" + name;

            found.Add(new ExFatDeletedItem(
                path,
                isDirectory,
                dataLength,
                cluster,
                contiguous,
                parentDeleted));

            if (found.Count >= MaxItems)
            {
                warnings.Add("Limite de entradas atingido.");
                return;
            }

            // Tentar ler o conteúdo residual
            // de um diretório que foi excluído.
            if (isDirectory &&
                cluster >= 2 &&
                dataLength > 0)
            {
                ScanDirectory(
                    image, boot,
                    cluster,
                    dataLength,
                    contiguous,
                    path,
                    true,
                    depth + 1,
                    visited, found, warnings);
            }

            offset += secondaryCount * 32;
        }
    }

    private static byte[] ReadDirectoryBytes(
        FileStream image,
        ExFatBootInfo boot,
        uint firstCluster,
        ulong? length,
        bool noFatChain)
    {
        int clusterSize = checked(
            (int)boot.ClusterSizeBytes);

        if (clusterSize <= 0 ||
            clusterSize > MaxDirectoryBytes)
            throw new InvalidDataException(
                "Tamanho de cluster inválido.");

        if (length.HasValue &&
            (length.Value == 0 ||
             length.Value > MaxDirectoryBytes))
            throw new InvalidDataException(
                "Tamanho de diretório não suportado.");

        ulong maxClusters = length.HasValue
            ? (length.Value + (ulong)clusterSize - 1)
                / (ulong)clusterSize
            : (ulong)MaxDirectoryBytes / (ulong)clusterSize;

        using var output = new MemoryStream();
        var seen = new HashSet<uint>();

        uint current = firstCluster;

        for (ulong index = 0; index < maxClusters; index++)
        {
            if (current < 2 ||
                (ulong)current > (ulong)boot.ClusterCount + 1 ||
                !seen.Add(current))
                throw new InvalidDataException(
                    "Cadeia de clusters inválida.");

            long position = checked(
                boot.ClusterHeapAbsoluteOffset +
                ((long)current - 2) * clusterSize);

            if (position < 0 ||
                position > image.Length - clusterSize)
                throw new InvalidDataException(
                    "Cluster fora da imagem.");

            byte[] buffer = new byte[clusterSize];

            image.Position = position;
            image.ReadExactly(buffer);
            output.Write(buffer);

            if (length.HasValue &&
                (ulong)output.Length >= length.Value)
                break;

            if (noFatChain)
            {
                current = checked(current + 1);
                continue;
            }

            uint next = ReadFatEntry(image, boot, current);

            if (next >= 0xFFFFFFF8)
                break;

            if (next < 2 || next == 0xFFFFFFF7)
                throw new InvalidDataException(
                    "Cadeia FAT interrompida.");

            current = next;
        }

        if (length.HasValue &&
            (ulong)output.Length < length.Value)
            throw new InvalidDataException(
                "Diretório incompleto.");

        if (!length.HasValue &&
            output.Length >= MaxDirectoryBytes)
            throw new InvalidDataException(
                "Limite de leitura do diretório raiz atingido.");

        return output.ToArray();
    }

    private static uint ReadFatEntry(
        FileStream image,
        ExFatBootInfo boot,
        uint cluster)
    {
        long position = checked(
            boot.FatAbsoluteOffset + (long)cluster * 4);

        long fatEnd = checked(
            boot.FatAbsoluteOffset +
            (long)boot.FatLengthSectors *
            boot.BytesPerSector);

        if (position < boot.FatAbsoluteOffset ||
            position > fatEnd - 4 ||
            position > image.Length - 4)
            throw new InvalidDataException(
                "Entrada FAT fora dos limites.");

        Span<byte> bytes = stackalloc byte[4];

        image.Position = position;
        image.ReadExactly(bytes);

        return BinaryPrimitives.ReadUInt32LittleEndian(
            bytes);
    }
}
