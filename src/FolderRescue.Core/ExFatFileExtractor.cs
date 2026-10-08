using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace FolderRescue.Core;

public static class ExFatFileExtractor
{
    public static void ExtractTree(
        string imagePath,
        ExFatBootInfo boot,
        IReadOnlyList<ExFatDirectoryItem> items,
        string outputDirectory)
    {
        string root = Path.GetFullPath(outputDirectory);

        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException(
                "A pasta de saída já existe. Escolha uma pasta nova.");

        string prefix = root.TrimEnd(
            Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        using var image = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        Directory.CreateDirectory(root);

        foreach (var item in items)
        {
            string relative = item.Path.Replace(
                '/', Path.DirectorySeparatorChar);

            string destination = Path.GetFullPath(
                Path.Combine(root, relative));

            if (!destination.StartsWith(prefix, comparison))
                throw new InvalidDataException(
                    "Caminho fora da pasta de destino.");

            if (item.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);

            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);

            CopyFile(image, output, boot, item);
            Console.WriteLine($"[EXTRAÍDO] {item.Path}");
        }
    }

    private static void CopyFile(
        FileStream image,
        FileStream output,
        ExFatBootInfo boot,
        ExFatDirectoryItem item)
    {
        if (item.LengthBytes == 0)
            return;

        ulong maxVolumeData =
            (ulong)boot.ClusterCount *
            (ulong)boot.ClusterSizeBytes;

        if (item.LengthBytes > maxVolumeData)
            throw new InvalidDataException(
                "Arquivo maior que a área de dados.");

        byte[] buffer = new byte[64 * 1024];

        ulong remaining = item.LengthBytes;
        uint cluster = item.FirstCluster;

        var visited = new HashSet<uint>();

        while (remaining > 0)
        {
            if (cluster < 2 ||
                (ulong)cluster > (ulong)boot.ClusterCount + 1)
                throw new InvalidDataException(
                    "Cluster de arquivo inválido.");

            if (!visited.Add(cluster))
                throw new InvalidDataException(
                    "Ciclo detectado na cadeia FAT.");

            long position = checked(
                boot.ClusterHeapAbsoluteOffset +
                ((long)cluster - 2) * boot.ClusterSizeBytes);

            ulong clusterBytes = Math.Min(
                remaining, (ulong)boot.ClusterSizeBytes);

            if (position < 0 ||
                position > image.Length - (long)clusterBytes)
                throw new InvalidDataException(
                    "Arquivo ultrapassa os limites da imagem.");

            image.Position = position;

            ulong pending = clusterBytes;

            while (pending > 0)
            {
                int count = (int)Math.Min(
                    pending, (ulong)buffer.Length);

                image.ReadExactly(buffer.AsSpan(0, count));
                output.Write(buffer.AsSpan(0, count));

                pending -= (ulong)count;
            }

            remaining -= clusterBytes;

            if (remaining == 0)
                break;

            if (item.NoFatChain)
            {
                cluster = checked(cluster + 1);
            }
            else
            {
                uint next = ReadFatEntry(
                    image, boot, cluster);

                if (next >= 0xFFFFFFF8)
                    throw new InvalidDataException(
                        "Cadeia FAT terminou antes do arquivo.");

                if (next < 2 || next == 0xFFFFFFF7)
                    throw new InvalidDataException(
                        "Entrada FAT inválida.");

                cluster = next;
            }
        }
    }

    private static uint ReadFatEntry(
        FileStream image,
        ExFatBootInfo boot,
        uint cluster)
    {
        long fatStart = boot.FatAbsoluteOffset;

        long fatEnd = checked(
            fatStart +
            (long)boot.FatLengthSectors *
            boot.BytesPerSector);

        long position = checked(
            fatStart + (long)cluster * 4);

        if (position < fatStart ||
            position > fatEnd - 4 ||
            position > image.Length - 4)
            throw new InvalidDataException(
                "Entrada FAT fora dos limites.");

        Span<byte> entry = stackalloc byte[4];

        image.Position = position;
        image.ReadExactly(entry);

        return BinaryPrimitives.ReadUInt32LittleEndian(entry);
    }
}
