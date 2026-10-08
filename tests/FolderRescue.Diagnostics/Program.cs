using System;
using System.Linq;
using FolderRescue.Core;

if (args.Length switch
{
    1 => false,
    2 => args[1] != "--deleted",
    3 => args[1] is not (
        "--extract" or
        "--recover-deleted" or
        "--clusters"),
    4 => args[1] != "--recover-fat",
    _ => true
})
{
    Console.WriteLine(
        "Uso: <imagem> [--deleted | --extract <destino> | " +
        "--recover-deleted <destino> | --clusters <arquivo> | " +
        "--recover-fat <arquivo> <destino>]");
    Environment.ExitCode = 1;
    return;
}

try
{
    string imagePath = args[0];
    var boot = ExFatBootSectorReader.ReadMbrImage(
        imagePath);

    if (args.Length == 4 &&
        args[1] == "--recover-fat")
    {
        var scan = ExFatDeletedEntryScanner.Scan(
            imagePath, boot);

        foreach (var warning in scan.Warnings)
            Console.WriteLine($"[AVISO] {warning}");

        var candidates = scan.Items.Where(x =>
            !x.IsDirectory &&
            x.Path == args[2]).ToList();

        if (candidates.Count != 1)
            throw new InvalidDataException(
                "É necessário encontrar exatamente uma " +
                "entrada excluída com esse caminho.");

        var item = candidates[0];

        var result = ExFatResidualFatRecovery.Recover(
            imagePath, boot, item, args[3]);

        Console.WriteLine("=== FAT RESIDUAL ===");
        Console.WriteLine($"Arquivo: {item.Path}");
        Console.WriteLine($"Clusters: {result.ClusterCount}");
        Console.WriteLine($"Extensões: {result.ExtentCount}");
        Console.WriteLine($"Bytes extraídos: {result.LengthBytes}");
        Console.WriteLine(
            "[CANDIDATO EXTRAÍDO] Verificar SHA-256.");

        return;
    }

    if (args.Length >= 2 && args[1] == "--deleted")
    {
        var scan = ExFatDeletedEntryScanner.Scan(
            imagePath, boot);

        foreach (var item in scan.Items)
            Console.WriteLine(
                $"[EXCLUÍDO] {item.Path}");

        Console.WriteLine(
            $"Pastas excluídas: {scan.Items.Count(x => x.IsDirectory)}");
        Console.WriteLine(
            $"Arquivos excluídos: {scan.Items.Count(x => !x.IsDirectory)}");

        foreach (var warning in scan.Warnings)
            Console.WriteLine($"[AVISO] {warning}");

        return;
    }

    if (args.Length == 3 &&
        args[1] == "--recover-deleted")
    {
        var scan = ExFatDeletedEntryScanner.Scan(
            imagePath, boot);

        foreach (var warning in scan.Warnings)
            Console.WriteLine($"[AVISO] {warning}");

        var result = ExFatDeletedFileRecovery.Recover(
            imagePath, boot, scan.Items, args[2]);

        foreach (var message in result.Messages)
            Console.WriteLine(message);

        Console.WriteLine();
        Console.WriteLine("=== RESULTADO DA RECUPERAÇÃO ===");
        Console.WriteLine($"Recuperados: {result.Recovered}");
        Console.WriteLine($"Ignorados: {result.Skipped}");

        if (result.Skipped > 0)
            Environment.ExitCode = 2;

        return;
    }

    var items = ExFatDirectoryReader.ReadTree(
        imagePath, boot);

    if (args.Length == 3 && args[1] == "--clusters")
    {
        var file = items.FirstOrDefault(x =>
            !x.IsDirectory && x.Path == args[2]);

        if (file is null)
            throw new FileNotFoundException(
                $"Arquivo não encontrado: {args[2]}");

        var report = ExFatClusterInspector.Inspect(
            imagePath, boot, file);

        Console.WriteLine("=== INSPEÇÃO DE CLUSTERS ===");
        Console.WriteLine($"Arquivo: {report.FilePath}");
        Console.WriteLine($"Tamanho: {file.LengthBytes} bytes");
        Console.WriteLine($"Cluster inicial: {file.FirstCluster}");
        Console.WriteLine($"NoFatChain: {report.NoFatChain}");
        Console.WriteLine($"Clusters: {report.ClusterCount}");
        Console.WriteLine($"Extensões: {report.Extents.Count}");
        Console.WriteLine(
            $"Fragmentado: {(report.IsFragmented ? "SIM" : "NÃO")}");

        Console.WriteLine();
        Console.WriteLine("Primeiras extensões:");

        foreach (var extent in report.Extents.Take(12))
        {
            ulong last =
                (ulong)extent.FirstCluster +
                extent.ClusterCount - 1;

            Console.WriteLine(
                $"  {extent.FirstCluster} até {last} " +
                $"({extent.ClusterCount} clusters)");
        }

        return;
    }

    Console.WriteLine(
        $"Diretórios ativos: {items.Count(x => x.IsDirectory)}");
    Console.WriteLine(
        $"Arquivos ativos: {items.Count(x => !x.IsDirectory)}");

    if (args.Length == 3)
    {
        ExFatFileExtractor.ExtractTree(
            imagePath, boot, items, args[2]);

        Console.WriteLine("Extração concluída.");
    }
    else
    {
        foreach (var item in items)
            Console.WriteLine(item.Path);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERRO: {ex.Message}");
    Environment.ExitCode = 1;
}
