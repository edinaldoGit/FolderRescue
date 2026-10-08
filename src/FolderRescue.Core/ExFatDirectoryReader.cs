using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FolderRescue.Core;

public sealed record ExFatDirectoryItem(
    string Path,
    bool IsDirectory,
    ulong LengthBytes,
    uint FirstCluster,
    bool NoFatChain);

public static class ExFatDirectoryReader
{
    private const int MaxDirectoryBytes = 16 * 1024 * 1024;
    private const int MaxDepth = 20;
    private const int MaxItems = 20000;

    public static IReadOnlyList<ExFatDirectoryItem> ReadTree(
        string imagePath,
        ExFatBootInfo boot)
    {
        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var items = new List<ExFatDirectoryItem>();
        var visitedDirectories = new HashSet<uint>();

        Visit(
            stream, boot,
            boot.RootDirectoryCluster,
            null, false, "",
            0, visitedDirectories, items);

        return items;
    }

    private static void Visit(
        FileStream stream,
        ExFatBootInfo boot,
        uint startCluster,
        ulong? directoryLength,
        bool contiguous,
        string parentPath,
        int depth,
        HashSet<uint> visitedDirectories,
        List<ExFatDirectoryItem> items)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException(
                "Profundidade máxima de diretórios excedida.");

        if (!visitedDirectories.Add(startCluster))
            throw new InvalidDataException(
                "Referência circular entre diretórios.");

        byte[] data = ReadDirectoryData(
            stream, boot, startCluster,
            directoryLength, contiguous);

        for (int offset = 0;
             offset + 32 <= data.Length;
             offset += 32)
        {
            byte entryType = data[offset];

            // Fim das entradas em uso.
            if (entryType == 0x00)
                break;

            // 0x85 = entrada principal de arquivo/diretório.
            // Entradas excluídas e metadados são ignorados.
            if (entryType != 0x85)
                continue;

            int secondaryCount = data[offset + 1];
            int setLength = (secondaryCount + 1) * 32;

            if (secondaryCount < 2 ||
                offset + setLength > data.Length)
                throw new InvalidDataException(
                    "Conjunto de entradas exFAT incompleto.");

            // A extensão de fluxo deve vir após a entrada 0x85.
            ReadOnlySpan<byte> streamEntry =
                data.AsSpan(offset + 32, 32);

            if (streamEntry[0] != 0xC0)
                throw new InvalidDataException(
                    "Extensão de fluxo exFAT inválida.");

            ushort attributes =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    data.AsSpan(offset + 4, 2));

            bool isDirectory = (attributes & 0x10) != 0;
            bool noFatChain = (streamEntry[1] & 0x02) != 0;

            int nameLength = streamEntry[3];

            uint firstCluster =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    streamEntry.Slice(20, 4));

            ulong dataLength =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    streamEntry.Slice(24, 8));

            // Cada entrada 0xC1 guarda até 15 caracteres UTF-16.
            var nameBytes = new List<byte>();
            int requiredBytes = nameLength * 2;

            for (int secondary = 2;
                 secondary <= secondaryCount;
                 secondary++)
            {
                ReadOnlySpan<byte> nameEntry =
                    data.AsSpan(offset + secondary * 32, 32);

                if (nameEntry[0] != 0xC1)
                    continue;

                int count = Math.Min(
                    30, requiredBytes - nameBytes.Count);

                for (int i = 0; i < count; i++)
                    nameBytes.Add(nameEntry[2 + i]);
            }

            if (nameLength == 0 ||
                nameBytes.Count != requiredBytes)
                throw new InvalidDataException(
                    "Nome de arquivo exFAT incompleto.");

            string name = Encoding.Unicode.GetString(
                nameBytes.ToArray());

            if (name is "." or ".." ||
                name.Contains('/') ||
                name.Contains('\\') ||
                name.Contains('\0'))
                throw new InvalidDataException(
                    "Nome de arquivo inválido.");

            string fullPath = string.IsNullOrEmpty(parentPath)
                ? name
                : parentPath + "/" + name;

            items.Add(new ExFatDirectoryItem(
                fullPath,
                isDirectory,
                dataLength,
                firstCluster,
                noFatChain));

            if (items.Count > MaxItems)
                throw new InvalidDataException(
                    "Limite de itens excedido.");

            if (isDirectory)
            {
                if (firstCluster < 2 || dataLength == 0)
                    throw new InvalidDataException(
                        "Diretório sem dados válidos.");

                Visit(
                    stream, boot,
                    firstCluster,
                    dataLength,
                    noFatChain,
                    fullPath,
                    depth + 1,
                    visitedDirectories,
                    items);
            }

            // Ignorar as demais entradas deste conjunto.
            offset += secondaryCount * 32;
        }
    }

    private static byte[] ReadDirectoryData(
        FileStream stream,
        ExFatBootInfo boot,
        uint startCluster,
        ulong? directoryLength,
        bool contiguous)
    {
        int clusterSize = checked(
            (int)boot.ClusterSizeBytes);

        if (clusterSize <= 0 ||
            clusterSize > MaxDirectoryBytes)
            throw new InvalidDataException(
                "Tamanho de cluster não suportado.");

        if (directoryLength.HasValue &&
            (directoryLength.Value == 0 ||
             directoryLength.Value > MaxDirectoryBytes))
            throw new InvalidDataException(
                "Diretório maior que o limite permitido.");

        if (contiguous && !directoryLength.HasValue)
            throw new InvalidDataException(
                "Diretório contíguo sem tamanho conhecido.");

        ulong expectedClusters = directoryLength.HasValue
            ? (directoryLength.Value + (ulong)clusterSize - 1)
                / (ulong)clusterSize
            : ulong.MaxValue;

        using var output = new MemoryStream();
        var visitedClusters = new HashSet<uint>();

        uint current = startCluster;

        for (ulong index = 0;
             index < expectedClusters;
             index++)
        {
            if (current < 2 ||
                (ulong)current > (ulong)boot.ClusterCount + 1)
                throw new InvalidDataException(
                    "Cluster fora dos limites do volume.");

            if (!visitedClusters.Add(current))
                throw new InvalidDataException(
                    "Ciclo detectado na cadeia de clusters.");

            if (output.Length + clusterSize > MaxDirectoryBytes)
                throw new InvalidDataException(
                    "Limite de leitura de diretório excedido.");

            long position = checked(
                boot.ClusterHeapAbsoluteOffset +
                ((long)current - 2) * clusterSize);

            if (position < 0 ||
                position > stream.Length - clusterSize)
                throw new InvalidDataException(
                    "Leitura fora dos limites da imagem.");

            byte[] cluster = new byte[clusterSize];

            stream.Position = position;
            stream.ReadExactly(cluster);
            output.Write(cluster);

            if (index + 1 >= expectedClusters)
                break;

            if (contiguous)
            {
                current = checked(current + 1);
                continue;
            }

            uint next = ReadFatEntry(stream, boot, current);

            // Fim da cadeia FAT.
            if (next >= 0xFFFFFFF8)
                break;

            if (next < 2 || next == 0xFFFFFFF7)
                throw new InvalidDataException(
                    "Cadeia FAT inválida.");

            current = next;
        }

        if (directoryLength.HasValue &&
            (ulong)output.Length < directoryLength.Value)
            throw new InvalidDataException(
                "Cadeia de diretório truncada.");

        return output.ToArray();
    }

    private static uint ReadFatEntry(
        FileStream stream,
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
            position > stream.Length - 4)
            throw new InvalidDataException(
                "Entrada FAT fora dos limites.");

        Span<byte> buffer = stackalloc byte[4];

        stream.Position = position;
        stream.ReadExactly(buffer);

        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}
