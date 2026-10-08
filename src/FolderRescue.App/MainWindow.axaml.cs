using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FolderRescue.Core;

namespace FolderRescue.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }



    private async void AnalisarImagem_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Selecionar imagem de disco",
                AllowMultiple = false
            });

        if (files.Count == 0)
            return;

        try
        {
            string path = files[0].Path.LocalPath;

            var result =
                await DiskImageInspector.InspectAsync(path);

            var scan =
                await PartitionTableInspector.InspectAsync(path);

            string details = scan.Partitions.Count == 0
                ? "Nenhuma partição MBR encontrada"
                : string.Join("; ", scan.Partitions.Select(p =>
                    $"P{p.Number}: {p.FileSystem}, " +
                    $"offset {p.OffsetBytes} bytes"));

            StatusTexto.Text =
                $"Imagem: {Path.GetFileName(path)} | " +
                $"Tabela: {scan.Scheme} | {details}";
        }
        catch (Exception ex)
        {
            StatusTexto.Text =
                $"Erro na análise: {ex.Message}";
        }
    }

    private async void AtualizarDispositivos_Click(
        object? sender,
        RoutedEventArgs e)
    {
        AtualizarDispositivosButton.IsEnabled = false;

        try
        {
            var devices = await DeviceDiscovery.ListAsync();

            DispositivosList.ItemsSource = devices;

            StatusTexto.Text =
                $"{devices.Count} disco(s) físico(s) encontrado(s).";
        }
        catch (Exception ex)
        {
            StatusTexto.Text =
                $"Erro ao identificar discos: {ex.Message}";
        }
        finally
        {
            AtualizarDispositivosButton.IsEnabled = true;
        }
    }

    private async void SelecionarOrigem_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var pastas = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Selecionar pasta de teste",
                AllowMultiple = false
            });

        if (pastas.Count == 0)
            return;

        var caminho = pastas[0].Path.LocalPath;

        OrigemTexto.Text = caminho;
        PastasList.ItemsSource = null;

        try
        {
            var nomes = Directory
                .EnumerateDirectories(caminho)
                .Select(p => Path.GetFileName(p))
                .OrderBy(p => p)
                .ToArray();

            PastasList.ItemsSource = nomes;

            StatusTexto.Text =
                $"{nomes.Length} subpastas visíveis encontradas.";
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException)
        {
            StatusTexto.Text =
                $"Não foi possível listar as pastas: {ex.Message}";
        }
    }

    private async void SelecionarDestino_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var pastas = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Selecionar destino",
                AllowMultiple = false
            });

        if (pastas.Count == 0)
            return;

        DestinoTexto.Text = pastas[0].Path.LocalPath;

        StatusTexto.Text =
            "Destino selecionado. Nenhum arquivo foi gravado.";
    }
}
