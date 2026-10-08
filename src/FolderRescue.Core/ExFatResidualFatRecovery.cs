using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace FolderRescue.Core;

public sealed record ExFatResidualFatResult(
    int ClusterCount,
    int ExtentCount,
    ulong LengthBytes);

public static class ExFatResidualFatRecovery
{
    private const ulong MaxFileBytes = 256UL * 1024 * 1024;

    public static ExFatResidualFatResult Recover(
        string imagePath,
        ExFatBootInfo boot,
        ExFatDeletedItem item,
        string outputPath)
    {
        if (item.IsDirectory)
            throw new InvalidDataException(
                "O item selecionado é um diretório.");

        if (item.NoFatChain)
            throw new InvalidDataException(
                "Esta operação exige uma cadeia FAT residual.");

        if (item.LengthBytes == 0 ||
            item.LengthBytes > MaxFileBytes)
            throw new InvalidDataException(
                "Tamanho não suportado no experimento.");

        ulong clusterSize = checked(
            (ulong)boot.ClusterSizeBytes);

        if (clusterSize == 0)
            throw new InvalidDataException(
                "Tamanho de cluster inválido.");

        ulong required =
            (item.LengthBytes + clusterSize - 1) /
            clusterSize;

        if (required == 0 ||
            required > boot.ClusterCount)
            throw new InvalidDataException(
                "Quantidade de clusters inválida.");

        using var image = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        byte[] bitmap =
            ExFatDeletedFileRecovery.ReadAllocationBitmap(
                image, boot);

        var chain = new List<uint>(
            checked((int)required));

        var visited = new HashSet<uint>();

        uint current = item.FirstCluster;
        uint previous = 0;
        int extents = 0;

        // Validar a cadeia inteira antes de criar
        // qualquer arquivo de saída.
        for (ulong index = 0; index < required; index++)
        {
            if (current < 2 ||
                (ulong)current > (ulong)boot.ClusterCount + 1)
                throw new InvalidDataException(
                    "Cluster fora do volume.");

            if (!visited.Add(current))
                throw new InvalidDataException(
                    "Ciclo detectado na FAT residual.");

            ulong bit = (ulong)current - 2;
            ulong byteIndex = bit / 8;

            if (byteIndex >= (ulong)bitmap.Length)
                throw new InvalidDataException(
                    "Bitmap insuficiente.");

            int mask = 1 << (int)(bit % 8);

            if ((bitmap[(int)byteIndex] & mask) != 0)
                throw new InvalidDataException(
                    "Cluster atualmente ocupado. Recuperação recusada.");

            long position = checked(
                boot.ClusterHeapAbsoluteOffset +
                ((long)current - 2) *
                boot.ClusterSizeBytes);

            if (position < 0 ||
                position > image.Length - boot.ClusterSizeBytes)
                throw new InvalidDataException(
                    "Dados fora dos limites da imagem.");

            if (index == 0 ||
                (ulong)current != (ulong)previous + 1)
                extents++;

            chain.Add(current);

            uint next = ReadFatEntry(
                image, boot, current);

            if (index + 1 == required)
            {
                if (next < 0xFFFFFFF8)
                    throw new InvalidDataException(
                        "Fim da cadeia FAT não encontrado.");
            }
            else
            {
                if (next < 2 ||
                    (ulong)next > (ulong)boot.ClusterCount + 1)
                    throw new InvalidDataException(
                        "Cadeia FAT interrompida.");
            }

            previous = current;
            current = next;
        }

        string destination = Path.GetFullPath(outputPath);
        string source = Path.GetFullPath(imagePath);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(source, destination, comparison))
            throw new IOException(
                "A saída não pode sobrescrever a imagem.");

        string parent = Path.GetDirectoryName(destination)
            ?? throw new IOException("Destino inválido.");

        Directory.CreateDirectory(parent);

        bool created = false;

        try
        {
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);

            created = true;

            byte[] buffer = new byte[64 * 1024];
            ulong remaining = item.LengthBytes;

            foreach (uint cluster in chain)
            {
                long position = checked(
                    boot.ClusterHeapAbsoluteOffset +
                    ((long)cluster - 2) *
                    boot.ClusterSizeBytes);

                image.Position = position;

                ulong pending = Math.Min(
                    remaining, clusterSize);

                remaining -= pending;

                while (pending > 0)
                {
                    int count = (int)Math.Min(
                        pending, (ulong)buffer.Length);

                    image.ReadExactly(
                        buffer.AsSpan(0, count));

                    output.Write(
                        buffer.AsSpan(0, count));

                    pending -= (ulong)count;
                }
            }

            if (remaining != 0)
                throw new InvalidDataException(
                    "Extração incompleta.");

            output.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(destination); }
                catch (IOException) { }
            }

            throw;
        }

        return new ExFatResidualFatResult(
            chain.Count,
            extents,
            item.LengthBytes);
    }

    private static uint ReadFatEntry(
        FileStream image,
        ExFatBootInfo boot,
        uint cluster)
    {
        long start = boot.FatAbsoluteOffset;

        long end = checked(
            start +
            (long)boot.FatLengthSectors *
            boot.BytesPerSector);

        long position = checked(
            start + (long)cluster * 4);

        if (position < start ||
            position > end - 4 ||
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
