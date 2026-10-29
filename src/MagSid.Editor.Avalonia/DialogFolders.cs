using Avalonia.Platform.Storage;
using MagSid.Editor.App;

namespace MagSid.Editor.Avalonia;

/// <summary>Where a file picker starts: the folder <see cref="EditorSettings"/> remembers for its
/// dialog - each dialog its own - or, before it has one, the folder of a file it belongs with.</summary>
internal static class DialogFolders
{
    public static async Task<IStorageFolder?> StartIn(this IStorageProvider storage, DialogFolder dialog, string? besideFile = null)
    {
        string folder = EditorSettings.FolderFor(dialog);
        if (folder.Length == 0 && Path.GetDirectoryName(besideFile ?? "") is { Length: > 0 } own && Directory.Exists(own))
            folder = own;
        return folder.Length > 0 ? await storage.TryGetFolderFromPathAsync(folder) : null;
    }
}
