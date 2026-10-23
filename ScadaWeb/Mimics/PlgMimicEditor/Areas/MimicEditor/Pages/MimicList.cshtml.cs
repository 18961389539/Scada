// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Scada.Web.Authorization;
using Scada.Web.Plugins.PlgMimicEditor.Code;
using Scada.Web.Services;

namespace Scada.Web.Plugins.PlgMimicEditor.Areas.MimicEditor.Pages
{
    /// <summary>
    /// Represents a page for displaying open mimic diagrams.
    /// <para>������������ �������� ��� ����������� �������� ���������.</para>
    /// </summary>
    [Authorize(Policy = PolicyName.Administrators)]
    public class MimicListModel(EditorManager editorManager, IWebContext webContext) : PageModel
    {
        /// <summary>
        /// Represents a mimic file available for editing.
        /// </summary>
        public record MimicFileItem(string RelativePath, long Size, DateTime Modified, int ViewID, string ViewTitle,
            List<MimicIssue> Issues)
        {
            public bool HasErrors => Issues.Exists(issue => issue.IsError);
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public string ErrorMessage { get; private set; } = "";
        public MimicGroup[] MimicGroups { get; private set; } = [];
        public List<MimicFileItem> AvailableFiles { get; private set; } = [];
        public const int FilesPerPage = 50;
        public int TotalAvailableFiles { get; private set; }
        public int MatchedFileCount { get; private set; }
        public int FilePageCount { get; private set; } = 1;
        public string FileListError { get; private set; } = "";

        [BindProperty(SupportsGet = true)]
        public string FileQuery { get; set; } = "";

        [BindProperty(SupportsGet = true)]
        public string FileSort { get; set; } = "modified";

        [BindProperty(SupportsGet = true)]
        public int FilePage { get; set; } = 1;
        public string RestoreMessage { get; private set; } = "";
        public bool RestoreFailed { get; private set; } = false;

        [BindProperty]
        public string FileName { get; set; }

        [BindProperty]
        public string NewFileName { get; set; }

        public string CreateMessage { get; private set; } = "";
        public bool CreateFailed { get; private set; } = false;

        /// <summary>
        /// Empty mimic written when a new view is created.
        /// </summary>
        private const string MimicTemplate = """
            <?xml version='1.0' encoding='utf-8'?>
            <Mimic editorVersion="6.0.2.2">
              <Dependencies />
              <Document>
                <BackColor>#eef2f7</BackColor>
                <Font>
                  <Name>Microsoft YaHei</Name>
                  <Size>14</Size>
                </Font>
                <ForeColor>#213047</ForeColor>
                <Size>
                  <Width>1400</Width>
                  <Height>900</Height>
                </Size>
              </Document>
              <Components />
              <Images />
            </Mimic>
            """;


        /// <summary>
        /// Gets the directory that stores the views.
        /// </summary>
        private static string GetViewsDir()
        {
            string instanceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            return Path.Combine(instanceDir, "Views");
        }

        /// <summary>
        /// Checks whether the path belongs to an internal folder that must stay out of the list.
        /// </summary>
        private static bool IsInternalPath(string fullName)
        {
            return fullName.Contains($"{Path.DirectorySeparatorChar}.backup{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains($"{Path.DirectorySeparatorChar}.trash{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase) ||
                   fullName.EndsWith(".autosave.mim", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a relative path typed by the user and keeps it inside the views directory.
        /// </summary>
        private string ResolveMimicFile(string relativePath)
        {
            string path = (relativePath ?? "").Trim();

            if (path.Length == 0 || path.Contains("..") || IsInternalPath(path))
                throw new InvalidOperationException("无效的画面文件。");

            string viewsDir = GetViewsDir();
            string fullName = Path.GetFullPath(Path.Combine(viewsDir, path.Replace('\\', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(viewsDir) + Path.DirectorySeparatorChar;

            if (!fullName.StartsWith(root, StringComparison.OrdinalIgnoreCase) || IsInternalPath(fullName) ||
                !string.Equals(Path.GetExtension(fullName), ".mim", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("无效的画面文件。");

            if (!System.IO.File.Exists(fullName))
                throw new InvalidOperationException(string.Format("画面不存在：{0}", path));

            return fullName;
        }

        /// <summary>
        /// Finds the view that displays the specified mimic file.
        /// </summary>
        private bool TryGetBoundView(string relativePath, out int viewID, out string viewTitle)
        {
            viewID = 0;
            viewTitle = "";

            foreach (var view in webContext.ConfigDatabase.ViewTable.Items.Values)
            {
                if (!string.IsNullOrEmpty(view.Path) &&
                    string.Equals(view.Path.Replace('/', '\\'), relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    viewID = view.ViewID;
                    viewTitle = view.Title;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether the file is currently open in the editor.
        /// </summary>
        private bool IsOpenInEditor(string fullName)
        {
            foreach (MimicGroup group in editorManager.GetMimicGroups())
            {
                foreach (MimicInstance instance in group.GetMimics())
                {
                    if (string.Equals(instance.FileName, fullName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks the channels and the document size referenced by a mimic file.
        /// </summary>
        private List<MimicIssue> InspectMimicFile(string fullName)
        {
            try
            {
                return MimicInspector.Inspect(XDocument.Load(fullName),
                    cnlNum => webContext.ConfigDatabase.CnlTable.GetItem(cnlNum)?.CnlTypeID);
            }
            catch (Exception ex)
            {
                return [new MimicIssue(string.Format("文件无法解析：{0}", ex.Message), true)];
            }
        }

        /// <summary>
        /// Scans the views directory so the editor does not require a typed file name.
        /// </summary>
        private void LoadAvailableFiles()
        {
            FileQuery = (FileQuery ?? "").Trim();
            FileSort = FileSort == "name" ? "name" : "modified";
            try
            {
                DirectoryInfo viewsDir = new(GetViewsDir());
                if (!viewsDir.Exists)
                    return;

                Dictionary<string, (int ViewID, string Title)> viewByPath = new(StringComparer.OrdinalIgnoreCase);
                foreach (var view in webContext.ConfigDatabase.ViewTable.Items.Values)
                {
                    if (!string.IsNullOrEmpty(view.Path))
                        viewByPath[view.Path.Replace('/', '\\')] = (view.ViewID, view.Title);
                }

                var files = viewsDir.EnumerateFiles("*.mim", SearchOption.AllDirectories)
                    .Where(file => !IsInternalPath(file.FullName))
                    .Select(file =>
                    {
                        string relativePath = Path.GetRelativePath(viewsDir.FullName, file.FullName).Replace('/', '\\');
                        bool bound = viewByPath.TryGetValue(relativePath, out var viewInfo);
                        return new { File = file, RelativePath = relativePath,
                            ViewID = bound ? viewInfo.ViewID : 0, ViewTitle = bound ? viewInfo.Title : "" };
                    })
                    .ToList();
                TotalAvailableFiles = files.Count;
                string[] terms = FileQuery.Replace('/', '\\').Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                var matches = files.Where(file => terms.All(term =>
                    ($"{file.RelativePath} {file.ViewTitle}").Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
                MatchedFileCount = matches.Count;
                FilePageCount = Math.Max(1, (MatchedFileCount + FilesPerPage - 1) / FilesPerPage);
                FilePage = Math.Clamp(FilePage, 1, FilePageCount);
                var sorted = FileSort == "name"
                    ? matches.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                    : matches.OrderByDescending(file => file.File.LastWriteTimeUtc)
                        .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
                AvailableFiles = sorted.Skip((FilePage - 1) * FilesPerPage).Take(FilesPerPage)
                    .Select(file => new MimicFileItem(file.RelativePath, file.File.Length, file.File.LastWriteTime,
                        file.ViewID, file.ViewTitle, InspectMimicFile(file.File.FullName))).ToList();
            }
            catch (Exception ex)
            {
                AvailableFiles = [];
                FileListError = ex.Message;
            }
        }

        /// <summary>
        /// Gets the backup statistics of the specified mimic file.
        /// </summary>
        public (int Count, DateTime Latest) GetBackupInfo(string fileName)
        {
            return editorManager.GetBackupInfo(fileName);
        }

        /// <summary>
        /// Validates a file name typed by the user and appends the .mim extension.
        /// </summary>
        private static string NormalizeFileName(string fileName)
        {
            string name = (fileName ?? "").Trim();

            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("请输入画面文件名。");

            if (!name.EndsWith(".mim", StringComparison.OrdinalIgnoreCase))
                name += ".mim";

            if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
                throw new InvalidOperationException("只填写文件名，不支持子目录或路径。");

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("名称包含非法字符。");

            if (name.EndsWith(".autosave.mim", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(".autosave.mim 是自动保存草稿的保留名称，请使用其他文件名。");

            return name;
        }

        public void OnGet()
        {
            MimicGroups = editorManager.GetMimicGroups();
            LoadAvailableFiles();
        }

        /// <summary>
        /// Creates an empty mimic in the views directory and opens it in the editor.
        /// </summary>
        public IActionResult OnPostCreate()
        {
            try
            {
                string name = NormalizeFileName(NewFileName);

                string viewsDir = GetViewsDir();
                Directory.CreateDirectory(viewsDir);
                string fullName = Path.Combine(viewsDir, name);

                if (System.IO.File.Exists(fullName))
                    throw new InvalidOperationException($"画面已存在：{name}");

                System.IO.File.WriteAllText(fullName, MimicTemplate, new UTF8Encoding(false));
                OpenResult result = editorManager.OpenMimic(fullName);

                if (result.IsSuccessful)
                    return RedirectToPage("MimicEdit", new { key = result.MimicKey });

                CreateFailed = true;
                CreateMessage = result.ErrorMessage;
            }
            catch (Exception ex)
            {
                CreateFailed = true;
                CreateMessage = ex.Message;
            }

            MimicGroups = editorManager.GetMimicGroups();
            LoadAvailableFiles();
            return Page();
        }

        /// <summary>
        /// Saves the open mimic under a new name and opens the copy.
        /// </summary>
        public IActionResult OnPostSaveAs(long mimicKey, string NewFileName)
        {
            try
            {
                string name = NormalizeFileName(NewFileName);
                string target = Path.Combine(GetViewsDir(), name);

                if (!editorManager.SaveMimicAs(mimicKey, target, out string saveError))
                    throw new InvalidOperationException(saveError);

                OpenResult result = editorManager.OpenMimic(target);

                if (result.IsSuccessful)
                    return RedirectToPage("MimicEdit", new { key = result.MimicKey });

                throw new InvalidOperationException(result.ErrorMessage);
            }
            catch (Exception ex)
            {
                CreateFailed = true;
                CreateMessage = ex.Message;
            }

            MimicGroups = editorManager.GetMimicGroups();
            LoadAvailableFiles();
            return Page();
        }

        public IActionResult OnPostRestore(long mimicKey)
        {
            if (editorManager.RestoreLatestBackup(mimicKey, out string errMsg))
            {
                // The in-memory copy still holds the previous content, so force a reopen.
                editorManager.CloseMimic(mimicKey);
                RestoreMessage = "已恢复最新备份，画面已关闭，请重新打开查看。";
            }
            else
            {
                RestoreFailed = true;
                RestoreMessage = errMsg;
            }

            MimicGroups = editorManager.GetMimicGroups();
            LoadAvailableFiles();
            return Page();
        }

        /// <summary>
        /// Renames a mimic file that is neither bound to a view nor open in the editor.
        /// </summary>
        public IActionResult OnPostRename()
        {
            try
            {
                string oldFullName = ResolveMimicFile(FileName);
                string relativePath = Path.GetRelativePath(GetViewsDir(), oldFullName).Replace('/', '\\');

                if (TryGetBoundView(relativePath, out int viewID, out string viewTitle))
                {
                    throw new InvalidOperationException(string.Format(
                        "该画面绑定在视图 {0}（{1}）上，重命名会让视图失效。请先在 ScadaAdmin 里解除绑定。",
                        viewID, viewTitle));
                }

                if (IsOpenInEditor(oldFullName))
                    throw new InvalidOperationException("画面正在编辑器中打开，请先关闭再重命名。");

                string name = NormalizeFileName(NewFileName);
                string newFullName = Path.Combine(Path.GetDirectoryName(oldFullName) ?? GetViewsDir(), name);

                if (System.IO.File.Exists(newFullName))
                    throw new InvalidOperationException(string.Format("文件已存在：{0}", name));

                System.IO.File.Move(oldFullName, newFullName);
                CreateMessage = string.Format("已重命名为 {0}", name);
            }
            catch (Exception ex)
            {
                CreateFailed = true;
                CreateMessage = ex.Message;
            }

            MimicGroups = editorManager.GetMimicGroups();
            LoadAvailableFiles();
            return Page();
        }

        /// <summary>
        /// Moves a mimic file to the trash folder, so the deletion stays reversible.
        /// </summary>
        public IActionResult OnPostDelete()
        {
            try
            {
                string fullName = ResolveMimicFile(FileName);
                string relativePath = Path.GetRelativePath(GetViewsDir(), fullName).Replace('/', '\\');

                if (TryGetBoundView(relativePath, out int viewID, out string viewTitle))
                {
                    throw new InvalidOperationException(string.Format(
                        "该画面绑定在视图 {0}（{1}）上，删除会让视图失效。请先在 ScadaAdmin 里解除绑定。",
                        viewID, viewTitle));
                }

                if (IsOpenInEditor(fullName))
                    throw new InvalidOperationException("画面正在编辑器中打开，请先关闭再删除。");

                string trashDir = Path.Combine(GetViewsDir(), ".trash");
                Directory.CreateDirectory(trashDir);
                string target = Path.Combine(trashDir,
                    string.Format("{0:yyyyMMdd-HHmmss}-{1}", DateTime.Now, Path.GetFileName(fullName)));
                System.IO.File.Move(fullName, target);

                CreateMessage = string.Format("已删除，文件移到 Views\\.trash\\{0}，可手动恢复。", Path.GetFileName(target));
            }
            catch (Exception ex)
            {
                CreateFailed = true;
                CreateMessage = ex.Message;
            }

            MimicGroups = editorManager.GetMimicGroups();
            LoadAvailableFiles();
            return Page();
        }

        /// <summary>
        /// Copies the saved source to a new file, then opens the copy without modifying the source.
        /// </summary>
        public IActionResult OnPostDuplicate()
        {
            try
            {
                string source = ResolveMimicFile(FileName);
                string name = NormalizeFileName(NewFileName);
                string target = Path.Combine(Path.GetDirectoryName(source) ?? GetViewsDir(), name);
                // File.Copy fails if the target already exists, including the source itself.
                System.IO.File.Copy(source, target, false);
                OpenResult copy = editorManager.OpenMimic(target);
                if (copy.IsSuccessful)
                    return RedirectToPage("MimicEdit", new { key = copy.MimicKey });

                throw new InvalidOperationException(copy.ErrorMessage);
            }
            catch (Exception ex)
            {
                CreateFailed = true;
                CreateMessage = ex.Message;
            }

            MimicGroups = editorManager.GetMimicGroups();
            LoadAvailableFiles();
            return Page();
        }

        public IActionResult OnPostOpen(int? componentID)
        {
            try
            {
                string name = (FileName ?? "").Trim();
                // List rows store paths relative to Views; absolute paths entered manually
                // are still resolved by the editor against its registered SCADA projects.
                string fullName = Path.IsPathRooted(name) ? name : ResolveMimicFile(name);
                OpenResult result = editorManager.OpenMimic(fullName);
                if (result.IsSuccessful)
                    return RedirectToPage("MimicEdit", new { key = result.MimicKey, componentID });

                ErrorMessage = result.ErrorMessage;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }

            MimicGroups = editorManager.GetMimicGroups();
            LoadAvailableFiles();
            return Page();
        }

        public IActionResult OnPostSave(long mimicKey)
        {
            if (editorManager.SaveMimic(mimicKey, out string errMsg))
            {
                return RedirectToPage();
            }
            else
            {
                ErrorMessage = errMsg;
                MimicGroups = editorManager.GetMimicGroups();
                LoadAvailableFiles();
                return Page();
            }
        }

        public IActionResult OnPostSaveAndClose(long mimicKey)
        {
            if (editorManager.SaveMimic(mimicKey, out string errMsg))
            {
                editorManager.CloseMimic(mimicKey);
                return RedirectToPage();
            }
            else
            {
                ErrorMessage = errMsg;
                MimicGroups = editorManager.GetMimicGroups();
                LoadAvailableFiles();
                return Page();
            }
        }

        public IActionResult OnPostCloseWithoutSaving(long mimicKey)
        {
            editorManager.CloseMimic(mimicKey);
            return RedirectToPage();
        }
    }
}
