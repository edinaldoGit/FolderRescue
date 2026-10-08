using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace FolderRescue.Core;

public sealed record ExFatClusterExtent(
    uint FirstCluster,
    ulong ClusterCount);

public sealed record ExFatClusterReport(
    string FilePath,
    ulong ClusterCount,
    bool NoFatChain,
    IReadOnlyList<ExFatClusterExtent> Extents)
{
    public bool IsFragmented => Extents.Count > 1;
}

public static class ExFatClusterInspector
{
    private const ulong MaxInspectedClusters = 1_000_000;

    public static ExFatClusterReport Inspect(
        string imagePath,
        ExFatBootInfo boot,
        ExFatDirectoryItem item)
    {
        if (item.IsDirectory)
            throw new InvalidDataException(
                "Selecione um arquivo, não um diretório.");

        if (item.LengthBytes == 0)
            throw new InvalidDataException(
                "Arquivo vazio não possui clusters de dados.");

        ulong clusterSize = checked(
            (ulong)boot.ClusterSizeBytes);

        if (clusterSize == 0)
            throw new InvalidDataException(
                "Tamanho de cluster inválido.");

        ulong required =
            item.LengthBytes / clusterSize +
            (item.LengthBytes % clusterSize == 0 ? 0UL : 1UL);

        if (required > MaxInspectedClusters ||
            required > boot.ClusterCount)
            throw new InvalidDataException(
                "Quantidade de clusters fora do limite.");

        using var image = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var extents = new List<ExFatClusterExtent>();

        uint current = item.FirstCluster;

        // Arquivos NoFatChain usam clusters consecutivos.
        if (item.NoFatChain)
        {
            ulong last = (ulong)current + required - 1;

            if (current < 2 ||
                last > (ulong)boot.ClusterCount + 1)
                throw new InvalidDataException(
                    "Extensão contígua fora do volume.");

            CheckCluster(image, boot, current);
            CheckCluster(image, boot, checked((uint)last));

            extents.Add(new ExFatClusterExtent(
                current, required));

            return new ExFatClusterReport(
                item.Path,
                required,
                true,
                extents);
        }

        // A primeira versão suporta volumes com uma FAT.
        image.Position = checked(
            boot.PartitionOffsetBytes + 110);

        if (image.ReadByte() != 1)
            throw new NotSupportedException(
                "Inspeção de múltiplas FATs ainda não suportada.");

        var visited = new HashSet<uint>();

        for (ulong index = 0; index < required; index++)
        {
            CheckCluster(image, boot, current);

            if (!visited.Add(current))
                throw new InvalidDataException(
                    "Ciclo na cadeia FAT.");

            if (extents.Count == 0)
            {
                extents.Add(
                    new ExFatClusterExtent(current, 1));
            }
            else
            {
                var lastExtent = extents[^1];

                ulong expectedNext =
                    (ulong)lastExtent.FirstCluster +
                    lastExtent.ClusterCount;

                if ((ulong)current == expectedNext)
                {
                    extents[^1] = lastExtent with
                    {
                        ClusterCount =
                            lastExtent.ClusterCount + 1
                    };
                }
                else
                {
                    extents.Add(
                        new ExFatClusterExtent(current, 1));
                }
            }

            if (index + 1 == required)
                break;

            uint next = ReadFatEntry(
                image, boot, current);

            if (next >= 0xFFFFFFF8)
                throw new InvalidDataException(
                    "Cadeia FAT terminou antes do esperado.");

            if (next < 2 || next == 0xFFFFFFF7)
                throw new InvalidDataException(
                    "Entrada FAT inválida.");

            current = next;
        }

        return new ExFatClusterReport(
            item.Path,
            required,
            false,
            extents);
    }

    private static void CheckCluster(
        FileStream image,
        ExFatBootInfo boot,
        uint cluster)
    {
        if (cluster < 2 ||
            (ulong)cluster > (ulong)boot.ClusterCount + 1)
            throw new InvalidDataException(
                "Cluster fora do volume.");

        long position = checked(
            boot.ClusterHeapAbsoluteOffset +
            ((long)cluster - 2) *
            boot.ClusterSizeBytes);

        if (position < 0 ||
            position > image.Length - boot.ClusterSizeBytes)
            throw new InvalidDataException(
                "Cluster fora dos limites da imagem.");
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

        Span<byte> buffer = stackalloc byte[4];

        image.Position = position;
        image.ReadExactly(buffer);

        return BinaryPrimitives.ReadUInt32LittleEndian(
            buffer);
    }
}
