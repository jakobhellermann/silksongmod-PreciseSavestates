using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;

namespace PreciseSavestates.Savestates;

public record SavestateInfo(
    string Path,
    string Slot,
    int? Index,
    string? Name
) {
    public string FullName => $"{Slot}-{Name}";
}

internal class SavestateStore {
    private string? backingDir;

    private string BackingDir =>
        backingDir ??= ModDirs.DataDir(PreciseSavestatesPlugin.Instance, "Savestates");

    private string LayerDir(string? layer) {
        return Path.Join(BackingDir, layer);
    }

    private string SavestatePath(string slot, string? layer) {
        return Path.Join(LayerDir(layer), $"{slot}.json");
    }

    public IEnumerable<SavestateInfo> List(string? slot = null, string? layer = null) {
        var dir = LayerDir(layer);
        if (!Directory.Exists(dir)) {
            return [];
        }

        // Parse the slot out of each file and filter in code, so both `{slot}.json` and `{slot}-{name}.json`
        // are matched (a glob like `{slot}-*.json` would miss the name-less form).
        var infos = Directory.GetFiles(dir, "*.json")
            .Select(path => {
                var stem = Path.GetFileNameWithoutExtension(path);
                var (parsedSlot, name) = SplitOnce(stem, '-') ?? (stem, "");
                int? index = int.TryParse(parsedSlot, out var i) ? i : null;
                return new SavestateInfo(path, parsedSlot, index, name);
            });

        return slot != null ? infos.Where(info => info.Slot == slot) : infos;
    }

    public void Delete(string? slot = null, string? layer = null) {
        foreach (var info in List(slot, layer)) {
            File.Delete(info.Path);
        }
    }

    public void Save(string name, Savestate savestate, string slot, string? layer = null) {
        Delete(slot, layer);
        var fullName = $"{slot}-{name}";

        Directory.CreateDirectory(LayerDir(layer));
        var path = SavestatePath(fullName, layer);
        try {
            using var file = File.CreateText(path);
            savestate.SerializeTo(file);
        } catch (Exception) {
            File.Delete(path);
            throw;
        }
    }

    public static bool TryGetValue(SavestateInfo info, [NotNullWhen(true)] out Savestate? savestate) {
        return TryGetValueInner(info.Path, out savestate);
    }

    public bool TryGetValue(string fullName, [NotNullWhen(true)] out Savestate? savestate, string? layer = null) {
        var path = SavestatePath(fullName, layer);
        return TryGetValueInner(path, out savestate);
    }

    private static bool TryGetValueInner(string path, [NotNullWhen(true)] out Savestate? savestate) {
        try {
            using var reader = File.OpenText(path);
            savestate = Savestate.DeserializeFrom(reader);
            return true;
        } catch (FileNotFoundException) {
            savestate = null;
            return false;
        }
    }

    private static (string, string)? SplitOnce(string str, char sep) {
        var length = str.IndexOf(sep);
        if (length == -1) {
            return null;
        }

        var startIndex = length + 1;
        var str3 = str.Substring(startIndex, str.Length - startIndex);
        return (str[..length], str3);
    }
}
