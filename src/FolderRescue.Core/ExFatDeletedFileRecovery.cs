using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace FolderRescue.Core;

public sealed record ExFatRecoveryResult(
    int Recovered,
    int Skipped,
    IReadOnlyList<string> Messages);

public static class ExFatDeletedFileRecovery
{
    private const ulong MaxBitmapBytes = 64UL * 1024 * 1024;
    private const ulong MaxFileBytes = 256UL * 1024 * 1024;

    public static ExFatRecoveryResult Recover(
        string imagePath,
        ExFatBootInfo boot,
        IReadOnlyList<ExFatDeletedItem> deletedItems,
        string outputDirectory)
    {
        string root = Path.GetFullPath(outputDirectory);

        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException(
                "Destino já existe. Escolha uma pasta nova.");

        using var image = new FileStream(
            imagePath, FileMode.Open,
            FileAccess.Read, FileShare.Read);

        byte[] bitmap = ReadAllocationBitmap(image, boot);

        var messages = new List<string>();
        int recovered = 0;
        int skipped = 0;

        Directory.CreateDirectory(root);

        foreach (var item in deletedItems)
        {
            if (item.IsDirectory)
                continue;

            string? reason = CheckRecoverability(
                boot, bitmap, item);

            if (reason is not null)
            {
                messages.Add(
                    $"[IGNORADO] {item.Path}: {reason}");
                skipped++;
                continue;
            }

            string relative = item.Path.Replace(
                '/', Path.DirectorySeparatorChar);

            string destination = Path.GetFullPath(
                Path.Combine(root, relative));

            string rootPrefix = root.TrimEnd(
                Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!destination.StartsWith(
                rootPrefix, comparison))
                throw new InvalidDataException(
                    "Caminho de recuperação inseguro.");

            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);

            try
            {
                using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);

                CopyContiguous(image, output, boot, item);
            }
            catch
            {
                File.Delete(destination);
                throw;
            }

            messages.Add($"[RECUPERADO] {item.Path}");
            recovered++;
        }

        return new ExFatRecoveryResult(
            recovered, skipped, messages);
    }

    private static string? CheckRecoverability(
        ExFatBootInfo boot,
        byte[] bitmap,
        ExFatDeletedItem item)
    {
        if (!item.NoFatChain)
            return "cadeia FAT não contígua não suportada";

        if (item.LengthBytes == 0)
            return "arquivo vazio";

        if (item.LengthBytes > MaxFileBytes)
            return "arquivo excede o limite experimental";

        ulong clusterSize = (ulong)boot.ClusterSizeBytes;

        ulong needed =
            (item.LengthBytes + clusterSize - 1) /
            clusterSize;

        ulong first = item.FirstCluster;
        ulong last = first + needed - 1;

        if (first < 2 ||
            last > (ulong)boot.ClusterCount + 1)
            return "clusters fora dos limites";

        for (ulong cluster = first; cluster <= last; cluster++)
        {
            ulong bit = cluster - 2;

            int byteIndex = checked((int)(bit / 8));
            int bitIndex = (int)(bit % 8);

            if ((bitmap[byteIndex] & (1 << bitIndex)) != 0)
                return "cluster atualmente alocado";
        }

        return null;
    }

    private static void CopyContiguous(
        FileStream image,
        FileStream output,
        ExFatBootInfo boot,
        ExFatDeletedItem item)
    {
        long offset = checked(
            boot.ClusterHeapAbsoluteOffset +
            ((long)item.FirstCluster - 2) *
            boot.ClusterSizeBytes);

        long remaining = checked((long)item.LengthBytes);

        if (offset < 0 ||
            offset > image.Length - remaining)
            throw new InvalidDataException(
                "Dados fora dos limites da imagem.");

        image.Position = offset;

        byte[] buffer = new byte[64 * 1024];

        while (remaining > 0)
        {
            int count = (int)Math.Min(
                remaining, buffer.Length);

            image.ReadExactly(buffer.AsSpan(0, count));
            output.Write(buffer.AsSpan(0, count));

            remaining -= count;
        }
    }

    private static byte[] ReadAllocationBitmap(
        FileStream image,
        ExFatBootInfo boot)
    {
        // Esta versão experimental suporta volumes
        // exFAT com somente uma FAT ativa.
        image.Position = boot.PartitionOffsetBytes + 110;

        if (image.ReadByte() != 1)
            throw new NotSupportedException(
                "Volumes com múltiplas FATs ainda não suportados.");

        int clusterSize = checked(
            (int)boot.ClusterSizeBytes);

        long rootOffset = checked(
            boot.ClusterHeapAbsoluteOffset +
            ((long)boot.RootDirectoryCluster - 2) *
            clusterSize);

        if (rootOffset < 0 ||
            rootOffset > image.Length - clusterSize)
            throw new InvalidDataException(
                "Diretório raiz fora da imagem.");

        byte[] root = new byte[clusterSize];
        image.Position = rootOffset;
        image.ReadExactly(root);

        uint bitmapCluster = 0;
        ulong bitmapLength = 0;

        // No volume experimental, os metadados
        // do bitmap ficam no primeiro cluster raiz.
        for (int offset = 0;
             offset + 32 <= root.Length;
             offset += 32)
        {
            ReadOnlySpan<byte> entry =
                root.AsSpan(offset, 32);

            if (entry[0] == 0x00)
                break;

            if (entry[0] != 0x81)
                continue;

            if ((entry[1] & 0x01) != 0)
                continue;

            bitmapCluster =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    entry.Slice(20, 4));

            bitmapLength =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    entry.Slice(24, 8));

            break;
        }

        ulong minimumLength =
            ((ulong)boot.ClusterCount + 7) / 8;

        if (bitmapCluster < 2 ||
            bitmapLength < minimumLength ||
            bitmapLength > MaxBitmapBytes)
            throw new InvalidDataException(
                "Bitmap de alocação não encontrado ou inválido.");

        byte[] bitmap = new byte[checked((int)bitmapLength)];

        uint current = bitmapCluster;
        int written = 0;
        var visited = new HashSet<uint>();

        while (written < bitmap.Length)
        {
            if (current < 2 ||
                (ulong)current > (ulong)boot.ClusterCount + 1 ||
                !visited.Add(current))
                throw new InvalidDataException(
                    "Cadeia do bitmap inválida.");

            long position = checked(
                boot.ClusterHeapAbsoluteOffset +
                ((long)current - 2) * clusterSize);

            int count = Math.Min(
                clusterSize, bitmap.Length - written);

            if (position < 0 ||
                position > image.Length - count)
                throw new InvalidDataException(
                    "Bitmap fora dos limites.");

            image.Position = position;
            image.ReadExactly(
                bitmap.AsSpan(written, count));

            written += count;

            if (written == bitmap.Length)
                break;

            long fatPosition = checked(
                boot.FatAbsoluteOffset + (long)current * 4);

            long fatEnd = checked(
                boot.FatAbsoluteOffset +
                (long)boot.FatLengthSectors *
                boot.BytesPerSector);

            if (fatPosition < boot.FatAbsoluteOffset ||
                fatPosition > fatEnd - 4 ||
                fatPosition > image.Length - 4)
                throw new InvalidDataException(
                    "FAT do bitmap fora dos limites.");

            Span<byte> fatEntry = stackalloc byte[4];
            image.Position = fatPosition;
            image.ReadExactly(fatEntry);

            uint next =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    fatEntry);

            if (next < 2 || next >= 0xFFFFFFF7)
                throw new InvalidDataException(
                    "Cadeia do bitmap interrompida.");

            current = next;
        }

        return bitmap;
    }
}
