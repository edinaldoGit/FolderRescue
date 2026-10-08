using System;
using System.Buffers.Binary;
using System.IO;

namespace FolderRescue.Core;

public sealed record ExFatBootInfo(
    long PartitionOffsetBytes,
    int BytesPerSector,
    int SectorsPerCluster,
    uint FatOffsetSectors,
    uint FatLengthSectors,
    uint ClusterHeapOffsetSectors,
    uint ClusterCount,
    uint RootDirectoryCluster,
    ulong VolumeLengthSectors)
{
    public long ClusterSizeBytes =>
        (long)BytesPerSector * SectorsPerCluster;

    public long FatAbsoluteOffset =>
        PartitionOffsetBytes +
        (long)FatOffsetSectors * BytesPerSector;

    public long ClusterHeapAbsoluteOffset =>
        PartitionOffsetBytes +
        (long)ClusterHeapOffsetSectors * BytesPerSector;
}

public static class ExFatBootSectorReader
{
    public static ExFatBootInfo ReadMbrImage(string imagePath)
    {
        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        if (stream.Length < 1024)
            throw new InvalidDataException("Imagem muito pequena.");

        Span<byte> mbr = stackalloc byte[512];
        stream.ReadExactly(mbr);

        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
            throw new InvalidDataException("MBR inválido.");

        for (int index = 0; index < 4; index++)
        {
            var entry = mbr.Slice(446 + index * 16, 16);

            byte partitionType = entry[4];

            uint startLba =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    entry.Slice(8, 4));

            uint sectorCount =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    entry.Slice(12, 4));

            if (partitionType == 0xEE)
                throw new NotSupportedException(
                    "Use o leitor GPT para esta imagem.");

            if (partitionType == 0 ||
                startLba == 0 ||
                sectorCount == 0)
                continue;

            long partitionOffset = (long)startLba * 512;
            long partitionSize = (long)sectorCount * 512;

            if (partitionOffset > stream.Length - 512)
                continue;

            stream.Position = partitionOffset;

            Span<byte> boot = stackalloc byte[512];
            stream.ReadExactly(boot);

            if (!boot.Slice(3, 8).SequenceEqual("EXFAT   "u8))
                continue;

            if (boot[510] != 0x55 || boot[511] != 0xAA)
                throw new InvalidDataException(
                    "Assinatura do setor exFAT inválida.");

            int sectorShift = boot[108];
            int clusterShift = boot[109];

            if (sectorShift < 9 ||
                sectorShift > 12 ||
                clusterShift > 25 - sectorShift)
                throw new InvalidDataException(
                    "Tamanho de setor ou cluster inválido.");

            int bytesPerSector = 1 << sectorShift;
            int sectorsPerCluster = 1 << clusterShift;

            uint fatOffset =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    boot.Slice(80, 4));

            uint fatLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    boot.Slice(84, 4));

            uint heapOffset =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    boot.Slice(88, 4));

            uint clusterCount =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    boot.Slice(92, 4));

            uint rootCluster =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    boot.Slice(96, 4));

            ulong volumeLength =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    boot.Slice(72, 8));

            int fatCount = boot[110];

            if (fatCount is not (1 or 2) ||
                fatOffset == 0 ||
                fatLength == 0 ||
                clusterCount == 0 ||
                rootCluster < 2 ||
                (ulong)rootCluster > (ulong)clusterCount + 1)
                throw new InvalidDataException(
                    "Metadados exFAT inválidos.");

            ulong fatEnd =
                (ulong)fatOffset +
                (ulong)fatLength * (uint)fatCount;

            ulong heapEnd =
                (ulong)heapOffset +
                (ulong)clusterCount * (uint)sectorsPerCluster;

            ulong volumeBytes =
                volumeLength * (ulong)bytesPerSector;

            if (volumeLength == 0 ||
                fatEnd > heapOffset ||
                heapEnd > volumeLength ||
                volumeBytes > (ulong)partitionSize ||
                volumeBytes > (ulong)(stream.Length - partitionOffset))
                throw new InvalidDataException(
                    "Estrutura exFAT fora dos limites da imagem.");

            return new ExFatBootInfo(
                partitionOffset,
                bytesPerSector,
                sectorsPerCluster,
                fatOffset,
                fatLength,
                heapOffset,
                clusterCount,
                rootCluster,
                volumeLength);
        }

        throw new InvalidDataException(
            "Nenhuma partição primária exFAT encontrada na imagem MBR.");
    }
}
