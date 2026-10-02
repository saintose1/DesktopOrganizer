using System.Text.Json;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Storage;

/// <summary>Written before parking; a crash or a deleted Fence cannot strand a real Shell item without a return position.</summary>
public sealed class JsonIconRecoveryStore(string directory, ILogger<JsonIconRecoveryStore> logger) : IIconRecoveryStore
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "parked-icons.json");
    public IReadOnlyList<DesktopIconTarget> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return Validate(JsonSerializer.Deserialize<DesktopIconTarget[]>(File.ReadAllText(_path)) ?? throw new InvalidDataException("Empty recovery journal."));
        }
        catch (Exception error) { logger.LogError(error, "Cannot read icon recovery journal; parking is disabled until repaired"); throw; }
    }
    public void Save(IReadOnlyList<DesktopIconTarget> targets)
    {
        Validate(targets);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, targets); file.Flush(flushToDisk: true); }
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) { logger.LogError(error, "Cannot write icon recovery journal; no new icons will be parked"); throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static IReadOnlyList<DesktopIconTarget> Validate(IReadOnlyList<DesktopIconTarget> targets)
    {
        if (targets.Count > 100000 || targets.Any(t => string.IsNullOrWhiteSpace(t.StableId) || t.StableId.Length > 32768 ||
                !double.IsFinite(t.Position.X) || !double.IsFinite(t.Position.Y)) ||
            targets.Select(t => t.StableId).Distinct(FenceIconLayout.IdentityComparer).Count() != targets.Count)
            throw new InvalidDataException("Invalid icon recovery journal.");
        return targets;
    }
}
