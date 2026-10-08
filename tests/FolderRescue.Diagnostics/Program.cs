using System;
using System.Linq;
using FolderRescue.Core;

if (args.Length < 1 ||
    args.Length > 3 ||
    (args.Length == 2 && args[1] != "--deleted") ||
    (args.Length == 3 &&
     args[1] != "--extract" &&
     args[1] != "--recover-deleted"))
{
    Console.WriteLine(
        "Uso: <imagem> [--deleted | --extract <destino> | --recover-deleted <destino>]");
    return;
}

try
{
    string imagePath = args[0];
    var boot = ExFatBootSectorReader.ReadMbrImage(
        imagePath);

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
