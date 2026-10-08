using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FolderRescue.Core;

const int SectorSize = 512;
const int PartitionLba = 2048;
const int PartitionSectors = 14336;
const int FatOffset = 24;
const int FatLength = 128;
const int HeapOffset = 152;
const int ClusterCount = 14000;

byte[] activeBytes = Encoding.UTF8.GetBytes(
    "Arquivo ativo do FolderRescue.\n");

byte[] deletedBytes = Encoding.UTF8.GetBytes(
    "Arquivo excluido do FolderRescue.\n");

string temp = Path.Combine(
    Path.GetTempPath(),
    "FolderRescue.PortableTests",
    Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(temp);

string imagePath = Path.Combine(temp, "fixture_exfat.img");
int passed = 0;
int failed = 0;

void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

void Run(string name, Action action)
{
    try
    {
        action();
        passed++;
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
    }
}

void CreateFixture(string path)
{
    using var file = new FileStream(
        path, FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None);

    file.SetLength(8L * 1024 * 1024);

    long partitionStart = (long)PartitionLba * SectorSize;
    long fatStart = partitionStart + FatOffset * SectorSize;
    long heapStart = partitionStart + HeapOffset * SectorSize;

    long ClusterPosition(int cluster) =>
        heapStart + ((long)cluster - 2) * SectorSize;

    void WriteAt(long position, ReadOnlySpan<byte> data)
    {
        file.Position = position;
        file.Write(data);
    }

    void SetFat(int cluster, uint next)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, next);
        WriteAt(fatStart + (long)cluster * 4, data);
    }

    // Tabela MBR com uma partição Microsoft/exFAT.
    byte[] mbr = new byte[512];
    mbr[450] = 0x07;

    BinaryPrimitives.WriteUInt32LittleEndian(
        mbr.AsSpan(454, 4), PartitionLba);

    BinaryPrimitives.WriteUInt32LittleEndian(
        mbr.AsSpan(458, 4), PartitionSectors);

    mbr[510] = 0x55;
    mbr[511] = 0xAA;

    WriteAt(0, mbr);

    // Setor de inicialização exFAT simplificado.
    byte[] boot = new byte[512];
    Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(boot, 3);

    BinaryPrimitives.WriteUInt64LittleEndian(
        boot.AsSpan(72, 8), PartitionSectors);

    BinaryPrimitives.WriteUInt32LittleEndian(
        boot.AsSpan(80, 4), FatOffset);

    BinaryPrimitives.WriteUInt32LittleEndian(
        boot.AsSpan(84, 4), FatLength);

    BinaryPrimitives.WriteUInt32LittleEndian(
        boot.AsSpan(88, 4), HeapOffset);

    BinaryPrimitives.WriteUInt32LittleEndian(
        boot.AsSpan(92, 4), ClusterCount);

    BinaryPrimitives.WriteUInt32LittleEndian(
        boot.AsSpan(96, 4), 4);

    boot[108] = 9;  // 512 bytes por setor
    boot[109] = 0;  // 1 setor por cluster
    boot[110] = 1;  // Uma FAT
    boot[510] = 0x55;
    boot[511] = 0xAA;

    WriteAt(partitionStart, boot);

    // FAT do diretório raiz e do bitmap.
    SetFat(4, 0xFFFFFFFF);
    SetFat(5, 6);
    SetFat(6, 7);
    SetFat(7, 8);
    SetFat(8, 0xFFFFFFFF);
    SetFat(9, 0xFFFFFFFF);

    // Bitmap: clusters 4 a 9 ocupados.
    // Cluster 10 livre, embora contenha bytes residuais.
    byte[] bitmap = new byte[(ClusterCount + 7) / 8];
    bitmap[0] = 0xFC;

    for (int i = 0; i < 4; i++)
    {
        int count = Math.Min(
            SectorSize, bitmap.Length - i * SectorSize);

        WriteAt(
            ClusterPosition(5 + i),
            bitmap.AsSpan(i * SectorSize, count));
    }

    byte[] root = new byte[512];

    // Entrada do bitmap de alocação.
    root[0] = 0x81;

    BinaryPrimitives.WriteUInt32LittleEndian(
        root.AsSpan(20, 4), 5);

    BinaryPrimitives.WriteUInt64LittleEndian(
        root.AsSpan(24, 8), (ulong)bitmap.Length);

    void AddFile(
        int offset,
        string name,
        uint cluster,
        byte[] content,
        bool deleted)
    {
        byte[] primary = new byte[32];
        byte[] stream = new byte[32];
        byte[] filename = new byte[32];

        primary[0] = deleted ? (byte)0x05 : (byte)0x85;
        primary[1] = 2;
        primary[4] = 0x20;

        stream[0] = deleted ? (byte)0x40 : (byte)0xC0;
        stream[1] = 0x02;
        stream[3] = (byte)name.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(
            stream.AsSpan(20, 4), cluster);

        BinaryPrimitives.WriteUInt64LittleEndian(
            stream.AsSpan(24, 8), (ulong)content.Length);

        filename[0] = deleted ? (byte)0x41 : (byte)0xC1;

        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        nameBytes.CopyTo(filename, 2);

        primary.CopyTo(root, offset);
        stream.CopyTo(root, offset + 32);
        filename.CopyTo(root, offset + 64);

        WriteAt(ClusterPosition((int)cluster), content);
    }

    AddFile(32, "ativo.txt", 9, activeBytes, false);
    AddFile(128, "apagado.txt", 10, deletedBytes, true);

    WriteAt(ClusterPosition(4), root);
}

try
{
    CreateFixture(imagePath);

    Run("Reconhecer setor exFAT sintético", () =>
    {
        var boot = ExFatBootSectorReader.ReadMbrImage(imagePath);

        Check(boot.BytesPerSector == 512,
            "Tamanho de setor incorreto.");

        Check(boot.RootDirectoryCluster == 4,
            "Cluster raiz incorreto.");
    });

    Run("Encontrar arquivo ativo", () =>
    {
        var boot = ExFatBootSectorReader.ReadMbrImage(imagePath);
        var items = ExFatDirectoryReader.ReadTree(imagePath, boot);

        Check(items.Count == 1 &&
              items[0].Path == "ativo.txt",
            "Arquivo ativo não localizado.");
    });

    Run("Encontrar arquivo excluído", () =>
    {
        var boot = ExFatBootSectorReader.ReadMbrImage(imagePath);
        var scan = ExFatDeletedEntryScanner.Scan(imagePath, boot);

        Check(scan.Items.Count == 1 &&
              scan.Items[0].Path == "apagado.txt",
            "Arquivo excluído não localizado.");
    });

    Run("Extrair arquivo ativo com integridade", () =>
    {
        var boot = ExFatBootSectorReader.ReadMbrImage(imagePath);
        var items = ExFatDirectoryReader.ReadTree(imagePath, boot);
        string output = Path.Combine(temp, "ativos");

        ExFatFileExtractor.ExtractTree(
            imagePath, boot, items, output);

        byte[] actual = File.ReadAllBytes(
            Path.Combine(output, "ativo.txt"));

        Check(
            SHA256.HashData(actual).SequenceEqual(
                SHA256.HashData(activeBytes)),
            "SHA-256 divergente.");
    });

    Run("Recuperar arquivo excluído com integridade", () =>
    {
        var boot = ExFatBootSectorReader.ReadMbrImage(imagePath);
        var scan = ExFatDeletedEntryScanner.Scan(imagePath, boot);
        string output = Path.Combine(temp, "recuperados");

        var result = ExFatDeletedFileRecovery.Recover(
            imagePath, boot, scan.Items, output);

        Check(result.Recovered == 1 && result.Skipped == 0,
            "Recuperação inesperada.");

        byte[] actual = File.ReadAllBytes(
            Path.Combine(output, "apagado.txt"));

        Check(
            SHA256.HashData(actual).SequenceEqual(
                SHA256.HashData(deletedBytes)),
            "SHA-256 divergente.");
    });

    Run("Recusar cluster reutilizado", () =>
    {
        string changed = Path.Combine(temp, "ocupado.img");
        File.Copy(imagePath, changed);

        // Marcar o cluster 10 como ocupado no bitmap.
        long bitmapPosition =
            (long)PartitionLba * SectorSize +
            HeapOffset * SectorSize +
            (5L - 2) * SectorSize;

        using (var file = new FileStream(
            changed, FileMode.Open, FileAccess.Write))
        {
            file.Position = bitmapPosition + 1;
            file.WriteByte(0x01);
        }

        var boot = ExFatBootSectorReader.ReadMbrImage(changed);
        var scan = ExFatDeletedEntryScanner.Scan(changed, boot);

        var result = ExFatDeletedFileRecovery.Recover(
            changed, boot, scan.Items,
            Path.Combine(temp, "recusa"));

        Check(result.Recovered == 0 && result.Skipped == 1,
            "Cluster ocupado não foi recusado.");
    });
}
finally
{
    Console.WriteLine();
    Console.WriteLine("=== TESTES PORTÁTEIS ===");
    Console.WriteLine($"Aprovados: {passed}");
    Console.WriteLine($"Reprovados: {failed}");

    if (failed == 0)
        Directory.Delete(temp, recursive: true);
    else
        Console.WriteLine($"Dados preservados em: {temp}");

    Environment.ExitCode = failed == 0 ? 0 : 1;
}
