using System.Windows.Media.Imaging;
using DesktopOrganizer.App.ViewModels;
using DesktopOrganizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class UiIntegrationTests
{
    [Fact]
    public void NavigationPreservesDraftAndFencePersistence()
    {
        using var directory = new TestDirectory();
        var store = new JsonFenceStore(directory.DirectoryPath, NullLogger<JsonFenceStore>.Instance);
        var vm = new MainWindowViewModel(store, NullLogger<MainWindowViewModel>.Instance);
        vm.CreateCommand.Execute(null);
        var id = vm.SelectedFence!.Id;
        vm.Name = "Edited across navigation";
        vm.OpenSettingsCommand.Execute(null);
        Assert.True(vm.IsSettingsPage);
        Assert.False(vm.IsFencesPage);
        vm.OpenFencesCommand.Execute(null);
        Assert.Equal(id, vm.SelectedFence.Id);
        Assert.Equal("Edited across navigation", vm.Name);
        vm.ApplyCommand.Execute(null);
        Assert.Equal("Edited across navigation", Assert.Single(store.Load().Fences).Name);
    }

    [Theory]
    [InlineData("application.ico")]
    [InlineData("tray.ico")]
    public void BrandingIcoContainsEightDecodableSizes(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DesktopOrganizer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root.FullName, "src", "DesktopOrganizer.App", "Assets", "Branding", file));
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        Assert.Equal(8, reader.ReadUInt16());
        int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];
        for (var i = 0; i < sizes.Length; i++)
        {
            stream.Position = 6 + i * 16;
            var width = reader.ReadByte(); var height = reader.ReadByte();
            Assert.Equal(sizes[i], width == 0 ? 256 : width);
            Assert.Equal(sizes[i], height == 0 ? 256 : height);
            reader.ReadBytes(6);
            var length = reader.ReadUInt32(); var offset = reader.ReadUInt32();
            stream.Position = offset;
            using var png = new MemoryStream(reader.ReadBytes(checked((int)length)));
            var bitmap = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(sizes[i], bitmap.Frames[0].PixelWidth);
            Assert.Equal(sizes[i], bitmap.Frames[0].PixelHeight);
        }
    }
}
