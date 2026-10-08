using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRescue.Core;

string baseDir = Path.Combine(
    Environment.GetFolderPath(
        Environment.SpecialFolder.UserProfile),
    "FolderRescueTeste");

string before = Path.Combine(
    baseDir, "exfat_antes_exclusao.img");

string after = Path.Combine(
    baseDir, "exfat_exclusoes.img");

string manifestPath = Path.Combine(
    baseDir, "gabarito_exfat.json");

if (!File.Exists(before) ||
    !File.Exists(after) ||
    !File.Exists(manifestPath))
{
    Console.Error.WriteLine(
        "ERRO: imagens ou gabarito não encontrados.");
    Environment.ExitCode = 1;
    return;
}

using var document = JsonDocument.Parse(
    File.ReadAllText(manifestPath));

var manifest = document.RootElement
    .EnumerateArray()
    .ToDictionary(
        e => e.GetProperty("path").GetString()!,
        e => (
            Size: e.GetProperty("size").GetInt64(),
            Hash: e.GetProperty("sha256").GetString()!
        ),
        StringComparer.Ordinal);

var deletedPaths = new HashSet<string>(
    StringComparer.Ordinal)
{
    "Fotos/Familia/foto_01.png",
    "Fotos/Viagens/foto_02.png",
    "Fotos/Eventos/foto_01.png",
    "Fotos/Eventos/foto_02.png",
    "Fotos/Eventos/foto_03.png",
    "Fotos/Eventos/foto_04.png"
};

string temp = Path.Combine(
    baseDir,
    "regressao_" + Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(temp);

int passed = 0;
int failed = 0;

void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

void Run(string name, Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}");
        Console.WriteLine($"       {ex.Message}");
    }
}

void VerifyFile(string root, string relative)
{
    Check(manifest.ContainsKey(relative),
        $"Arquivo ausente no gabarito: {relative}");

    string fullPath = Path.Combine(
        root,
        relative.Replace(
            '/', Path.DirectorySeparatorChar));

    Check(File.Exists(fullPath),
        $"Arquivo não encontrado: {relative}");

    byte[] content = File.ReadAllBytes(fullPath);
    string sha = Convert.ToHexString(
        SHA256.HashData(content)).ToLowerInvariant();

    var expected = manifest[relative];

    Check(content.LongLength == expected.Size,
        $"Tamanho divergente: {relative}");

    Check(sha == expected.Hash,
        $"SHA-256 divergente: {relative}");
}

try
{
    var beforeBoot =
        ExFatBootSectorReader.ReadMbrImage(before);

    var afterBoot =
        ExFatBootSectorReader.ReadMbrImage(after);

    Run("Diretórios e arquivos intactos", () =>
    {
        var items = ExFatDirectoryReader.ReadTree(
            before, beforeBoot);

        Check(items.Count(x => x.IsDirectory) == 6,
            "Quantidade de diretórios diferente de 6.");

        Check(items.Count(x => !x.IsDirectory) == 13,
            "Quantidade de arquivos diferente de 13.");
    });

    Run("Estrutura após exclusões", () =>
    {
        var items = ExFatDirectoryReader.ReadTree(
            after, afterBoot);

        Check(items.Count(x => x.IsDirectory) == 5,
            "Quantidade de diretórios diferente de 5.");

        Check(items.Count(x => !x.IsDirectory) == 7,
            "Quantidade de arquivos diferente de 7.");
    });

    Run("Identificação dos itens excluídos", () =>
    {
        var scan = ExFatDeletedEntryScanner.Scan(
            after, afterBoot);

        var files = scan.Items
            .Where(x => !x.IsDirectory)
            .Select(x => x.Path.Replace(
                "FOLDERRESCUE_LAB/", ""))
            .ToHashSet(StringComparer.Ordinal);

        Check(files.SetEquals(deletedPaths),
            "Arquivos excluídos encontrados não correspondem ao esperado.");

        Check(scan.Items.Count(x => x.IsDirectory) == 1,
            "Quantidade de pastas excluídas diferente de 1.");

        Check(scan.Items.Any(x =>
            x.IsDirectory &&
            x.Path == "FOLDERRESCUE_LAB/Fotos/Eventos"),
            "Pasta Eventos não identificada.");

        Check(scan.Warnings.Count == 0,
            "O scanner emitiu avisos.");
    });

    Run("Extração íntegra das 12 fotos", () =>
    {
        string output = Path.Combine(temp, "intactos");

        var items = ExFatDirectoryReader.ReadTree(
            before, beforeBoot);

        ExFatFileExtractor.ExtractTree(
            before, beforeBoot, items, output);

        string root = Path.Combine(
            output, "FOLDERRESCUE_LAB");

        Check(manifest.Count == 12,
            "Gabarito não contém 12 fotos.");

        foreach (string path in manifest.Keys)
            VerifyFile(root, path);
    });

    Run("Recuperação íntegra das 6 fotos", () =>
    {
        string output = Path.Combine(
            temp, "recuperados");

        var scan = ExFatDeletedEntryScanner.Scan(
            after, afterBoot);

        var result = ExFatDeletedFileRecovery.Recover(
            after, afterBoot, scan.Items, output);

        Check(result.Recovered == 6,
            $"Recuperados: {result.Recovered}");

        Check(result.Skipped == 0,
            $"Ignorados: {result.Skipped}");

        string root = Path.Combine(
            output, "FOLDERRESCUE_LAB");

        foreach (string path in deletedPaths)
            VerifyFile(root, path);
    });

    Run("Recusar clusters ainda ocupados", () =>
    {
        var active = ExFatDirectoryReader.ReadTree(
            after, afterBoot);

        var file = active.Single(x =>
            x.Path ==
            "FOLDERRESCUE_LAB/Fotos/Familia/foto_02.png");

        Check(file.NoFatChain,
            "Arquivo de teste não é contíguo.");

        var candidate = new ExFatDeletedItem(
            file.Path,
            false,
            file.LengthBytes,
            file.FirstCluster,
            file.NoFatChain,
            false);

        var result = ExFatDeletedFileRecovery.Recover(
            after,
            afterBoot,
            new[] { candidate },
            Path.Combine(temp, "recusa"));

        Check(result.Recovered == 0,
            "Arquivo com clusters ocupados foi recuperado.");

        Check(result.Skipped == 1,
            "O arquivo não foi ignorado.");

        Check(result.Messages.Any(m =>
            m.Contains("cluster atualmente alocado")),
            "A recusa não foi atribuída ao bitmap.");
    });

    Run("Impedir sobrescrita da pasta de destino", () =>
    {
        string existing = Path.Combine(
            temp, "destino_existente");

        Directory.CreateDirectory(existing);

        bool blocked = false;

        try
        {
            var items = ExFatDirectoryReader.ReadTree(
                before, beforeBoot);

            ExFatFileExtractor.ExtractTree(
                before, beforeBoot, items, existing);
        }
        catch (IOException)
        {
            blocked = true;
        }

        Check(blocked,
            "O extrator aceitou um destino existente.");
    });

    Run("Rejeitar imagem truncada", () =>
    {
        string invalid = Path.Combine(
            temp, "imagem_invalida.img");

        File.WriteAllBytes(invalid, new byte[64]);

        bool rejected = false;

        try
        {
            ExFatBootSectorReader.ReadMbrImage(invalid);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Check(rejected,
            "Imagem incompleta não foi rejeitada.");
    });
}
catch (Exception ex)
{
    failed++;
    Console.WriteLine(
        $"[FAIL] Preparação dos testes: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== FOLDERRESCUE: REGRESSÃO ===");
Console.WriteLine($"Aprovados: {passed}");
Console.WriteLine($"Reprovados: {failed}");
Console.WriteLine($"Total: {passed + failed}");

if (failed == 0)
{
    Console.WriteLine("TODOS OS TESTES PASSARAM.");

    try
    {
        Directory.Delete(temp, recursive: true);
    }
    catch (IOException ex)
    {
        Console.WriteLine(
            $"Aviso ao limpar arquivos temporários: {ex.Message}");
    }
}
else
{
    Console.WriteLine(
        $"Arquivos temporários preservados em: {temp}");
    Environment.ExitCode = 1;
}
