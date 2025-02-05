﻿using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Resources;
using ADB_Explorer.ViewModels;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Explorer.Services.AppInfra;

internal static class FileActionLogic
{
    public static async void UninstallPackages()
    {
        var pkgs = Data.SelectedPackages;
        var files = Data.SelectedFiles;

        var result = await DialogService.ShowConfirmation(
            Strings.S_REM_APK(Data.FileActions.IsAppDrive ? pkgs : files),
            Strings.S_CONF_UNI_TITLE,
            "Uninstall",
            icon: DialogService.DialogIcon.Exclamation);

        if (result.Item1 is not ContentDialogResult.Primary)
            return;

        var packageTask = await Task.Run(() =>
        {
            if (Data.FileActions.IsAppDrive)
                return pkgs.Select(pkg => pkg.Name);

            return files.Select(item => ShellFileOperation.GetPackageName(Data.CurrentADBDevice, item.FullPath));
        });

        ShellFileOperation.UninstallPackages(Data.CurrentADBDevice, packageTask, App.Current.Dispatcher);
    }

    public static void InstallPackages()
    {
        var packages = Data.SelectedFiles;

        ShellFileOperation.InstallPackages(Data.CurrentADBDevice, packages, App.Current.Dispatcher);
    }

    public static void CopyToTemp()
    {
        _ = ShellFileOperation.MoveItems(FileClass.CutType.Copy,
                                         AdbExplorerConst.TEMP_PATH,
                                         Data.SelectedFiles,
                                         FileHelper.DisplayName(Data.SelectedFiles.First()),
                                         Data.DirList.FileList,
                                         App.Current.Dispatcher,
                                         Data.CurrentADBDevice,
                                         Data.CurrentPath);
    }

    public static void PushPackages()
    {
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = false,
            Multiselect = true,
            DefaultDirectory = Data.Settings.DefaultFolder,
            Title = Strings.S_INSTALL_APK,
        };
        dialog.Filters.Add(new("Android Package", string.Join(';', AdbExplorerConst.INSTALL_APK.Select(name => name[1..]))));

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        ShellFileOperation.PushPackages(Data.CurrentADBDevice, dialog.FilesAsShellObject, App.Current.Dispatcher);
    }

    public static void UpdateModifiedDates()
    {
        ShellFileOperation.ChangeDateFromName(Data.CurrentADBDevice, Data.SelectedFiles, App.Current.Dispatcher);
    }

    public static void OpenEditor()
    {
        if (!Data.FileActions.EditFileEnabled || Data.FileActions.IsEditorOpen && Data.FileActions.EditorAndroidPath.Equals(Data.SelectedFiles.First()))
        {
            Data.FileActions.IsEditorOpen = false;
            return;
        }
        Data.FileActions.IsEditorOpen = true;

        Data.FileActions.EditorAndroidPath = Data.SelectedFiles.First();
        
        var readTask = Task.Run(() =>
        {
            try
            {
                Data.FileActions.EditorWindowsPath = Path.GetTempFileName();
                if (AdbHelper.SilentPull(Data.CurrentADBDevice, Data.FileActions.EditorAndroidPath, Data.FileActions.EditorWindowsPath))
                    return File.ReadAllText(Data.FileActions.EditorWindowsPath);
                else
                    throw new Exception(Strings.S_READ_FILE_ERROR);
            }
            catch (Exception e)
            {
                App.Current.Dispatcher.Invoke(() =>
                    DialogService.ShowMessage(e.Message, Strings.S_READ_FILE_ERROR_TITLE, DialogService.DialogIcon.Exclamation, copyToClipboard: true));

                return "";
            }
        });

        readTask.ContinueWith((t) => App.Current.Dispatcher.Invoke(() =>
        {
            Data.FileActions.EditorText =
            Data.FileActions.OriginalEditorText = t.Result;
        }));
    }

    public static void SaveEditorText()
    {
        string text = Data.FileActions.EditorText;

        var writeTask = Task.Run(() =>
        {
            try
            {
                File.WriteAllText(Data.FileActions.EditorWindowsPath, Data.FileActions.EditorText);

                if (AdbHelper.SilentPush(Data.CurrentADBDevice, Data.FileActions.EditorWindowsPath, Data.FileActions.EditorAndroidPath.FullPath))
                    return true;
                else
                    throw new Exception(Strings.S_WRITE_FILE_ERROR);
            }
            catch (Exception e)
            {
                App.Current.Dispatcher.Invoke(() =>
                    DialogService.ShowMessage(e.Message, Strings.S_WRITE_FILE_ERROR_TITLE, DialogService.DialogIcon.Exclamation, copyToClipboard: true));

                return false;
            }
        });

        writeTask.ContinueWith((t) => App.Current.Dispatcher.Invoke(() =>
        {
            if (!t.Result)
                return;

            Data.FileActions.OriginalEditorText = Data.FileActions.EditorText;

            if (Data.FileActions.EditorAndroidPath.ParentPath == Data.CurrentPath)
                Data.RuntimeSettings.Refresh = true;
        }));
    }

    public static void RestoreItems()
    {
        var restoreItems = (!Data.SelectedFiles.Any() ? Data.DirList.FileList : Data.SelectedFiles).Where(file => file.TrashIndex is not null && !string.IsNullOrEmpty(file.TrashIndex.OriginalPath));
        string[] existingItems = Array.Empty<string>();
        List<FileClass> existingFiles = new();
        bool merge = false;

        var restoreTask = Task.Run(() =>
        {
            existingItems = ADBService.FindFiles(Data.CurrentADBDevice.ID, restoreItems.Select(file => file.TrashIndex.OriginalPath));
            if (existingItems?.Length > 0)
            {
                if (restoreItems.Any(item => item.IsDirectory && existingItems.Contains(item.TrashIndex.OriginalPath)))
                    merge = true;

                existingItems = existingItems.Select(path => path[(path.LastIndexOf('/') + 1)..]).ToArray();
            }

            foreach (var item in restoreItems)
            {
                if (existingItems.Contains(item.FullName))
                    return;

                if (restoreItems.Count(file => file.FullName == item.FullName && file.TrashIndex.OriginalPath == item.TrashIndex.OriginalPath) > 1)
                {
                    existingItems = existingItems.Append(item.FullName).ToArray();
                    existingFiles.Add(item);
                    if (item.IsDirectory)
                        merge = true;
                }
            }
        });

        restoreTask.ContinueWith((t) =>
        {
            App.Current.Dispatcher.BeginInvoke(async () =>
            {
                if (existingItems.Length is int count and > 0)
                {
                    var result = await DialogService.ShowConfirmation(
                        Strings.S_CONFLICT_ITEMS(count),
                        Strings.S_RESTORE_CONF_TITLE,
                        primaryText: Strings.S_MERGE_REPLACE(merge),
                        secondaryText: count == restoreItems.Count() ? "" : "Skip",
                        cancelText: "Cancel",
                        icon: DialogService.DialogIcon.Exclamation);

                    if (result.Item1 is ContentDialogResult.None)
                    {
                        return;
                    }

                    if (result.Item1 is ContentDialogResult.Secondary)
                    {
                        restoreItems = existingFiles.Count != count
                            ? restoreItems.Where(item => !existingItems.Contains(item.FullName))
                            : restoreItems.Except(existingFiles);
                    }
                }

                ShellFileOperation.MoveItems(device: Data.CurrentADBDevice,
                                         items: restoreItems,
                                         targetPath: null,
                                         currentPath: Data.CurrentPath,
                                         fileList: Data.DirList.FileList,
                                         dispatcher: App.Current.Dispatcher);

                var remainingItems = Data.DirList.FileList.Except(restoreItems);
                TrashHelper.EnableRecycleButtons(remainingItems);

                // Clear all remaining files if none of them are indexed
                if (!remainingItems.Any(item => item.TrashIndex is not null))
                {
                    _ = Task.Run(() => ShellFileOperation.SilentDelete(Data.CurrentADBDevice, remainingItems));
                }

                if (!Data.SelectedFiles.Any())
                    TrashHelper.EnableRecycleButtons();
            });
        });
    }

    public static void CopyItemPath()
    {
        var path = Data.FileActions.IsAppDrive ? Data.SelectedPackages.First().Name : Data.SelectedFiles.First().FullPath;
        Clipboard.SetText(path);
    }

    public static void CreateNewItem(FileClass file, string newName = null)
    {
        if (!string.IsNullOrEmpty(newName))
            file.UpdatePath($"{Data.CurrentPath}{(Data.CurrentPath == "/" ? "" : "/")}{newName}");

        if (Data.Settings.ShowExtensions)
            file.UpdateType();

        try
        {
            if (file.Type is FileType.Folder)
                ShellFileOperation.MakeDir(Data.CurrentADBDevice, file.FullPath);
            else if (file.Type is FileType.File)
                ShellFileOperation.MakeFile(Data.CurrentADBDevice, file.FullPath);
            else
                throw new NotSupportedException();
        }
        catch (Exception e)
        {
            DialogService.ShowMessage(e.Message, Strings.S_CREATE_ERR_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
            Data.DirList.FileList.Remove(file);
            throw;
        }

        file.IsTemp = false;
        file.ModifiedTime = DateTime.Now;
        if (file.Type is FileType.File)
            file.Size = 0;

        var index = Data.DirList.FileList.IndexOf(file);
        Data.DirList.FileList.Remove(file);
        Data.DirList.FileList.Insert(index, file);
        Data.FileActions.ItemToSelect = file;
    }

    public static bool IsPasteEnabled(bool isKeyboard = false)
    {
        // Explorer view but not trash or app drive
        if (Data.FileActions.IsPasteStateVisible)
        {
            Data.FileActions.CutItemsCount.Value = Data.CutItems.Count > 0 ? Data.CutItems.Count.ToString() : "";
            Data.FileActions.IsCutState.Value = Data.FileActions.PasteState is FileClass.CutType.Cut;
            Data.FileActions.IsCopyState.Value = Data.FileActions.PasteState is FileClass.CutType.Copy;
        }
        else
        {
            Data.FileActions.CutItemsCount.Value = "";
            Data.FileActions.IsCopyState.Value = false;
            Data.FileActions.IsCutState.Value = false;

            return false;
        }

        Data.FileActions.PasteDescription.Value = $"Paste {Data.CutItems.Count} {FileClass.CutTypeString(Data.FileActions.PasteState)} {PluralityConverter.Convert(Data.CutItems, "Item")}";

        if (Data.CutItems.Count < 1)
            return false;

        if (Data.CutItems.Count == 1 && Data.CutItems[0].Relation(Data.CurrentPath) is RelationType.Descendant or RelationType.Self)
            return false;

        var selected = isKeyboard & Data.SelectedFiles?.Count() > 1 ? 0 : Data.SelectedFiles?.Count();
        switch (selected)
        {
            case 0:
                return !(Data.CutItems[0].ParentPath == Data.CurrentPath && Data.FileActions.PasteState is FileClass.CutType.Cut);
            case 1:
                if (isKeyboard && Data.FileActions.PasteState is FileClass.CutType.Copy && Data.CutItems[0].RelationFrom(Data.SelectedFiles.First()) is RelationType.Self)
                    return true;

                var item = Data.SelectedFiles.First();
                if (!item.IsDirectory
                    || (Data.CutItems.Count == 1 && Data.CutItems[0].FullPath == item.FullPath)
                    || (Data.CutItems[0].ParentPath == item.FullPath))
                    return false;
                break;
            default:
                return false;
        }

        return true;
    }

    public static void UpdateClipboardDropItems()
    {
        FileHelper.ClearCutFiles(i => i.PathType is FilePathType.Windows);

        if (!Clipboard.ContainsFileDropList())
        {
            if (!Data.CutItems.Any())
                Data.FileActions.PasteState = FileClass.CutType.None;

            return;
        }

        int newItems = 0;
        foreach (var item in Clipboard.GetFileDropList())
        {
            if (item is null)
                continue;

            var windowsPath = ShellObject.FromParsingName(item);
            Data.CutItems.Add(new(windowsPath)
            {
                ShellObject = windowsPath,
                CutState = FileClass.CutType.Copy
            });
            newItems++;
        }

        if (newItems == 0)
            return;

        FileHelper.ClearCutFiles(i => i.PathType is FilePathType.Android);

        Data.FileActions.PasteState = FileClass.CutType.Copy;
        UpdateFileActions();
    }

    public static async void PasteFiles(bool isLink = false)
    {
        var firstSelectedFile = Data.SelectedFiles.Any() ? Data.SelectedFiles.First() : null;
        string targetName, targetPath = "";

        if (Data.SelectedFiles.Count() != 1 || (firstSelectedFile is not null && !firstSelectedFile.IsDirectory))
        {
            targetPath = Data.CurrentPath;
            targetName = FileHelper.GetFullName(Data.CurrentPath);
        }
        else
        {
            targetPath = Data.SelectedFiles.First().FullPath;
            targetName = FileHelper.DisplayName(Data.SelectedFiles.First());
        }

        if (Data.CutItems.First().PathType is FilePathType.Windows)
        {
            PushShellObjects(Data.CutItems.Where(f => !f.IsLink).Select(f => f.ShellObject), targetPath);
        }
        else
        {
            var pasteItems = isLink
                ? Data.CutItems
                : Data.CutItems.Where(f => f.Relation(targetPath) is not (RelationType.Self or RelationType.Descendant));

            await ShellFileOperation.MoveItems(isLink ? FileClass.CutType.Link : Data.FileActions.PasteState,
                                               targetPath,
                                               pasteItems,
                                               targetName,
                                               Data.DirList.FileList,
                                               App.Current.Dispatcher,
                                               Data.CurrentADBDevice,
                                               Data.CurrentPath);

            if (Data.FileActions.PasteState is FileClass.CutType.Cut)
                FileHelper.ClearCutFiles(pasteItems);
        }
        
        Data.FileActions.PasteEnabled = IsPasteEnabled();
    }

    public static void CutFiles(IEnumerable<FileClass> items, bool isCopy = false)
    {
        FileHelper.ClearCutFiles();
        Data.FileActions.PasteState = isCopy ? FileClass.CutType.Copy : FileClass.CutType.Cut;

        var itemsToCut = Data.DevicesObject.Current.Root is not AbstractDevice.RootStatus.Enabled
                    ? items.Where(file => file.Type is FileType.File or FileType.Folder) : items;

        foreach (var item in itemsToCut)
        {
            item.CutState = Data.FileActions.PasteState;
        }

        Data.CutItems.AddRange(itemsToCut);

        Data.FileActions.CopyEnabled = !isCopy;
        Data.FileActions.CutEnabled = isCopy;

        Data.FileActions.PasteEnabled = IsPasteEnabled();
        Data.FileActions.IsKeyboardPasteEnabled = IsPasteEnabled(true);

        if (isCopy)
        {
            var vfdo = VirtualFileDataObject.PrepareTransfer(Data.CutItems);
            if (vfdo is null)
                return;

            VirtualFileDataObject.SendObjectToSystem(vfdo, VirtualFileDataObject.SendMethod.Clipboard);
        }
    }

    public static void Rename(TextBox textBox)
    {
        FileClass file = TextHelper.GetAltObject(textBox) as FileClass;
        var name = FileHelper.DisplayName(textBox);
        if (file.IsTemp)
        {
            if (string.IsNullOrEmpty(textBox.Text))
            {
                Data.DirList.FileList.Remove(file);
                return;
            }
            try
            {
                CreateNewItem(file, textBox.Text);
            }
            catch (Exception e)
            {
                if (e is NotImplementedException)
                    throw;
            }
        }
        else if (!string.IsNullOrEmpty(textBox.Text) && textBox.Text != name)
        {
            try
            {
                FileHelper.RenameFile(file, textBox.Text);
            }
            catch (Exception)
            { }
        }
    }

    public static async void DeleteFiles()
    {
        IEnumerable<FileClass> itemsToDelete;
        if (Data.FileActions.IsRecycleBin && !Data.SelectedFiles.Any())
        {
            itemsToDelete = Data.DirList.FileList.Where(f => f.Extension != AdbExplorerConst.RECYCLE_INDEX_SUFFIX);
        }
        else
        {
            itemsToDelete = Data.DevicesObject.Current.Root != AbstractDevice.RootStatus.Enabled
                    ? Data.SelectedFiles.Where(file => file.Type is FileType.File or FileType.Folder) : Data.SelectedFiles;
        }

        string deletedString;
        if (itemsToDelete.Count() == 1)
            deletedString = FileHelper.DisplayName(itemsToDelete.First());
        else
        {
            deletedString = $"{itemsToDelete.Count()} ";
            if (itemsToDelete.All(item => item.IsDirectory))
                deletedString += "folders";
            else if (itemsToDelete.All(item => !item.IsDirectory))
                deletedString += "files";
            else
                deletedString += "items";
        }

        var result = await DialogService.ShowConfirmation(
            Strings.S_DELETE_CONF(Data.FileActions.IsRecycleBin, deletedString),
            Strings.S_DEL_CONF_TITLE,
            Strings.S_DELETE_ACTION,
            checkBoxText: Data.Settings.EnableRecycle && !Data.FileActions.IsRecycleBin ? Strings.S_PERM_DEL : "",
            icon: DialogService.DialogIcon.Delete);

        if (result.Item1 is not ContentDialogResult.Primary)
            return;

        if (!Data.FileActions.IsRecycleBin && Data.Settings.EnableRecycle && !result.Item2)
        {
            await Task.Run(() => ShellFileOperation.MakeDir(Data.CurrentADBDevice, AdbExplorerConst.RECYCLE_PATH));

            ShellFileOperation.MoveItems(Data.CurrentADBDevice,
                                         itemsToDelete,
                                         AdbExplorerConst.RECYCLE_PATH,
                                         Data.CurrentPath,
                                         Data.DirList.FileList,
                                         App.Current.Dispatcher);
        }
        else
        {
            ShellFileOperation.DeleteItems(Data.CurrentADBDevice, itemsToDelete, App.Current.Dispatcher);

            if (Data.FileActions.IsRecycleBin)
            {
                var remainingItems = Data.DirList.FileList.Except(itemsToDelete);
                TrashHelper.EnableRecycleButtons(remainingItems);

                // Clear all remaining files if none of them are indexed
                if (!remainingItems.Any(item => item.TrashIndex is not null))
                {
                    _ = Task.Run(() => ShellFileOperation.SilentDelete(Data.CurrentADBDevice, remainingItems));
                }
            }
        }
    }

    public static void RemoveFile(FileClass file)
    {
        file.CutState = FileClass.CutType.None;

        Data.CutItems.RemoveAll(cutItem => cutItem.RelationFrom(file) is RelationType.Ancestor or RelationType.Self);

        if (Data.CutItems.Count == 0)
            Data.FileActions.PasteState = FileClass.CutType.None;

        file.TrashIndex = null;
    }

    public static void RefreshDrives(bool asyncClassify = false)
    {
        if (Data.DevicesObject.Current is null)
            return;

        if (!asyncClassify && Data.DevicesObject.Current.Drives?.Count > 0 && !Data.FileActions.IsExplorerVisible)
            asyncClassify = true;

        var driveTask = Task.Run(() =>
        {
            if (Data.CurrentADBDevice is null)
                return null;

            var drives = Data.CurrentADBDevice.GetDrives();

            if (Data.DevicesObject.Current.Type is AbstractDevice.DeviceType.Recovery)
            {
                foreach (var item in Data.DevicesObject.Current.Drives.OfType<VirtualDriveViewModel>())
                {
                    item.SetItemsCount(item.Type is AbstractDrive.DriveType.Package ? -1 : null);
                }
            }
            else
            {
                if (Data.DevicesObject.Current.Drives.Any(d => d.Type is AbstractDrive.DriveType.Trash))
                    TrashHelper.UpdateRecycledItemsCount();

                if (Data.DevicesObject.Current.Drives.Any(d => d.Type is AbstractDrive.DriveType.Temp))
                    UpdateInstallersCount();

                if (Data.DevicesObject.Current.Drives.Any(d => d.Type is AbstractDrive.DriveType.Package))
                    UpdatePackagesCount();
            }

            return drives;
        });
        driveTask.ContinueWith((t) =>
        {
            if (t.IsCanceled || t.Result is null)
                return;

            App.Current.Dispatcher.Invoke(async () =>
            {
                if (await Data.DevicesObject.Current?.UpdateDrives(await t, App.Current.Dispatcher, asyncClassify))
                {
                    Data.RuntimeSettings.FilterDrives = true;
                    FolderHelper.CombineDisplayNames();
                }
            });
        });
    }

    public static void UpdateInstallersCount()
    {
        var countTask = Task.Run(() => ADBService.CountPackages(Data.DevicesObject.Current.ID));
        countTask.ContinueWith((t) => App.Current?.Dispatcher.Invoke(() =>
        {
            if (!t.IsCanceled && Data.DevicesObject.Current is not null)
            {
                var temp = Data.DevicesObject.Current.Drives.Find(d => d.Type is AbstractDrive.DriveType.Temp);
                ((VirtualDriveViewModel)temp)?.SetItemsCount((long)t.Result);
            }
        }));
    }

    public static void UpdatePackagesCount()
    {
        var packageTask = Task.Run(() => ShellFileOperation.GetPackagesCount(Data.CurrentADBDevice));

        packageTask.ContinueWith((t) =>
        {
            if (t.IsCanceled || t.Result is null || Data.DevicesObject.Current is null)
                return;

            App.Current.Dispatcher.Invoke(() =>
            {
                var package = Data.DevicesObject.Current.Drives.Find(d => d.Type is AbstractDrive.DriveType.Package);
                ((VirtualDriveViewModel)package)?.SetItemsCount((int?)t.Result);
            });
        });
    }

    public static void UpdatePackages(bool updateExplorer = false)
    {
        Data.FileActions.ListingInProgress = true;

        var version = Data.DevicesObject.Current.AndroidVersion;
        var packageTask = Task.Run(() => ShellFileOperation.GetPackages(Data.CurrentADBDevice, Data.Settings.ShowSystemPackages, version is not null && version >= AdbExplorerConst.MIN_PKG_UID_ANDROID_VER));

        packageTask.ContinueWith((t) =>
        {
            if (t.IsCanceled)
                return;

            App.Current.Dispatcher.Invoke(() =>
            {
                Data.Packages = t.Result;
                if (updateExplorer)
                    Data.RuntimeSettings.ExplorerSource = Data.Packages;

                if (!updateExplorer && Data.DevicesObject.Current is not null)
                {
                    var package = Data.DevicesObject.Current.Drives.Find(d => d.Type is AbstractDrive.DriveType.Package);
                    ((VirtualDriveViewModel)package)?.SetItemsCount(Data.Packages.Count);
                }

                Data.FileActions.ListingInProgress = false;
            });
        });
    }

    public static void ClearExplorer(bool clearDevice = true)
    {
        Data.DirList?.FileList?.Clear();
        Data.Packages.Clear();
        Data.FileActions.PushFilesFoldersEnabled =
        Data.FileActions.PullEnabled =
        Data.FileActions.DeleteEnabled =
        Data.FileActions.RenameEnabled =
        Data.FileActions.HomeEnabled =
        Data.FileActions.NewEnabled =
        Data.FileActions.PasteEnabled =
        Data.FileActions.IsUninstallVisible.Value =
        Data.FileActions.CutEnabled =
        Data.FileActions.CopyEnabled =
        Data.FileActions.IsExplorerVisible =
        Data.FileActions.PackageActionsEnabled =
        Data.FileActions.IsCopyItemPathEnabled =
        Data.FileActions.UpdateModifiedEnabled =
        Data.FileActions.IsFollowLinkEnabled =
        Data.RuntimeSettings.IsExplorerLoaded =
        Data.FileActions.ParentEnabled = false;

        Data.FileActions.ExplorerFilter = "";

        if (clearDevice)
        {
            Data.CurrentDisplayNames.Clear();
            Data.CurrentPath = null;
            Data.RuntimeSettings.CurrentDevice = null;
            Data.RuntimeSettings.ClearNavBox = true;

            UpdateFileActions();
        }

        Data.RuntimeSettings.FilterActions = true;
    }

    public static void UpdateFileActions()
    {
        Data.FileActions.IsApkActionsVisible.Value = Data.Settings.EnableApk && Data.DevicesObject?.Current;
        Data.FileActions.PushPackageEnabled = Data.FileActions.IsApkActionsVisible && Data.DevicesObject.Current.Type is not AbstractDevice.DeviceType.Recovery;

        Data.FileActions.UninstallPackageEnabled = Data.FileActions.IsAppDrive && Data.SelectedPackages.Any();
        Data.FileActions.ContextPushPackagesEnabled = Data.FileActions.IsAppDrive && !Data.SelectedPackages.Any();

        Data.FileActions.IsRefreshEnabled = Data.FileActions.IsDriveViewVisible || Data.FileActions.IsExplorerVisible;
        Data.FileActions.IsCopyCurrentPathEnabled = Data.FileActions.IsExplorerVisible && !Data.FileActions.IsRecycleBin && !Data.FileActions.IsAppDrive;

        Data.FileActions.IsApkWebSearchEnabled = Data.FileActions.IsAppDrive && Data.SelectedPackages.Count() == 1 && !string.IsNullOrEmpty(Data.RuntimeSettings.DefaultBrowserPath);

        if (Data.FileActions.IsAppDrive)
        {
            Data.FileActions.IsCopyItemPathEnabled = Data.SelectedPackages.Count() == 1;

            Data.RuntimeSettings.FilterActions = true;
            return;
        }

        Data.FileActions.IsRegularItem = Data.RuntimeSettings.IsRootActive
            || Data.SelectedFiles.AnyAll(item => item.Type is FileType.File or FileType.Folder);

        if (Data.FileActions.IsRecycleBin)
        {
            TrashHelper.EnableRecycleButtons(Data.SelectedFiles.Any() ? Data.SelectedFiles : Data.DirList.FileList);
        }
        else
        {
            Data.FileActions.DeleteEnabled = Data.SelectedFiles.Any() && Data.FileActions.IsRegularItem
                && (!Data.FileActions.IsFollowLinkEnabled || Data.RuntimeSettings.IsRootActive);

            Data.FileActions.RestoreEnabled = false;
        }

        Data.FileActions.IsFollowLinkEnabled = !Data.FileActions.IsRecycleBin
                                               && Data.SelectedFiles.Count() == 1
                                               && Data.SelectedFiles.First().IsLink
                                               && Data.SelectedFiles.First().Type is not FileType.BrokenLink;

        Data.FileActions.PullDescription.Value = Data.FileActions.IsFollowLinkEnabled ? Strings.S_PULL_ACTION_LINK : Strings.S_PULL_ACTION;
        Data.FileActions.DeleteDescription.Value = Data.FileActions.IsRecycleBin && !Data.SelectedFiles.Any() ? Strings.S_EMPTY_TRASH : Strings.S_DELETE_ACTION;
        Data.FileActions.RestoreDescription.Value = Data.FileActions.IsRecycleBin && !Data.SelectedFiles.Any() ? Strings.S_RESTORE_ALL : Strings.S_RESTORE_ACTION;

        Data.FileActions.PullEnabled = !Data.FileActions.IsRecycleBin
                                       && Data.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                       && Data.FileActions.IsRegularItem;

        Data.FileActions.ContextPushEnabled = !Data.FileActions.IsRecycleBin && (!Data.SelectedFiles.Any() || (Data.SelectedFiles.Count() == 1 && Data.SelectedFiles.First().IsDirectory));

        Data.FileActions.RenameEnabled = !Data.FileActions.IsRecycleBin
                                         && Data.SelectedFiles.Count() == 1
                                         && Data.FileActions.IsRegularItem
                                         && (!Data.FileActions.IsFollowLinkEnabled || Data.RuntimeSettings.IsRootActive);

        var allSelectedAreCut = Data.SelectedFiles.Any() && Data.CutItems.All(item => Data.SelectedFiles.Contains(item)) && Data.CutItems.Count == Data.SelectedFiles.Count();
        Data.FileActions.CutEnabled = Data.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                      && !(allSelectedAreCut && Data.FileActions.PasteState is FileClass.CutType.Cut)
                                      && Data.FileActions.IsRegularItem
                                      && (!Data.FileActions.IsFollowLinkEnabled || Data.RuntimeSettings.IsRootActive);

        Data.FileActions.CopyEnabled = Data.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                       && !(allSelectedAreCut && Data.FileActions.PasteState is FileClass.CutType.Copy)
                                       && Data.FileActions.IsRegularItem
                                       && !Data.FileActions.IsRecycleBin;

        Data.FileActions.PasteEnabled = IsPasteEnabled();
        Data.FileActions.IsKeyboardPasteEnabled = IsPasteEnabled(true);

        Data.FileActions.PackageActionsEnabled = Data.Settings.EnableApk
                                                 && Data.SelectedFiles.AnyAll(file => file.IsInstallApk)
                                                 && !Data.FileActions.IsRecycleBin
                                                 && !(Data.DevicesObject?.Current?.Type is AbstractDevice.DeviceType.Recovery
                                                 && Data.FileActions.IsTemp);

        Data.FileActions.IsCopyItemPathEnabled = Data.SelectedFiles.Count() == 1 && !Data.FileActions.IsRecycleBin;

        Data.FileActions.ContextNewEnabled = !Data.SelectedFiles.Any() && !Data.FileActions.IsRecycleBin;
        Data.FileActions.SubmenuUninstallEnabled = Data.FileActions.IsTemp
            && Data.SelectedFiles.AnyAll(file => file.IsInstallApk)
            && Data.DevicesObject?.Current?.Type is not AbstractDevice.DeviceType.Recovery;

        Data.FileActions.UpdateModifiedEnabled = !Data.FileActions.IsRecycleBin
            && Data.SelectedFiles.AnyAll(file => file.Type is FileType.File && !file.IsApk && !file.IsLink);

        Data.FileActions.EditFileEnabled = !Data.FileActions.IsRecycleBin
            && Data.SelectedFiles.Count() == 1
            && Data.SelectedFiles.First().Type is FileType.File
            && !Data.SelectedFiles.First().IsApk
            && !Data.SelectedFiles.First().IsLink
            && Data.SelectedFiles.First().Size < Data.Settings.EditorMaxFileSize;

        Data.FileActions.IsPasteLinkEnabled = !Data.FileActions.IsRecycleBin
            && Data.RuntimeSettings.IsRootActive
            && Data.CutItems.Count == 1
            && Data.CutItems[0].PathType is FilePathType.Android
            && Data.FileActions.PasteState is FileClass.CutType.Copy
            && (!Data.SelectedFiles.Any() ||
            (Data.SelectedFiles.Count() == 1 && Data.SelectedFiles.First().IsDirectory));

        Data.RuntimeSettings.FilterActions = true;
    }

    public static void PushItems(bool isFolderPicker, bool isContextMenu)
    {
        Data.RuntimeSettings.IsPathBoxFocused = false;

        SyncFile targetPath;
        if (isContextMenu && Data.SelectedFiles.Count() == 1)
            targetPath = (SyncFile)Data.SelectedFiles.First();
        else
            targetPath = new(Data.CurrentPath, FileType.Folder);

        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = isFolderPicker,
            Multiselect = true,
            DefaultDirectory = Data.Settings.DefaultFolder,
            Title = Strings.S_PUSH_BROWSE_TITLE(isFolderPicker, targetPath.FullPath == Data.CurrentPath ? "" : targetPath.FullName),
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        PushShellObjects(dialog.FilesAsShellObject, targetPath.FullPath);
    }

    public static void PushShellObjects(IEnumerable<ShellObject> items, string targetPath)
    {
        foreach (var item in items)
        {
            var source = new SyncFile(item) { ShellObject = item };
            var target = new SyncFile(FileHelper.ConcatPaths(targetPath, source.FullName), source.IsDirectory ? FileType.Folder : FileType.File);
            
            var pushOpeartion = FileSyncOperation.PushFile(source, target, Data.CurrentADBDevice, App.Current.Dispatcher);
            pushOpeartion.PropertyChanged += PushOpeartion_PropertyChanged;
            Data.FileOpQ.AddOperation(pushOpeartion);
        }
    }

    private static void PushOpeartion_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        var op = sender as FileSyncOperation;

        // If operation completed now and current path is where the new file was pushed to and it is not shown yet
        if (e.PropertyName is nameof(FileOperation.Status)
            && op.Status is FileOperation.OperationStatus.Completed)
        {
            if (op.Device.ID == Data.CurrentADBDevice.ID
                && op.TargetPath.ParentPath == Data.CurrentPath
                && !Data.DirList.FileList.Any(f => f.FullName == op.FilePath.FullName))
            {
                Data.DirList.FileList.Add(FileClass.FromWindowsPath(op.TargetPath, op.FilePath.ShellObject));
            }

            op.FilePath.ShellObject = null;
            op.PropertyChanged -= PushOpeartion_PropertyChanged;
        }
    }

    public static void PullFiles(string targetPath = "")
    {
        Data.RuntimeSettings.IsPathBoxFocused = false;

        int itemsCount = Data.SelectedFiles.Count();
        ShellObject path;

        if (!string.IsNullOrEmpty(targetPath))
        {
            path = ShellObject.FromParsingName(targetPath);
        }
        else
        {
            var dialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = true,
                Multiselect = false,
                DefaultDirectory = Data.Settings.DefaultFolder,
                Title = Strings.S_ITEMS_DESTINATION(itemsCount > 1, Data.SelectedFiles.First()),
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                return;

            path = dialog.FileAsShellObject;
        }

        if (!Directory.Exists(path.ParsingName))
        {
            try
            {
                Directory.CreateDirectory(path.ParsingName);
            }
            catch (Exception e)
            {
                DialogService.ShowMessage(e.Message, Strings.S_DEST_ERR, DialogService.DialogIcon.Critical, copyToClipboard: true);
                return;
            }
        }

        foreach (var item in Data.SelectedFiles)
        {
            SyncFile target = new(path);
            target.UpdatePath(FileHelper.ConcatPaths(target, item.FullName));

            Data.FileOpQ.AddOperation(FileSyncOperation.PullFile(new(item), target, Data.CurrentADBDevice, App.Current.Dispatcher));
        }
    }

    public static void ToggleFileOpQ()
    {
        Data.FileOpQ.IsAutoPlayOn ^= true;

        if (Data.FileOpQ.IsAutoPlayOn)
            Data.FileOpQ.Start();
        else
            Data.FileOpQ.Stop();

        Data.RuntimeSettings.RefreshFileOpControls = true;
    }

    private static Mutex FileOpControlsMutex = new();

    public static void UpdateFileOpControls()
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            FileOpControlsMutex.WaitOne(0);

            var changed = false;

            var removeAction = PluralityConverter.Convert(Data.FileActions.SelectedFileOps, "Remove Operation");
            if (Data.FileActions.RemoveFileOpDescription.Value != removeAction)
            {
                Data.FileActions.RemoveFileOpDescription.Value = removeAction;
                changed = true;
            }

            var validateAction = PluralityConverter.Convert(Data.FileActions.SelectedFileOps, "Validate Operation");
            if (Data.FileActions.ValidateDescription.Value != validateAction)
            {
                Data.FileActions.ValidateDescription.Value = validateAction;
                changed = true;
            }

            if (changed)
                Data.RuntimeSettings.RefreshFileOpControls = true;

            FileOpControlsMutex.ReleaseMutex();
        });
    }

    public static async void ResetAppSettings()
    {
        var result = await DialogService.ShowConfirmation(
                        Strings.S_RESET_SETTINGS,
                        Strings.S_RESET_SETTINGS_TITLE,
                        primaryText: "Confirm",
                        cancelText: "Cancel",
                        icon: DialogService.DialogIcon.Exclamation);

        if (result.Item1 == ContentDialogResult.None)
            return;

        Data.RuntimeSettings.ResetAppSettings = true;
    }

    public static void ToggleSettingsSort()
    {
        Data.RuntimeSettings.SortedView ^= true;
        Data.FileActions.IsExpandSettingsVisible.Value ^= true;

        Data.RuntimeSettings.RefreshSettingsControls = true;
    }

    public static void ToggleSettingsExpand()
    {
        Data.RuntimeSettings.GroupsExpanded ^= true;

        Data.RuntimeSettings.RefreshSettingsControls = true;
    }

    public static async void FollowLink()
    {
        var target = ADBService.ReadLink(Data.CurrentADBDevice.ID, Data.SelectedFiles.First().FullPath);

        if (string.IsNullOrEmpty(target))
            return;

        if (FileHelper.GetParentPath(target) != Data.CurrentPath)
        {
            Data.RuntimeSettings.LocationToNavigate = target + "/..";
        }

        await AsyncHelper.WaitUntil(() => !Data.DirList.InProgress, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(20), new());

        var file = Data.DirList.FileList.FirstOrDefault(f => f.FullPath == target);
        if (file is not null)
            Data.FileActions.ItemToSelect = file;
    }

    public static void ApkWebSearch()
    {
        var apk = Data.SelectedPackages.First();
        
        Process.Start(Data.RuntimeSettings.DefaultBrowserPath, $"\"? {apk.Name}\"");
    }
}
