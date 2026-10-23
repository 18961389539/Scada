// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Scada.Data.Const;
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

        /// <summary>
        /// Represents a configuration problem found in a mimic file.
        /// </summary>
        public record MimicIssue(string Message, bool IsError);

        /// <summary>
        /// Properties that read a channel value.
        /// </summary>
        private static readonly string[] ReadChannelProperties =
        [
            "InCnlNum", "Channels", "FeedbackChannel", "ProcessChannel", "StatusChannel", "FaultChannel",
            "TripChannel", "EnergizedChannel", "ModeChannel", "PermitChannel", "OutputChannel"
        ];

        /// <summary>
        /// Properties that write a channel value, so the channel must be writable.
        /// </summary>
        private static readonly string[] WriteChannelProperties =
        [
            "OutCnlNum", "SetpointChannel", "ModeOutputChannel"
        ];

        private const int MaxIssuesPerFile = 12;
        private static readonly ConcurrentDictionary<string, (long Ticks, long Length, List<MimicIssue> Issues)> IssueCache =
            new(StringComparer.OrdinalIgnoreCase);

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public string ErrorMessage { get; private set; } = "";
        public MimicGroup[] MimicGroups { get; private set; } = [];
        public List<MimicFileItem> AvailableFiles { get; private set; } = [];
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

            if (!fullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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
            FileInfo fileInfo = new(fullName);

            if (IssueCache.TryGetValue(fullName, out var cached) &&
                cached.Ticks == fileInfo.LastWriteTimeUtc.Ticks &&
                cached.Length == fileInfo.Length)
            {
                return cached.Issues;
            }

            List<MimicIssue> issues = [];

            try
            {
                AddMimicIssues(XDocument.Load(fullName), issues);
            }
            catch (Exception ex)
            {
                issues.Add(new MimicIssue(string.Format("文件无法解析：{0}", ex.Message), true));
            }

            if (issues.Count > MaxIssuesPerFile)
            {
                int hiddenCount = issues.Count - MaxIssuesPerFile;
                issues = issues.Take(MaxIssuesPerFile).ToList();
                issues.Add(new MimicIssue(string.Format("还有 {0} 条问题未显示", hiddenCount), false));
            }

            IssueCache[fullName] = (fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length, issues);
            return issues;
        }

        /// <summary>
        /// Adds the issues found in a parsed mimic document.
        /// </summary>
        private void AddMimicIssues(XDocument doc, List<MimicIssue> issues)
        {
            XElement root = doc.Root;

            if (root == null)
            {
                issues.Add(new MimicIssue("画面内容为空", true));
                return;
            }

            XElement sizeElem = root.Element("Document")?.Element("Size");
            if (int.TryParse(sizeElem?.Element("Width")?.Value, out int width) &&
                int.TryParse(sizeElem?.Element("Height")?.Value, out int height) &&
                (width <= 0 || height <= 0 || width > 20000 || height > 20000))
            {
                issues.Add(new MimicIssue(string.Format("画面尺寸异常：{0} × {1}", width, height), true));
            }

            XElement componentsElem = root.Element("Components");
            if (componentsElem == null)
                return;

            foreach (XElement componentElem in componentsElem.Elements())
            {
                string typeName = componentElem.Name.LocalName;
                string componentName = componentElem.Element("Name")?.Value ?? "";
                string label = componentElem.Element("Label")?.Value ?? "";
                bool hasInput = false;

                foreach (XElement propertyElem in componentElem.Elements())
                {
                    string propertyName = propertyElem.Name.LocalName;
                    bool isRead = ReadChannelProperties.Contains(propertyName);
                    bool isWrite = WriteChannelProperties.Contains(propertyName);

                    if (!isRead && !isWrite)
                        continue;

                    if (propertyName == "InCnlNum")
                        hasInput = true;

                    string rawValue = propertyElem.Value.Trim();
                    if (rawValue.Length == 0)
                        continue;

                    if (!TryParseChannelNumbers(rawValue, propertyName == "Channels", out List<int> cnlNums))
                    {
                        issues.Add(new MimicIssue(string.Format("{0}：{1} 的值 \"{2}\" 不是有效的通道号",
                            DescribeComponent(typeName, componentName), propertyName, rawValue), true));
                        continue;
                    }

                    foreach (int cnlNum in cnlNums)
                    {
                        if (cnlNum <= 0)
                            continue;

                        if (!webContext.ConfigDatabase.CnlTable.PkExists(cnlNum))
                        {
                            issues.Add(new MimicIssue(string.Format("{0}：{1} 引用的通道 {2} 不存在",
                                DescribeComponent(typeName, componentName), propertyName, cnlNum), true));
                        }
                        else if (isWrite &&
                            !CnlTypeID.IsOutput(webContext.ConfigDatabase.CnlTable.GetItem(cnlNum).CnlTypeID))
                        {
                            issues.Add(new MimicIssue(string.Format("{0}：通道 {1} 为只读，无法下发命令",
                                DescribeComponent(typeName, componentName), cnlNum), true));
                        }
                    }
                }

                if (hasInput && componentElem.Element("Label") != null && string.IsNullOrWhiteSpace(label))
                {
                    issues.Add(new MimicIssue(string.Format("{0}：绑定了通道但标签为空，运行页看不出含义",
                        DescribeComponent(typeName, componentName)), false));
                }
            }
        }

        private static string DescribeComponent(string typeName, string componentName)
        {
            return string.IsNullOrEmpty(componentName) ? typeName : string.Format("{0}（{1}）", componentName, typeName);
        }

        /// <summary>
        /// Parses a single channel number or a comma separated channel list.
        /// </summary>
        private static bool TryParseChannelNumbers(string value, bool isList, out List<int> cnlNums)
        {
            cnlNums = [];

            if (!isList)
            {
                if (int.TryParse(value, out int cnlNum))
                {
                    cnlNums.Add(cnlNum);
                    return true;
                }

                return false;
            }

            foreach (string part in value.Split([',', ';', ' ', '，', '；'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, out int cnlNum))
                    return false;

                cnlNums.Add(cnlNum);
            }

            return true;
        }

        /// <summary>
        /// Scans the views directory so the editor does not require a typed file name.
        /// </summary>
        private void LoadAvailableFiles()
        {
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

                AvailableFiles = viewsDir.EnumerateFiles("*.mim", SearchOption.AllDirectories)
                    .Where(file => !IsInternalPath(file.FullName))
                    .OrderByDescending(file => file.LastWriteTime)
                    .Take(200)
                    .Select(file =>
                    {
                        string relativePath = Path.GetRelativePath(viewsDir.FullName, file.FullName).Replace('/', '\\');
                        bool bound = viewByPath.TryGetValue(relativePath, out var viewInfo);
                        return new MimicFileItem(
                            relativePath,
                            file.Length,
                            file.LastWriteTime,
                            bound ? viewInfo.ViewID : 0,
                            bound ? viewInfo.Title : "",
                            InspectMimicFile(file.FullName));
                    })
                    .ToList();
            }
            catch
            {
                AvailableFiles = [];
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
                IssueCache.TryRemove(oldFullName, out _);
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
                IssueCache.TryRemove(fullName, out _);

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

        public IActionResult OnPostOpen()
        {            OpenResult result = editorManager.OpenMimic(FileName);

            if (result.IsSuccessful)
            {
                return RedirectToPage(); // Post-Redirect-Get pattern
            }
            else
            {
                ErrorMessage = result.ErrorMessage;
                MimicGroups = editorManager.GetMimicGroups();
                return Page();
            }
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
