// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Scada.Lang;
using Scada.Log;
using Scada.Web.Lang;
using Scada.Web.Plugins.PlgMimic;
using Scada.Web.Plugins.PlgMimic.Components;
using Scada.Web.Plugins.PlgMimic.Config;
using Scada.Web.Plugins.PlgMimic.MimicModel;
using Scada.Web.Plugins.PlgMimicEditor.Models;
using Scada.Web.Services;

namespace Scada.Web.Plugins.PlgMimicEditor.Code
{
    /// <summary>
    /// Manages mimic diagrams being edited.
    /// <para>Управляет редактируемыми мнемосхемами.</para>
    /// </summary>
    public class EditorManager
    {
        /// <summary>
        /// The period of cleaning up inactive mimics.
        /// </summary>
        private static readonly TimeSpan CleanupPeriod = TimeSpan.FromMinutes(1);
        /// <summary>
        /// The period for keeping inactive mimics alive.
        /// </summary>
        private static readonly TimeSpan KeepInactiveMimics = TimeSpan.FromMinutes(10);

        private readonly IWebContext webContext; // the web application context
        private readonly object editorLock;      // synchronizes access to the open mimics
        private readonly Dictionary<string, MimicGroup> mimicGroups;        // the mimic groups by project file name
        private readonly Dictionary<string, MimicInstance> mimicByFileName; // the mimics accessible by file name
        private readonly Dictionary<long, MimicInstance> mimicByKey;        // the mimics accessible by editor key

        private Thread cleanupThread;     // the thread to cleanup inactive mimics
        private volatile bool terminated; // necessary to stop the thread


        /// <summary>
        /// Initializes a new instance of the class.
        /// </summary>
        public EditorManager(IWebContext webContext)
        {
            this.webContext = webContext ?? throw new ArgumentNullException(nameof(webContext));
            editorLock = new object();
            mimicGroups = [];
            mimicByFileName = [];
            mimicByKey = [];

            cleanupThread = null;
            terminated = false;

            PluginConfig = new MimicPluginConfig();
            PluginLog = new LogFile(LogFormat.Simple)
            {
                FileName = Path.Combine(webContext.AppDirs.LogDir, EditorUtils.LogFileName),
                CapacityMB = webContext.AppConfig.GeneralOptions.MaxLogSize
            };
            ComponentSpecs = [];
            ModelMeta = new ModelMeta();
            ModelTranslation = new ModelTranslation();
            PageReferences = new PageReferences();
        }


        /// <summary>
        /// Gets the configuration of the mimic plugin.
        /// </summary>
        public MimicPluginConfig PluginConfig { get; }

        /// <summary>
        /// Gets the plugin log.
        /// </summary>
        public ILog PluginLog { get; }

        /// <summary>
        /// Gets the component library specifications.
        /// </summary>
        public List<IComponentSpec> ComponentSpecs { get; }

        /// <summary>
        /// Gets the information associated with the mimic model.
        /// </summary>
        public ModelMeta ModelMeta { get; }

        /// <summary>
        /// Gets the translation of the mimic model.
        /// </summary>
        public ModelTranslation ModelTranslation { get; }

        /// <summary>
        /// Gets the references to insert into a page that contains a mimic.
        /// </summary>
        public PageReferences PageReferences { get; }


        /// <summary>
        /// Loads the plugin configuration.
        /// </summary>
        private void LoadConfig()
        {
            if (!PluginConfig.Load(webContext.Storage, MimicPluginConfig.DefaultFileName, out string errMsg))
            {
                PluginLog.WriteError(errMsg);
                webContext.Log.WriteError(WebPhrases.PluginMessage, EditorPluginInfo.PluginCode, errMsg);
            }
        }

        /// <summary>
        /// Retrieves mimic components from the active plugins.
        /// </summary>
        private void RetrieveComponents()
        {
            ComponentSpecs.Clear();

            foreach (PluginLogic pluginLogic in webContext.PluginHolder.EnumeratePlugins())
            {
                if (pluginLogic is IComponentPlugin componentPlugin &&
                    componentPlugin.GetComponentSpec(true) is IComponentSpec componentSpec)
                {
                    ComponentSpecs.Add(componentSpec);
                }
            }
        }

        /// <summary>
        /// Fills in the information about the available components and subtypes from the active plugins.
        /// </summary>
        private void FillModelMeta()
        {
            ModelMeta.Clear();
            ModelMeta.ComponentGroups.Add(new StandardComponentGroup());
            ModelMeta.SubtypeGroups.Add(new StandardSubtypeGroup());

            foreach (IComponentSpec componentLibrary in ComponentSpecs)
            {
                if (componentLibrary.ComponentGroups is List<ComponentGroup> componentGroups)
                    ModelMeta.ComponentGroups.AddRange(componentGroups);

                if (componentLibrary.SubtypeGroups is List<SubtypeGroup> subtypeGroups)
                    ModelMeta.SubtypeGroups.AddRange(subtypeGroups);
            }

            ModelTranslation.Init(ModelMeta);
        }

        /// <summary>
        /// Fills in the page references based on the plugin configuration and available components.
        /// </summary>
        private void FillPageReferences()
        {
            PageReferences.Clear();
            PageReferences.AddConfigReferences(PluginConfig);
            PageReferences.AddComponentReferences(ComponentSpecs);
        }

        /// <summary>
        /// Adds the specified mimic to the editor.
        /// </summary>
        private MimicInstance AddMimic(string projectFileName, string mimicFileName, Mimic mimic)
        {
            MimicGroup mimicGroup;

            lock (mimicGroups)
            {
                if (!mimicGroups.TryGetValue(projectFileName, out mimicGroup))
                {
                    mimicGroup = new MimicGroup(projectFileName);
                    mimicGroups.Add(projectFileName, mimicGroup);
                }
            }

            MimicInstance mimicInstance = new(mimic)
            {
                FileName = mimicFileName,
                ParentGroup = mimicGroup
            };

            mimicByFileName.Add(mimicFileName, mimicInstance);
            mimicByKey.Add(mimicInstance.MimicKey, mimicInstance);
            mimicGroup.AddMimic(mimicInstance);
            return mimicInstance;
        }

        /// <summary>
        /// Reloads the faceplates of the specified mimic.
        /// </summary>
        private void ReloadFaceplates(MimicInstance mimicInstance)
        {
            PluginLog.WriteAction(Locale.IsRussian ?
                "Перезагрузка фейсплейтов для мнемосхемы {0}" :
                "Reload faceplates for mimic {0}", mimicInstance.FileName);
            string viewDir = EditorUtils.GetViewDir(mimicInstance.ParentGroup.ProjectFileName);
            LoadContext loadContext = new() { EditMode = true };
            mimicInstance.Mimic.ReloadFaceplates(viewDir, loadContext);

            if (loadContext.Errors.Count > 0)
            {
                PluginLog.WriteError(Locale.IsRussian ?
                    "Ошибка при перезагрузке фейсплейтов:{0}{1}" :
                    "Error reloading faceplates:{0}{1}",
                    Environment.NewLine, string.Join(Environment.NewLine, loadContext.Errors));
            }
        }

        /// <summary>
        /// Starts a process of cleaning up inactive mimics.
        /// </summary>
        private void StartCleanup()
        {
            if (cleanupThread == null)
            {
                PluginLog.WriteAction(Locale.IsRussian ?
                    "Запуск очистки неактивных мнемосхем" :
                    "Start cleanup inactive mimics");
                terminated = false;
                cleanupThread = new Thread(ExecuteCleanup) { Priority = ThreadPriority.BelowNormal };
                cleanupThread.Start();
            }
        }

        /// <summary>
        /// Stops the process of cleaning up inactive mimics.
        /// </summary>
        private void StopCleanupInternal()
        {
            if (cleanupThread != null)
            {
                // do not wait for the thread by calling Join()
                PluginLog.WriteAction(Locale.IsRussian ?
                    "Остановка очистки неактивных мнемосхем" :
                    "Stop cleanup inactive mimics");
                terminated = true;
                cleanupThread = null;
            }
        }

        /// <summary>
        /// Cleans up inactive mimics in a separate thread.
        /// </summary>
        private void ExecuteCleanup()
        {
            DateTime utcNow = DateTime.UtcNow;
            DateTime cleanupTime = utcNow;

            while (!terminated)
            {
                utcNow = DateTime.UtcNow;

                if (utcNow - cleanupTime >= CleanupPeriod)
                {
                    cleanupTime = utcNow;

                    if (CloseInactiveMimics())
                    {
                        StopCleanupInternal();
                        break;
                    }
                }

                Thread.Sleep(ScadaUtils.ThreadDelay);
            }
        }

        /// <summary>
        /// Closes inactive mimics. Returns true if there are no open mimics.
        /// </summary>
        private bool CloseInactiveMimics()
        {
            try
            {
                Monitor.Enter(editorLock);

                // find inactive mimics
                DateTime utcNow = DateTime.UtcNow;
                List<long> mimicsToClose = [];

                foreach (MimicInstance mimicInstance in mimicByKey.Values)
                {
                    if (utcNow - mimicInstance.ClientAccessTime > KeepInactiveMimics)
                        mimicsToClose.Add(mimicInstance.MimicKey);
                }

                // close found mimics
                foreach (long mimicKey in mimicsToClose)
                {
                    CloseMimic(mimicKey);
                }

                return mimicByKey.Count == 0;
            }
            catch (Exception ex)
            {
                PluginLog.WriteError(ex, Locale.IsRussian ?
                    "Ошибка при закрытии неактивных мнемосхем" :
                    "Error closing inactive mimics");
                return false;
            }
            finally
            {
                Monitor.Exit(editorLock);
            }
        }


        /// <summary>
        /// Initializes the editor manager.
        /// </summary>
        public void Init()
        {
            LoadConfig();
            RetrieveComponents();
            FillModelMeta();
            FillPageReferences();
        }

        /// <summary>
        /// Opens a mimic from the specified file.
        /// </summary>
        public OpenResult OpenMimic(string fileName)
        {
            try
            {
                Monitor.Enter(editorLock);

                // find already open mimic
                if (mimicByFileName.TryGetValue(fileName, out MimicInstance openMimicInstance))
                {
                    return new OpenResult
                    {
                        IsSuccessful = true,
                        MimicKey = openMimicInstance.MimicKey
                    };
                }

                // find project
                if (!EditorUtils.FindProject(fileName, out string projectFileName))
                    throw new ScadaException(EditorPhrases.ProjectNotFound);

                // load mimic
                Mimic mimic = new();
                LoadContext loadContext = new() { EditMode = true };
                mimic.Load(fileName, loadContext);

                // load faceplates
                string viewDir = EditorUtils.GetViewDir(projectFileName);
                mimic.LoadFaceplates(viewDir, loadContext);

                // add mimic to the editor
                StartCleanup();
                MimicInstance mimicInstance = AddMimic(projectFileName, fileName, mimic);

                // log results
                if (loadContext.Errors.Count == 0)
                {
                    PluginLog.WriteAction(Locale.IsRussian ?
                        "Загружена мнемосхема {0}" :
                        "Mimic loaded {0}", fileName);
                }
                else
                {
                    PluginLog.WriteAction(Locale.IsRussian ?
                        "Загружена мнемосхема {0} с ошибками:" :
                        "Mimic loaded {0} with errors:", fileName);
                    loadContext.Errors.ForEach(s => PluginLog.WriteLine(s));
                }

                return new OpenResult
                {
                    IsSuccessful = true,
                    MimicKey = mimicInstance.MimicKey
                };
            }
            catch (Exception ex)
            {
                string errMsg = ex.BuildErrorMessage(EditorPhrases.LoadMimicError);
                PluginLog.WriteError(errMsg);

                return new OpenResult
                {
                    IsSuccessful = false,
                    ErrorMessage = errMsg
                };
            }
            finally
            {
                Monitor.Exit(editorLock);
            }
        }

        /// <summary>
        /// Finds an open mimic by the key.
        /// </summary>
        public bool FindMimic(long mimicKey, out MimicInstance mimicInstance, out string errMsg)
        {
            lock (editorLock)
            {
                if (mimicByKey.TryGetValue(mimicKey, out mimicInstance))
                {
                    errMsg = "";
                    return true;
                }
                else
                {
                    mimicInstance = null;
                    errMsg = EditorPhrases.MimicNotFound;
                    return false;
                }
            }
        }

        /// <summary>
        /// Reloads the mimic that is already open.
        /// </summary>
        public bool ReloadMimic(long mimicKey, out string errMsg)
        {
            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out errMsg))
                return false;

            try
            {
                lock (mimicInstance.Mimic.SyncRoot)
                {
                    // clear mimic
                    Mimic mimic = mimicInstance.Mimic;
                    mimic.Clear();

                    // load mimic
                    LoadContext loadContext = new() { EditMode = true };
                    mimic.Load(mimicInstance.FileName, loadContext);

                    // load faceplates
                    string viewDir = EditorUtils.GetViewDir(mimicInstance.ParentGroup.ProjectFileName);
                    mimic.LoadFaceplates(viewDir, loadContext);

                    // log results
                    if (loadContext.Errors.Count == 0)
                    {
                        PluginLog.WriteAction(Locale.IsRussian ?
                            "Перезагружена мнемосхема {0}" :
                            "Mimic reloaded {0}", mimicInstance.FileName);
                    }
                    else
                    {
                        PluginLog.WriteAction(Locale.IsRussian ?
                            "Перезагружена мнемосхема {0} с ошибками:" :
                            "Mimic reloaded {0} with errors:", mimicInstance.FileName);
                        loadContext.Errors.ForEach(s => PluginLog.WriteLine(s));
                    }
                }

                errMsg = "";
                return true;
            }
            catch (Exception ex)
            {
                errMsg = ex.BuildErrorMessage(EditorPhrases.LoadMimicError);
                PluginLog.WriteError(errMsg);
                return false;
            }
        }

        /// <summary>
        /// Saves the mimic specified by the key.
        /// </summary>
        public bool SaveMimic(long mimicKey, out string errMsg)
        {
            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out errMsg))
                return false;

            try
            {
                BackupMimic(mimicInstance.FileName);

                lock (mimicInstance.Mimic.SyncRoot)
                {
                    using FileStream mimicStream =
                        new(mimicInstance.FileName, FileMode.Create, FileAccess.Write, FileShare.Read);
                    mimicInstance.Mimic.Save(mimicStream);
                }

                // The saved file is now the source of truth, the draft is obsolete.
                string autoSavePath = GetAutoSavePath(mimicInstance.FileName);
                if (File.Exists(autoSavePath))
                    File.Delete(autoSavePath);

                PluginLog.WriteAction(Locale.IsRussian ?
                    "Сохранена мнемосхема {0}" :
                    "Mimic saved {0}", mimicInstance.FileName);

                errMsg = "";
                return true;
            }
            catch (Exception ex)
            {
                errMsg = ex.BuildErrorMessage(EditorPhrases.SaveMimicError);
                PluginLog.WriteError(errMsg);
                return false;
            }
        }

        /// <summary>
        /// The maximum number of backups kept for a mimic.
        /// </summary>
        private const int MaxBackupCount = 20;

        /// <summary>
        /// Gets the auto-save file name of the specified mimic file.
        /// </summary>
        public static string GetAutoSavePath(string fileName)
        {
            return Path.ChangeExtension(fileName, ".autosave.mim");
        }

        /// <summary>
        /// Writes the in-memory mimic to the auto-save file without touching the original.
        /// </summary>
        public bool AutoSaveMimic(long mimicKey, out string errMsg)
        {
            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out errMsg))
                return false;

            try
            {
                string autoSavePath = GetAutoSavePath(mimicInstance.FileName);

                lock (mimicInstance.Mimic.SyncRoot)
                {
                    using FileStream stream = new(autoSavePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    mimicInstance.Mimic.Save(stream);
                }

                errMsg = "";
                return true;
            }
            catch (Exception ex)
            {
                errMsg = ex.BuildErrorMessage(EditorPhrases.SaveMimicError);
                PluginLog.WriteError(errMsg);
                return false;
            }
        }

        /// <summary>
        /// Gets the auto-save timestamp of the opened mimic, if a draft exists.
        /// </summary>
        public bool HasAutoSave(long mimicKey, out DateTime timestamp)
        {
            timestamp = DateTime.MinValue;

            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out _))
                return false;

            string autoSavePath = GetAutoSavePath(mimicInstance.FileName);

            if (!File.Exists(autoSavePath))
                return false;

            timestamp = File.GetLastWriteTime(autoSavePath);
            return true;
        }

        /// <summary>
        /// Backs up the original file, replaces it with the auto-saved draft and reopens the mimic.
        /// </summary>
        public bool RestoreAutoSave(long mimicKey, out long newKey, out string errMsg)
        {
            newKey = 0;

            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out errMsg))
                return false;

            string fileName = mimicInstance.FileName;
            string autoSavePath = GetAutoSavePath(fileName);

            try
            {
                if (!File.Exists(autoSavePath))
                {
                    errMsg = "没有可恢复的自动保存草稿。";
                    return false;
                }

                // The draft replaces the original, so keep a restore point in the backup folder.
                BackupMimic(fileName);
                File.Copy(autoSavePath, fileName, true);
                File.Delete(autoSavePath);
                CloseMimic(mimicKey);

                OpenResult result = OpenMimic(fileName);

                if (!result.IsSuccessful)
                {
                    errMsg = result.ErrorMessage;
                    return false;
                }

                newKey = result.MimicKey;
                errMsg = "";
                return true;
            }
            catch (Exception ex)
            {
                errMsg = ex.BuildErrorMessage(EditorPhrases.LoadMimicError);
                PluginLog.WriteError(errMsg);
                return false;
            }
        }

        /// <summary>
        /// Deletes the auto-saved draft of the opened mimic.
        /// </summary>
        public void DiscardAutoSave(long mimicKey)
        {
            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out _))
                return;

            try
            {
                string autoSavePath = GetAutoSavePath(mimicInstance.FileName);

                if (File.Exists(autoSavePath))
                    File.Delete(autoSavePath);
            }
            catch (Exception ex)
            {
                PluginLog.WriteError(ex.BuildErrorMessage(EditorPhrases.SaveMimicError));
            }
        }

        /// <summary>
        /// Gets the backup directory of the specified mimic file.
        /// </summary>
        private static string GetBackupDir(string fileName)
        {
            return Path.Combine(Path.GetDirectoryName(fileName) ?? "", ".backup",
                Path.GetFileNameWithoutExtension(fileName));
        }

        /// <summary>
        /// Copies the mimic file to the backup folder before it is overwritten.
        /// </summary>
        private void BackupMimic(string fileName)
        {
            try
            {
                if (!File.Exists(fileName))
                    return;

                string backupDir = GetBackupDir(fileName);
                Directory.CreateDirectory(backupDir);
                string backupName = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".mim";
                string backupFile = Path.Combine(backupDir, backupName);
                File.Copy(fileName, backupFile, true);
                File.SetLastWriteTimeUtc(backupFile, DateTime.UtcNow); // show the backup time, not the content time

                foreach (FileInfo file in new DirectoryInfo(backupDir).GetFiles("*.mim")
                    .OrderByDescending(f => f.Name).Skip(MaxBackupCount))
                {
                    file.Delete();
                }
            }
            catch (Exception ex)
            {
                // A failed backup must not block saving.
                PluginLog.WriteError(ex.BuildErrorMessage(EditorPhrases.SaveMimicError));
            }
        }

        /// <summary>
        /// Gets the number of backups of the specified mimic file and the time of the latest one.
        /// </summary>
        public (int Count, DateTime Latest) GetBackupInfo(string fileName)
        {
            try
            {
                DirectoryInfo dir = new(GetBackupDir(fileName));

                if (!dir.Exists)
                    return (0, DateTime.MinValue);

                FileInfo[] files = dir.GetFiles("*.mim");
                return (files.Length, files.Length == 0 ? DateTime.MinValue : files.Max(f => f.LastWriteTime));
            }
            catch
            {
                return (0, DateTime.MinValue);
            }
        }

        /// <summary>
        /// Restores the most recent backup of the mimic. The mimic has to be reopened afterwards.
        /// </summary>
        public bool RestoreLatestBackup(long mimicKey, out string errMsg)
        {
            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out errMsg))
                return false;

            try
            {
                DirectoryInfo dir = new(GetBackupDir(mimicInstance.FileName));
                FileInfo latest = dir.Exists
                    ? dir.GetFiles("*.mim").OrderByDescending(f => f.Name).FirstOrDefault()
                    : null;

                if (latest == null)
                {
                    errMsg = Locale.IsRussian ? "Резервные копии не найдены." : "没有可恢复的备份。";
                    return false;
                }

                File.Copy(latest.FullName, mimicInstance.FileName, true);
                PluginLog.WriteAction(Locale.IsRussian ?
                    "Восстановлена мнемосхема {0} из файла {1}" :
                    "Mimic restored {0} from {1}", mimicInstance.FileName, latest.Name);
                errMsg = "";
                return true;
            }
            catch (Exception ex)
            {
                errMsg = ex.BuildErrorMessage(EditorPhrases.SaveMimicError);
                PluginLog.WriteError(errMsg);
                return false;
            }
        }

        /// <summary>
        /// Saves the in-memory mimic to a new file, keeping the original untouched.
        /// </summary>
        public bool SaveMimicAs(long mimicKey, string targetFileName, out string errMsg)
        {
            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out errMsg))
                return false;

            try
            {
                if (System.IO.File.Exists(targetFileName))
                {
                    errMsg = Locale.IsRussian ? "Файл уже существует." : "目标文件已存在。";
                    return false;
                }

                lock (mimicInstance.Mimic.SyncRoot)
                {
                    using FileStream stream =
                        new(targetFileName, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    mimicInstance.Mimic.Save(stream);
                }

                PluginLog.WriteAction(Locale.IsRussian ?
                    "Мнемосхема сохранена как {0}" :
                    "Mimic saved as {0}", targetFileName);
                errMsg = "";
                return true;
            }
            catch (Exception ex)
            {
                errMsg = ex.BuildErrorMessage(EditorPhrases.SaveMimicError);
                PluginLog.WriteError(errMsg);
                return false;
            }
        }

        /// <summary>
        /// Closes the mimic specified by the key.
        /// </summary>
        public void CloseMimic(long mimicKey)
        {
            lock (editorLock)
            {
                if (mimicByKey.TryGetValue(mimicKey, out MimicInstance mimicInstance))
                {
                    // Closing is a deliberate decision, so the draft must not come back.
                    try
                    {
                        string autoSavePath = GetAutoSavePath(mimicInstance.FileName);
                        if (File.Exists(autoSavePath))
                            File.Delete(autoSavePath);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.WriteError(ex.BuildErrorMessage(EditorPhrases.SaveMimicError));
                    }

                    mimicByFileName.Remove(mimicInstance.FileName);
                    mimicByKey.Remove(mimicKey);

                    lock (mimicGroups)
                    {
                        MimicGroup mimicGroup = mimicInstance.ParentGroup;
                        mimicGroup.RemoveMimic(mimicInstance);

                        if (mimicGroup.IsEmpty)
                            mimicGroups.Remove(mimicGroup.Name);
                    }

                    PluginLog.WriteAction(Locale.IsRussian ?
                        "Закрыта мнемосхема {0}" :
                        "Mimic closed {0}", mimicInstance.FileName);
                }
            }
        }

        /// <summary>
        /// Updates the mimic specified by the key.
        /// </summary>
        public bool UpdateMimic(long mimicKey, IEnumerable<Change> changes, out string errMsg)
        {
            if (!FindMimic(mimicKey, out MimicInstance mimicInstance, out errMsg))
                return false;

            try
            {
                lock (mimicInstance.Mimic.SyncRoot)
                {
                    mimicInstance.RegisterClientActivity();
                    mimicInstance.Updater.ApplyChanges(changes);

                    if (mimicInstance.Updater.DependenciesChanged)
                        ReloadFaceplates(mimicInstance);
                }

                errMsg = "";
                return true;
            }
            catch (Exception ex)
            {
                errMsg = ex.BuildErrorMessage(EditorPhrases.UpdateMimicError);
                PluginLog.WriteError(errMsg);
                return false;
            }
        }

        /// <summary>
        /// Gets a snapshot containing the mimic groups.
        /// </summary>
        public MimicGroup[] GetMimicGroups()
        {
            lock (mimicGroups)
            {
                return [.. mimicGroups.Values.OrderBy(g => g.Name)];
            }
        }

        /// <summary>
        /// Stops the process of cleaning up inactive mimics.
        /// </summary>
        public void StopCleanup()
        {
            lock (editorLock)
            {
                StopCleanupInternal();
            }
        }
    }
}
