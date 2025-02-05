﻿using ADB_Explorer.Converters;
using ADB_Explorer.Models;
using ADB_Explorer.Resources;
using ADB_Explorer.Services;
using static ADB_Explorer.Models.AbstractFile;
using static ADB_Explorer.Models.FileClass;

namespace ADB_Explorer.Helpers;

public static class FileHelper
{
    public static FileClass ListerFileManipulator(FileClass item)
    {
        if (Data.CutItems.Count > 0 && (Data.CutItems[0].ParentPath == Data.DirList.CurrentPath))
        {
            var cutItem = Data.CutItems.Where(f => f.FullPath == item.FullPath);
            if (cutItem.Any())
            {
                item.CutState = cutItem.First().CutState;
                Data.CutItems.Remove(cutItem.First());
                Data.CutItems.Add(item);
            }
        }

        if (Data.FileActions.IsRecycleBin)
        {
            var query = Data.RecycleIndex.Where(index => index.RecycleName == item.FullName);
            if (query.Any())
            {
                item.TrashIndex = query.First();
                item.UpdateType();
            }
        }

        return item;
    }

    public static Predicate<object> HideFiles() => file =>
    {
        if (file is not FileClass fileClass)
            return false;

        if (fileClass.IsHidden)
            return false;

        return !IsHiddenRecycleItem(fileClass);
    };

    public static Predicate<object> PkgFilter() => pkg =>
    {
        if (pkg is not Package)
            return false;
        
        return string.IsNullOrEmpty(Data.FileActions.ExplorerFilter)
            || pkg.ToString().Contains(Data.FileActions.ExplorerFilter, StringComparison.OrdinalIgnoreCase);
    };

    public static bool IsHiddenRecycleItem(FileClass file)
    {
        if (AdbExplorerConst.POSSIBLE_RECYCLE_PATHS.Contains(file.FullPath) || file.Extension == AdbExplorerConst.RECYCLE_INDEX_SUFFIX)
            return true;
        
        if (!string.IsNullOrEmpty(Data.FileActions.ExplorerFilter) && !file.ToString().Contains(Data.FileActions.ExplorerFilter, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static void RenameFile(FileClass file, string newName)
    {
        var newPath = ConcatPaths(file.ParentPath, newName);
        if (!Data.Settings.ShowExtensions)
            newPath += file.Extension;

        if (Data.DirList.FileList.Any(file => file.DisplayName == newName))
        {
            DialogService.ShowMessage(Strings.S_PATH_EXIST(newName), Strings.S_RENAME_CONF_TITLE, DialogService.DialogIcon.Exclamation, copyToClipboard: true);
            return;
        }

        ShellFileOperation.Rename(file, newPath, Data.CurrentADBDevice);
    }

    public static string DisplayName(TextBox textBox) => DisplayName(textBox.DataContext as FilePath);

    public static string DisplayName(FilePath file) => Data.Settings.ShowExtensions ? file?.FullName : file?.NoExtName;

    public static FileClass GetFromCell(DataGridCellInfo cell) => CellConverter.GetDataGridCell(cell).DataContext as FileClass;

    public static void ClearCutFiles(Func<FileClass, bool> predicate)
        => ClearCutFiles(Data.CutItems.Where(predicate));

    public static void ClearCutFiles(IEnumerable<FileClass> items = null)
    {
        items ??= Data.CutItems;
        
        foreach (var item in items)
        {
            item.CutState = CutType.None;
        }
        Data.CutItems.RemoveAll(items.ToList());

        Data.FileActions.PasteState = CutType.None;
    }

    public static string ConcatPaths(FilePath path1, string path2) => 
        ConcatPaths(path1.FullPath, path2, path1.PathType is FilePathType.Android ? '/' : '\\');

    public static string ConcatPaths(string path1, string path2, char separator = '/')
    {
        return $"{path1.TrimEnd(Separators)}{separator}{path2.TrimStart(Separators)}";
    }

    public static string ExtractRelativePath(string fullPath, string parent)
    {
        if (fullPath == parent)
            return FilePath.GetFullName(fullPath);

        var index = fullPath.IndexOf(parent);
        
        var result = index < 0
            ? fullPath
            : fullPath[parent.Length..];

        return result.TrimStart('/', '\\');
    }

    public static string GetParentPath(string fullPath) =>
        fullPath[..FilePath.LastSeparatorIndex(fullPath)];

    public static string GetFullName(string fullPath) =>
        fullPath[FilePath.LastSeparatorIndex(fullPath)..];

    public static ulong? GetSize(this ShellObject shellObject)
        => shellObject.Properties.System.Size.Value;

    public static DateTime? GetDateModified(this ShellObject shellObject)
        => shellObject.Properties.System.DateModified.Value;

    public static ulong TotalSize(IEnumerable<FileClass> files)
    {
        if (files.Any(f => f.Type is not FileType.File || f.IsLink))
            return 0;

        return (ulong)files.Select(f => (decimal)f.Size.GetValueOrDefault(0)).Sum();
    }

    public static string ExistingIndexes(ObservableList<FileClass> fileList, string namePrefix, CutType cutType = CutType.None)
    {
        var existingItems = fileList.Where(item => item.NoExtName.StartsWith(namePrefix));
        var suffixes = existingItems.Select(item => item.NoExtName[namePrefix.Length..].Trim());

        if (cutType is CutType.Copy or CutType.Link)
        {
            suffixes = suffixes.Select(item => item.Replace("- Copy", "").Trim());
        }

        return ExistingIndexes(suffixes, cutType);
    }

    public static string ExistingIndexes(IEnumerable<string> suffixes, CutType cutType = CutType.None)
    {
        var copySuffix = cutType is CutType.Copy or CutType.Link ? " - Copy" : "";

        var indexes = (from i in suffixes
                       where int.TryParse(i, out _)
                       select int.Parse(i)).ToList();
        if (suffixes.Any(s => s == ""))
            indexes.Add(0);

        indexes.Sort();
        if (indexes.Count == 0 || indexes[0] != 0)
            return "";

        var result = "";
        for (int i = 0; i < indexes.Count; i++)
        {
            if (indexes[i] > i)
            {
                result = $"{copySuffix} {i}";
                break;
            }
        }
        if (result == "")
            result = $"{copySuffix} {indexes.Count}";

        return result;
    }
}
